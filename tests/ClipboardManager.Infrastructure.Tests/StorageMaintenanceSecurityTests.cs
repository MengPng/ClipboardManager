using System.IO.Compression;
using System.Text.Json;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class StorageMaintenanceSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Storage.Security", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FakeCurrentSchema_IsRejectedAndDoesNotReplaceLiveDatabase()
    {
        var paths = new AppPaths(_root);
        var service = await CreateAsync(paths);
        await ExecuteAsync(paths, "INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES('w',0,'live','x','x');");
        var archive = Path.Combine(_root, "fake.zip");
        var fake = Path.Combine(_root, "fake.db");
        await using (var connection = Open(fake))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=7;";
            await command.ExecuteNonQueryAsync();
        }
        await CreateArchiveAsync(archive, fake, 7);

        Assert.False((await service.ValidateBackupAsync(archive)).IsValid);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreAsync(archive));
        Assert.Equal("live", await ScalarAsync(paths, "SELECT name FROM workspaces WHERE id='w';"));
    }

    [Fact]
    public async Task OldSupportedSchema_IsMigratedBeforeRestore()
    {
        var sourceRoot = Path.Combine(_root, "old");
        var targetRoot = Path.Combine(_root, "target");
        var sourcePaths = new AppPaths(sourceRoot);
        Directory.CreateDirectory(sourceRoot);
        await using (var connection = Open(sourcePaths.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE identities(id TEXT PRIMARY KEY,provider TEXT NOT NULL,provider_subject TEXT NOT NULL,display_name TEXT NOT NULL,avatar_url TEXT,created_at TEXT NOT NULL,last_signed_in_at TEXT);
                CREATE TABLE devices(id TEXT PRIMARY KEY,owner_id TEXT,device_name TEXT NOT NULL,platform TEXT NOT NULL,app_version TEXT,created_at TEXT NOT NULL,last_seen_at TEXT,is_revoked INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE workspaces(id TEXT PRIMARY KEY,workspace_type INTEGER NOT NULL,name TEXT NOT NULL,owner_id TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
                CREATE TABLE workspace_members(workspace_id TEXT NOT NULL,identity_id TEXT NOT NULL,role TEXT NOT NULL,status TEXT NOT NULL,joined_at TEXT NOT NULL,PRIMARY KEY(workspace_id,identity_id),FOREIGN KEY(workspace_id) REFERENCES workspaces(id));
                CREATE TABLE clipboard_items(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL,owner_id TEXT,device_id TEXT NOT NULL,item_type INTEGER NOT NULL,content_hash TEXT NOT NULL,text_content TEXT,preview_text TEXT,source_app TEXT,source_process TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,captured_at TEXT NOT NULL,last_used_at TEXT,deleted_at TEXT,is_favorite INTEGER NOT NULL DEFAULT 0,is_pinned INTEGER NOT NULL DEFAULT 0,use_count INTEGER NOT NULL DEFAULT 0,visibility INTEGER NOT NULL DEFAULT 0,sync_status INTEGER NOT NULL DEFAULT 0,local_version INTEGER NOT NULL DEFAULT 1,server_version INTEGER,file_manifest TEXT,image_path TEXT,thumbnail_path TEXT,content_size INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(workspace_id) REFERENCES workspaces(id));
                CREATE TABLE blobs(id TEXT PRIMARY KEY,clipboard_item_id TEXT NOT NULL,content_hash TEXT NOT NULL,mime_type TEXT NOT NULL,byte_size INTEGER NOT NULL,local_path TEXT,thumbnail_path TEXT,remote_blob_id TEXT,upload_status TEXT NOT NULL,created_at TEXT NOT NULL,FOREIGN KEY(clipboard_item_id) REFERENCES clipboard_items(id));
                CREATE TABLE sync_operations(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL,entity_type TEXT NOT NULL,entity_id TEXT NOT NULL,operation_type INTEGER NOT NULL,base_version INTEGER NOT NULL,payload TEXT NOT NULL,created_at TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,last_error TEXT,synced_at TEXT);
                CREATE TABLE sync_state(workspace_id TEXT PRIMARY KEY,remote_cursor TEXT,last_sync_at TEXT,last_success_at TEXT,last_error TEXT);
                PRAGMA user_version=1;
                INSERT INTO workspaces VALUES('old',0,'old',NULL,'x','x');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var archive = Path.Combine(_root, "old.zip");
        await CreateArchiveAsync(archive, sourcePaths.DatabasePath, 1);
        var target = await CreateAsync(new AppPaths(targetRoot));

        await target.RestoreAsync(archive);

        Assert.Equal("old", await ScalarAsync(new AppPaths(targetRoot), "SELECT name FROM workspaces WHERE id='old';"));
        Assert.Equal("7", await ScalarAsync(new AppPaths(targetRoot), "PRAGMA user_version;"));
    }

    [Fact]
    public async Task CrossRootRestore_RebasesManagedImportsAndPreservesExternalPaths()
    {
        var sourceRoot = Path.Combine(_root, "source");
        var targetRoot = Path.Combine(_root, "target");
        var sourcePaths = new AppPaths(sourceRoot);
        var source = await CreateAsync(sourcePaths);
        var external = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(sourcePaths.ImportsPath);
        await File.WriteAllTextAsync(Path.Combine(sourcePaths.ImportsPath, "attached.txt"), "managed");
        await File.WriteAllTextAsync(external, "external");
        await ExecuteAsync(sourcePaths, """
            INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES('w',0,'w','x','x');
            INSERT INTO clipboard_items(id,workspace_id,device_id,item_type,content_hash,created_at,updated_at,captured_at,file_manifest)
            VALUES('c','w','d',2,'h','x','x','x',$manifest);
            """, ("$manifest", JsonSerializer.Serialize(new[] { Path.Combine(sourcePaths.ImportsPath, "attached.txt"), external })));
        var archive = Path.Combine(_root, "full.zip");
        await source.CreateBackupAsync(archive);
        var targetPaths = new AppPaths(targetRoot);
        var target = await CreateAsync(targetPaths);

        await target.RestoreAsync(archive);

        Assert.Equal("managed", await File.ReadAllTextAsync(Path.Combine(targetPaths.ImportsPath, "attached.txt")));
        var manifest = await ScalarAsync(targetPaths, "SELECT file_manifest FROM clipboard_items WHERE id='c';");
        var manifestPaths = JsonSerializer.Deserialize<string[]>(manifest)!;
        Assert.Contains(Path.Combine(targetPaths.ImportsPath, "attached.txt"), manifestPaths);
        Assert.Contains(external, manifestPaths);
    }

    [Fact]
    public async Task ArchiveLimitsAndCancellation_LeaveLiveStateUntouched()
    {
        var paths = new AppPaths(_root);
        var service = await CreateAsync(paths);
        await ExecuteAsync(paths, "INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at) VALUES('w',0,'live','x','x');");
        var archive = Path.Combine(_root, "large.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("manifest.json");
            await using (var stream = manifestEntry.Open())
                await stream.WriteAsync(new byte[65 * 1024]);
            await using var db = zip.CreateEntry("clipboard.db").Open();
            await db.WriteAsync(new byte[128]);
        }
        Assert.False((await service.ValidateBackupAsync(archive)).IsValid);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RestoreAsync(archive, cancelled.Token));
        Assert.Equal("live", await ScalarAsync(paths, "SELECT name FROM workspaces WHERE id='w';"));
    }

    private static async Task<StorageMaintenanceService> CreateAsync(AppPaths paths)
    {
        var factory = new SqliteConnectionFactory(paths);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        return new StorageMaintenanceService(paths, factory, NullLogger<StorageMaintenanceService>.Instance);
    }

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false }.ConnectionString);

    private static async Task CreateArchiveAsync(string archive, string database, int schema)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(database, "clipboard.db");
        await using var stream = zip.CreateEntry("manifest.json").Open();
        await JsonSerializer.SerializeAsync(stream, new { BackupFormatVersion = 1, SchemaVersion = schema, CreatedAtUtc = DateTimeOffset.UtcNow });
    }

    private static async Task ExecuteAsync(AppPaths paths, string sql, params (string Name, object Value)[] values)
    {
        await using var connection = Open(paths.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(AppPaths paths, string sql)
    {
        await using var connection = Open(paths.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
