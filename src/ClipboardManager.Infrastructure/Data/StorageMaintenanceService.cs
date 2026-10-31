using System.IO.Compression;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Data;

public sealed class StorageMaintenanceService(
    AppPaths paths,
    SqliteConnectionFactory connectionFactory,
    ILogger<StorageMaintenanceService> logger) : IStorageMaintenanceService
{
    private const int BackupFormatVersion = 1;
    private const string DatabaseEntry = "clipboard.db";
    private const string ManifestEntry = "manifest.json";
    internal int MaxArchiveEntries { get; set; } = 100_000;
    internal long MaxEntryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    internal long MaxTotalBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    internal long MaxManifestBytes { get; set; } = 64 * 1024;

    public Task<StorageUsageStats> GetUsageStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (blobBytes, blobCount) = MeasureDirectory(paths.BlobsPath, cancellationToken);
        var (thumbnailBytes, thumbnailCount) = MeasureDirectory(paths.ThumbnailsPath, cancellationToken);
        return Task.FromResult(new StorageUsageStats(
            FileSize(paths.DatabasePath), blobBytes, thumbnailBytes, blobCount, thumbnailCount));
    }

    public async Task CreateBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        paths.EnsureCreated();
        var fullBackupPath = Path.GetFullPath(backupPath);
        var parent = Path.GetDirectoryName(fullBackupPath) ?? throw new ArgumentException("Backup path has no parent directory.", nameof(backupPath));
        Directory.CreateDirectory(parent);
        var workingRoot = Path.Combine(paths.Root, ".backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingRoot);
        var snapshotPath = Path.Combine(workingRoot, DatabaseEntry);
        var temporaryArchive = Path.Combine(parent, "." + Path.GetFileName(fullBackupPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CreateDatabaseSnapshotAsync(snapshotPath, cancellationToken);
            var schemaVersion = await ValidateDatabaseAsync(snapshotPath, cancellationToken);
            await ValidateCandidateAsync(snapshotPath, schemaVersion, cancellationToken);
            await ValidateMetadataAsync(paths.Root, cancellationToken);
            var manifest = new BackupManifest(BackupFormatVersion, schemaVersion, DateTimeOffset.UtcNow,
                Path.GetFullPath(paths.Root), ImportsIncluded: true);
            await using (var stream = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                AddFile(archive, snapshotPath, DatabaseEntry);
                AddOptionalFile(archive, paths.SettingsPath, "settings.json");
                AddOptionalFile(archive, paths.DevicePath, "device.json");
                AddDirectory(archive, paths.BlobsPath, "blobs", temporaryArchive, fullBackupPath, cancellationToken);
                AddDirectory(archive, paths.ThumbnailsPath, "thumbnails", temporaryArchive, fullBackupPath, cancellationToken);
                AddDirectory(archive, paths.ImportsPath, "imports", temporaryArchive, fullBackupPath, cancellationToken);
                var entry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var output = entry.Open();
                await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: cancellationToken);
            }
            File.Move(temporaryArchive, fullBackupPath, true);
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
            TryDeleteDirectory(workingRoot);
        }
    }

    public async Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var validated = await ValidateAndExtractAsync(backupPath, cancellationToken);
            TryDeleteDirectory(validated.ExtractedRoot);
            return new BackupValidationResult(true, null, validated.Manifest.SchemaVersion, validated.Manifest.CreatedAtUtc);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or SqliteException or UnauthorizedAccessException or ArgumentException)
        {
            return new BackupValidationResult(false, ex.Message, null, null);
        }
    }

    public async Task<RestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var validated = await ValidateAndExtractAsync(backupPath, cancellationToken);
        var rollbackRoot = Path.Combine(paths.Root, ".restore-rollback-" + Guid.NewGuid().ToString("N"));
        var retainRollback = false;
        try
        {
            Directory.CreateDirectory(rollbackRoot);
            await SnapshotLiveStateAsync(rollbackRoot, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplaceLiveState(validated.ExtractedRoot, validated.HasImports, cancellationToken);
                await ValidateDatabaseAsync(paths.DatabasePath, cancellationToken);
                await VerifyStartupAsync(paths.DatabasePath, cancellationToken);
                await ValidateMetadataAsync(paths.Root, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception restoreError)
            {
                try { RestoreRollbackState(rollbackRoot); }
                catch (Exception rollbackError)
                {
                    retainRollback = true;
                    logger.LogError(rollbackError, "Restore rollback failed; recovery snapshot retained at {RollbackRoot}.", rollbackRoot);
                    throw new AggregateException("Restore failed. Recovery snapshot retained at " + rollbackRoot, restoreError, rollbackError);
                }
                throw;
            }

            return new RestoreResult(DateTimeOffset.UtcNow, validated.Manifest.SchemaVersion);
        }
        finally
        {
            TryDeleteDirectory(validated.ExtractedRoot);
            if (!retainRollback) TryDeleteDirectory(rollbackRoot);
        }
    }

    public async Task<AssetCleanupPreview> PreviewOrphanedAssetsAsync(CancellationToken cancellationToken = default)
    {
        var referenced = await GetReferencedManagedFilesAsync(cancellationToken);
        var candidates = new List<string>();
        long bytes = 0;
        foreach (var (root, prefix) in new[] { (paths.BlobsPath, "blobs"), (paths.ThumbnailsPath, "thumbnails") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file);
                if (referenced.Contains(full)) continue;
                candidates.Add(prefix + "/" + Path.GetRelativePath(root, full).Replace('\\', '/'));
                bytes += new FileInfo(full).Length;
            }
        }
        candidates.Sort(StringComparer.Ordinal);
        return new AssetCleanupPreview(candidates, bytes);
    }

    public async Task<AssetCleanupResult> CleanupOrphanedAssetsAsync(AssetCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var current = await PreviewOrphanedAssetsAsync(cancellationToken);
        var allowed = current.RelativePaths.ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();
        var count = 0;
        long bytes = 0;
        foreach (var relative in preview.RelativePaths.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowed.Contains(relative) || !TryResolveManagedRelativePath(relative, out var fullPath))
            {
                failures.Add(relative);
                continue;
            }
            try
            {
                var length = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                File.Delete(fullPath);
                count++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Failed to delete orphaned managed asset {RelativePath}.", relative);
                failures.Add(relative);
            }
        }
        return new AssetCleanupResult(count, bytes, failures);
    }

    private async Task CreateDatabaseSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = connectionFactory.Create();
        await source.OpenAsync(cancellationToken);
        var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private async Task<ValidatedBackup> ValidateAndExtractAsync(string backupPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullPath = Path.GetFullPath(backupPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Backup archive was not found.", fullPath);
        var extractedRoot = Path.Combine(Path.GetTempPath(), "ClipboardManager.Restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedRoot);
        try
        {
            BackupManifest? manifest = null;
            using var archive = ZipFile.OpenRead(fullPath);
            if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("Backup has too many entries.");
            long totalBytes = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(normalized)) throw new InvalidDataException("Empty archive entry.");
                if (normalized.EndsWith('/'))
                {
                    if (!IsAllowedEntry(normalized + "_")) throw new InvalidDataException("Unsafe archive directory.");
                    GetContainedPath(extractedRoot, normalized.TrimEnd('/'));
                    continue;
                }
                if (!seen.Add(normalized)) throw new InvalidDataException($"Duplicate archive entry: {normalized}");
                if (!IsAllowedEntry(normalized)) throw new InvalidDataException($"Unsupported or unsafe archive entry: {normalized}");
                var limit = normalized == ManifestEntry ? Math.Min(MaxEntryBytes, MaxManifestBytes) : MaxEntryBytes;
                if (entry.Length > limit || entry.Length > MaxTotalBytes - totalBytes)
                    throw new InvalidDataException("Backup exceeds extraction limits.");
                var destination = GetContainedPath(extractedRoot, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long entryBytes = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (read > limit - entryBytes || read > MaxTotalBytes - totalBytes)
                        throw new InvalidDataException("Backup exceeds extraction limits.");
                    entryBytes += read;
                    totalBytes += read;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (entryBytes != entry.Length) throw new InvalidDataException("Archive entry length mismatch.");
            }
            var manifestPath = Path.Combine(extractedRoot, ManifestEntry);
            if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(extractedRoot, DatabaseEntry)))
                throw new InvalidDataException("Backup is missing its manifest or database snapshot.");
            await using (var input = File.OpenRead(manifestPath))
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, cancellationToken: cancellationToken);
            if (manifest is null || manifest.BackupFormatVersion != BackupFormatVersion)
                throw new InvalidDataException("Unsupported backup format version.");
            var actualSchema = await ValidateDatabaseAsync(Path.Combine(extractedRoot, DatabaseEntry), cancellationToken);
            if (actualSchema != manifest.SchemaVersion)
                throw new InvalidDataException("Backup manifest schema version does not match the database.");
            await ValidateMetadataAsync(extractedRoot, cancellationToken);
            await ValidateCandidateAsync(Path.Combine(extractedRoot, DatabaseEntry), actualSchema, cancellationToken);
            await RebaseManagedPathsAsync(extractedRoot, manifest.SourceRoot, cancellationToken);
            var hasImports = manifest.ImportsIncluded || seen.Any(x => x.StartsWith("imports/", StringComparison.Ordinal));
            return new ValidatedBackup(
                extractedRoot,
                manifest with { SchemaVersion = SqliteDatabaseInitializer.CurrentVersion },
                hasImports);
        }
        catch
        {
            TryDeleteDirectory(extractedRoot);
            throw;
        }
    }

    private static bool IsAllowedEntry(string entry) =>
        entry is DatabaseEntry or ManifestEntry or "settings.json" or "device.json" ||
        entry.StartsWith("blobs/", StringComparison.Ordinal) || entry.StartsWith("thumbnails/", StringComparison.Ordinal) ||
        entry.StartsWith("imports/", StringComparison.Ordinal);

    private static string GetContainedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Archive entry is rooted.");
        if (relative.Split('/').Any(part => part is "" or "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe archive entry name.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive entry escapes the restore directory.");
        return full;
    }

    private static async Task<int> ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite integrity check failed.");
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (version < 1 || version > SqliteDatabaseInitializer.CurrentVersion)
                throw new InvalidDataException($"Unsupported SQLite schema version {version}.");
            return version;
        }
    }

    private static async Task ValidateMetadataAsync(string root, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var settingsPath = Path.Combine(root, "settings.json");
        if (File.Exists(settingsPath))
        {
            await using var input = File.OpenRead(settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(input, options, cancellationToken)
                ?? throw new InvalidDataException("Backup settings are empty.");
            AppSettingsValidator.Validate(settings);
        }
        var devicePath = Path.Combine(root, "device.json");
        if (File.Exists(devicePath))
        {
            await using var input = File.OpenRead(devicePath);
            var device = await JsonSerializer.DeserializeAsync<DeviceIdentity>(input, options, cancellationToken);
            if (device is null || device.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceName) ||
                string.IsNullOrWhiteSpace(device.Platform) || device.CreatedAt == default)
                throw new InvalidDataException("Backup device identity is invalid.");
        }
    }

    private static SqliteConnection OpenCandidate(string databasePath, bool writable = false) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false, ForeignKeys = true
        }.ConnectionString);

    private static async Task ValidateCandidateAsync(string databasePath, int version, CancellationToken cancellationToken)
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), "ClipboardManager.Schema", Guid.NewGuid().ToString("N"));
        var expectedPaths = new AppPaths(expectedRoot);
        var candidatePaths = new AppPaths(Path.GetDirectoryName(databasePath)!);
        var expectedFactory = new SqliteConnectionFactory(expectedPaths);
        var candidateFactory = new SqliteConnectionFactory(candidatePaths);
        try
        {
            await new SqliteDatabaseInitializer(expectedFactory, NullLogger<SqliteDatabaseInitializer>.Instance)
                .InitializeAsync(cancellationToken);
            await using var expected = OpenCandidate(expectedPaths.DatabasePath);
            await expected.OpenAsync(cancellationToken);
            // Check the claimed schema before migrations can create missing later-version tables.
            await CheckSchemaAsync(databasePath, expected, version, cancellationToken);
            if (version < SqliteDatabaseInitializer.CurrentVersion)
                await new SqliteDatabaseInitializer(candidateFactory, NullLogger<SqliteDatabaseInitializer>.Instance)
                    .InitializeAsync(cancellationToken);
            await CheckSchemaAsync(databasePath, expected, SqliteDatabaseInitializer.CurrentVersion, cancellationToken);
            await VerifyStartupAsync(databasePath, cancellationToken);
            await CheckpointAsync(databasePath, cancellationToken);
        }
        finally
        {
            using (var connection = expectedFactory.Create()) SqliteConnection.ClearPool(connection);
            using (var connection = candidateFactory.Create()) SqliteConnection.ClearPool(connection);
            TryDeleteDirectory(expectedRoot);
        }
    }

    private static int IntroducedVersion(string table) => table switch
    {
        "collection_folders" or "collection_items" => 2,
        "sync_conflicts" or "cloud_workspaces_cache" or "cloud_members_cache" or "cloud_groups_cache" or "blob_transfers" => 4,
        "cloud_workspace_mappings" => 5,
        "todo_tags" or "todo_items" or "todo_item_tags" => 6,
        "published_config" or "paired_peer" or "subscription" or "config_snapshot" => 8,
        _ => 1
    };

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static async Task CheckSchemaAsync(
        string databasePath, SqliteConnection expected, int version, CancellationToken cancellationToken)
    {
        await using var actual = OpenCandidate(databasePath);
        await actual.OpenAsync(cancellationToken);
        await using (var unsafeObjects = actual.CreateCommand())
        {
            unsafeObjects.CommandText = "SELECT count(*) FROM sqlite_master WHERE type IN ('view','trigger');";
            if (Convert.ToInt32(await unsafeObjects.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidDataException("Backup contains unsupported database views or triggers.");
        }
        var tables = new List<string>();
        await using (var command = expected.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables.Where(t => IntroducedVersion(t) <= version))
        {
            var expectedColumns = await ReadColumnsAsync(expected, table, cancellationToken);
            var actualColumns = await ReadColumnsAsync(actual, table, cancellationToken);
            foreach (var (name, signature) in expectedColumns)
            {
                if (table == "sync_operations" && version < 4 &&
                    name is "next_attempt_at" or "error_code" or "lease_owner" or "lease_expires_at") continue;
                if (table == "collection_folders" && version < 8 && name == "managed_subscription_id") continue;
                if (!actualColumns.TryGetValue(name, out var found) || found != signature)
                    throw new InvalidDataException($"Backup schema is missing or incompatible: {table}.{name}.");
            }
            foreach (var connection in new[] { expected, actual })
            {
                await using var query = connection.CreateCommand();
                query.CommandText = $"SELECT * FROM {QuoteIdentifier(table)} LIMIT 0;";
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            }
            // Missing FK declarations would otherwise make foreign_key_check falsely pass.
            var expectedKeys = await ReadForeignKeysAsync(expected, table, cancellationToken);
            var actualKeys = await ReadForeignKeysAsync(actual, table, cancellationToken);
            if (!expectedKeys.SequenceEqual(actualKeys))
                throw new InvalidDataException($"Backup foreign key schema is incompatible: {table}.");
        }
        await CheckForeignKeysAsync(actual, cancellationToken);
    }

    private static async Task<Dictionary<string, string>> ReadColumnsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(1), $"{reader.GetString(2).ToUpperInvariant()}:{reader.GetInt32(3)}:{reader.GetInt32(5)}");
        return result;
    }

    private static async Task<string[]> ReadForeignKeysAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(string.Join("|", Enumerable.Range(1, 7).Select(i => Convert.ToString(reader.GetValue(i)))));
        return result.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static async Task CheckForeignKeysAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Backup contains invalid foreign key references.");
    }

    private static async Task VerifyStartupAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = OpenCandidate(databasePath);
        await connection.OpenAsync(cancellationToken);
        await CheckForeignKeysAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, workspace_type FROM workspaces LIMIT 1;
            SELECT id, workspace_id, text_content, image_path, thumbnail_path, file_manifest
                FROM clipboard_items WHERE deleted_at IS NULL ORDER BY captured_at DESC LIMIT 1;
            SELECT id, workspace_id, folder_id, local_path FROM collection_items WHERE deleted_at IS NULL LIMIT 1;
            SELECT id, title, is_completed FROM todo_items WHERE deleted_at IS NULL LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do { while (await reader.ReadAsync(cancellationToken)) { } }
        while (await reader.NextResultAsync(cancellationToken));
    }

    private static async Task CheckpointAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = OpenCandidate(databasePath, writable: true);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && reader.GetInt32(0) != 0)
            throw new IOException("Candidate database checkpoint is busy.");
    }

    private async Task RebaseManagedPathsAsync(string extractedRoot, string? sourceRoot, CancellationToken cancellationToken)
    {
        // Legacy archives have no authoritative source root. Keep their paths rather than
        // guessing that an external user file belongs to the application.
        if (sourceRoot is null) return;
        if (!Path.IsPathFullyQualified(sourceRoot)) throw new InvalidDataException("Backup source root must be absolute.");
        var origin = Path.GetFullPath(sourceRoot);
        string Rebase(string value)
        {
            if (!Path.IsPathFullyQualified(value)) return value;
            var full = Path.GetFullPath(value);
            foreach (var prefix in new[] { "blobs", "thumbnails", "imports" })
            {
                var managedRoot = Path.Combine(origin, prefix);
                if (IsContained(managedRoot, full))
                    return Path.Combine(Path.GetFullPath(paths.Root), prefix, Path.GetRelativePath(managedRoot, full));
            }
            return value;
        }
        await using var connection = OpenCandidate(Path.Combine(extractedRoot, DatabaseEntry), writable: true);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var (table, columns) in new[]
        {
            ("clipboard_items", new[] { "image_path", "thumbnail_path", "file_manifest" }),
            ("blobs", new[] { "local_path", "thumbnail_path" }),
            ("collection_items", new[] { "local_path", "thumbnail_path", "file_manifest" })
        })
        {
            var updates = new List<(string Id, string Column, string Value)>();
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = $"SELECT id,{string.Join(",", columns)} FROM {table};";
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    for (var i = 0; i < columns.Length; i++)
                    {
                        if (reader.IsDBNull(i + 1)) continue;
                        var original = reader.GetString(i + 1);
                        string updated;
                        if (columns[i] == "file_manifest")
                        {
                            var files = JsonSerializer.Deserialize<string[]>(original)
                                ?? throw new InvalidDataException("Backup file manifest must be an array.");
                            if (files.Any(x => x is null)) throw new InvalidDataException("Backup file manifest contains null paths.");
                            var mapped = files.Select(Rebase).ToArray();
                            updated = files.SequenceEqual(mapped) ? original : JsonSerializer.Serialize(mapped);
                        }
                        else updated = Rebase(original);
                        if (updated != original) updates.Add((reader.GetString(0), columns[i], updated));
                    }
            }
            foreach (var update in updates)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"UPDATE {table} SET {update.Column}=$value WHERE id=$id;";
                command.Parameters.AddWithValue("$value", update.Value);
                command.Parameters.AddWithValue("$id", update.Id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        transaction.Commit();
        await CheckpointAsync(Path.Combine(extractedRoot, DatabaseEntry), cancellationToken);
    }

    private async Task<HashSet<string>> GetReferencedManagedFilesAsync(CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(paths.DatabasePath)) return result;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT image_path, thumbnail_path
            FROM clipboard_items
            WHERE deleted_at IS NULL
            UNION ALL
            SELECT b.local_path, b.thumbnail_path
            FROM blobs b
            INNER JOIN clipboard_items c ON c.id = b.clipboard_item_id
            WHERE c.deleted_at IS NULL
            UNION ALL
            SELECT local_path, thumbnail_path
            FROM collection_items
            WHERE deleted_at IS NULL;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var i = 0; i < 2; i++)
            {
                if (reader.IsDBNull(i)) continue;
                var value = reader.GetString(i);
                if (TryResolveExistingManagedPath(value, out var managed)) result.Add(managed);
            }
        }
        return result;
    }

    private bool TryResolveExistingManagedPath(string value, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(paths.Root, value));
        if (!IsContained(paths.BlobsPath, candidate) && !IsContained(paths.ThumbnailsPath, candidate)) return false;
        fullPath = candidate;
        return true;
    }

    private bool TryResolveManagedRelativePath(string relative, out string fullPath)
    {
        fullPath = string.Empty;
        var slash = relative.IndexOf('/');
        if (slash <= 0) return false;
        var prefix = relative[..slash];
        var root = prefix switch { "blobs" => paths.BlobsPath, "thumbnails" => paths.ThumbnailsPath, _ => null };
        if (root is null) return false;
        var candidate = Path.GetFullPath(Path.Combine(root, relative[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(root, candidate)) return false;
        fullPath = candidate;
        return true;
    }

    private static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SnapshotLiveStateAsync(string rollbackRoot, CancellationToken cancellationToken)
    {
        if (File.Exists(paths.DatabasePath)) await CreateDatabaseSnapshotAsync(Path.Combine(rollbackRoot, DatabaseEntry), cancellationToken);
        CopyOptional(paths.SettingsPath, Path.Combine(rollbackRoot, "settings.json"));
        CopyOptional(paths.DevicePath, Path.Combine(rollbackRoot, "device.json"));
        CopyDirectory(paths.BlobsPath, Path.Combine(rollbackRoot, "blobs"), cancellationToken);
        CopyDirectory(paths.ThumbnailsPath, Path.Combine(rollbackRoot, "thumbnails"), cancellationToken);
        CopyDirectory(paths.ImportsPath, Path.Combine(rollbackRoot, "imports"), cancellationToken);
    }

    private void ReplaceLiveState(string extractedRoot, bool replaceImports, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        paths.EnsureCreated();
        ReplaceFile(Path.Combine(extractedRoot, DatabaseEntry), paths.DatabasePath);
        ReplaceOptional(Path.Combine(extractedRoot, "settings.json"), paths.SettingsPath);
        ReplaceOptional(Path.Combine(extractedRoot, "device.json"), paths.DevicePath);
        ReplaceDirectory(Path.Combine(extractedRoot, "blobs"), paths.BlobsPath, cancellationToken);
        ReplaceDirectory(Path.Combine(extractedRoot, "thumbnails"), paths.ThumbnailsPath, cancellationToken);
        if (replaceImports) ReplaceDirectory(Path.Combine(extractedRoot, "imports"), paths.ImportsPath, cancellationToken);
    }

    private void RestoreRollbackState(string rollbackRoot)
    {
        SqliteConnection.ClearAllPools();
        ReplaceOptional(Path.Combine(rollbackRoot, DatabaseEntry), paths.DatabasePath);
        ReplaceOptional(Path.Combine(rollbackRoot, "settings.json"), paths.SettingsPath);
        ReplaceOptional(Path.Combine(rollbackRoot, "device.json"), paths.DevicePath);
        ReplaceDirectory(Path.Combine(rollbackRoot, "blobs"), paths.BlobsPath, CancellationToken.None);
        ReplaceDirectory(Path.Combine(rollbackRoot, "thumbnails"), paths.ThumbnailsPath, CancellationToken.None);
        ReplaceDirectory(Path.Combine(rollbackRoot, "imports"), paths.ImportsPath, CancellationToken.None);
    }

    private static void ReplaceOptional(string source, string destination)
    {
        if (File.Exists(source)) ReplaceFile(source, destination); else TryDeleteFile(destination);
    }

    private static void ReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".restore-" + Guid.NewGuid().ToString("N");
        File.Copy(source, staging, true);
        File.Move(staging, destination, true);
    }

    private static void ReplaceDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        var staging = destination + ".restore-" + Guid.NewGuid().ToString("N");
        TryDeleteDirectory(staging);
        CopyDirectory(source, staging, cancellationToken);
        TryDeleteDirectory(destination);
        if (Directory.Exists(staging)) Directory.Move(staging, destination); else Directory.CreateDirectory(destination);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void AddDirectory(
        ZipArchive archive,
        string root,
        string prefix,
        string temporaryArchive,
        string finalArchive,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file);
            if (string.Equals(full, temporaryArchive, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full, finalArchive, StringComparison.OrdinalIgnoreCase)) continue;
            AddFile(archive, file, prefix + "/" + Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
    }

    private static void AddOptionalFile(ZipArchive archive, string path, string entry) { if (File.Exists(path)) AddFile(archive, path, entry); }
    private static void AddFile(ZipArchive archive, string path, string entry) => archive.CreateEntryFromFile(path, entry, CompressionLevel.Optimal);
    private static void CopyOptional(string source, string destination) { if (File.Exists(source)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true); } }
    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static (long Bytes, int Count) MeasureDirectory(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return (0, 0);
        long bytes = 0; var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { cancellationToken.ThrowIfCancellationRequested(); bytes += new FileInfo(file).Length; count++; }
        return (bytes, count);
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed record BackupManifest(
        int BackupFormatVersion,
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        string? SourceRoot = null,
        bool ImportsIncluded = false);
    private sealed record ValidatedBackup(string ExtractedRoot, BackupManifest Manifest, bool HasImports);
}
