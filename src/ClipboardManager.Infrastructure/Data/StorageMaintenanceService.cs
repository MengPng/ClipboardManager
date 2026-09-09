using System.IO.Compression;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.Data;

public sealed class StorageMaintenanceService(
    AppPaths paths,
    SqliteConnectionFactory connectionFactory,
    ILogger<StorageMaintenanceService> logger) : IStorageMaintenanceService
{
    private const int BackupFormatVersion = 1;
    private const string DatabaseEntry = "clipboard.db";
    private const string ManifestEntry = "manifest.json";

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
            var manifest = new BackupManifest(BackupFormatVersion, schemaVersion, DateTimeOffset.UtcNow);
            await using (var stream = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                AddFile(archive, snapshotPath, DatabaseEntry);
                AddOptionalFile(archive, paths.SettingsPath, "settings.json");
                AddOptionalFile(archive, paths.DevicePath, "device.json");
                AddDirectory(archive, paths.BlobsPath, "blobs", temporaryArchive, fullBackupPath, cancellationToken);
                AddDirectory(archive, paths.ThumbnailsPath, "thumbnails", temporaryArchive, fullBackupPath, cancellationToken);
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
        Directory.CreateDirectory(rollbackRoot);
        try
        {
            await SnapshotLiveStateAsync(rollbackRoot, cancellationToken);
            try
            {
                ReplaceLiveState(validated.ExtractedRoot, cancellationToken);
                await ValidateDatabaseAsync(paths.DatabasePath, cancellationToken);
            }
            catch
            {
                RestoreRollbackState(rollbackRoot);
                throw;
            }

            return new RestoreResult(DateTimeOffset.UtcNow, validated.Manifest.SchemaVersion);
        }
        finally
        {
            TryDeleteDirectory(validated.ExtractedRoot);
            TryDeleteDirectory(rollbackRoot);
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrEmpty(normalized) || normalized.EndsWith('/')) continue;
                if (!seen.Add(normalized)) throw new InvalidDataException($"Duplicate archive entry: {normalized}");
                if (!IsAllowedEntry(normalized)) throw new InvalidDataException($"Unsupported or unsafe archive entry: {normalized}");
                var destination = GetContainedPath(extractedRoot, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, cancellationToken);
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
            return new ValidatedBackup(extractedRoot, manifest);
        }
        catch
        {
            TryDeleteDirectory(extractedRoot);
            throw;
        }
    }

    private static bool IsAllowedEntry(string entry) =>
        entry is DatabaseEntry or ManifestEntry or "settings.json" or "device.json" ||
        entry.StartsWith("blobs/", StringComparison.Ordinal) || entry.StartsWith("thumbnails/", StringComparison.Ordinal);

    private static string GetContainedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Archive entry is rooted.");
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
    }

    private void ReplaceLiveState(string extractedRoot, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        paths.EnsureCreated();
        ReplaceFile(Path.Combine(extractedRoot, DatabaseEntry), paths.DatabasePath);
        ReplaceOptional(Path.Combine(extractedRoot, "settings.json"), paths.SettingsPath);
        ReplaceOptional(Path.Combine(extractedRoot, "device.json"), paths.DevicePath);
        ReplaceDirectory(Path.Combine(extractedRoot, "blobs"), paths.BlobsPath, cancellationToken);
        ReplaceDirectory(Path.Combine(extractedRoot, "thumbnails"), paths.ThumbnailsPath, cancellationToken);
    }

    private void RestoreRollbackState(string rollbackRoot)
    {
        SqliteConnection.ClearAllPools();
        ReplaceOptional(Path.Combine(rollbackRoot, DatabaseEntry), paths.DatabasePath);
        ReplaceOptional(Path.Combine(rollbackRoot, "settings.json"), paths.SettingsPath);
        ReplaceOptional(Path.Combine(rollbackRoot, "device.json"), paths.DevicePath);
        ReplaceDirectory(Path.Combine(rollbackRoot, "blobs"), paths.BlobsPath, CancellationToken.None);
        ReplaceDirectory(Path.Combine(rollbackRoot, "thumbnails"), paths.ThumbnailsPath, CancellationToken.None);
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

    private sealed record BackupManifest(int BackupFormatVersion, int SchemaVersion, DateTimeOffset CreatedAtUtc);
    private sealed record ValidatedBackup(string ExtractedRoot, BackupManifest Manifest);
}
