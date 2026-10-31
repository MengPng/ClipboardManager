using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class SqliteDatabaseInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_CreatesVersionOneSchema()
    {
        var paths = new AppPaths(_root);
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(
            factory,
            NullLogger<SqliteDatabaseInitializer>.Instance);

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("clipboard_items", tables);
        Assert.Contains("workspaces", tables);
        Assert.Contains("sync_operations", tables);
        Assert.Contains("sync_conflicts", tables);
        Assert.Contains("blob_transfers", tables);
        Assert.Contains("todo_items", tables);
        Assert.Contains("todo_tags", tables);
        await reader.DisposeAsync();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(SqliteDatabaseInitializer.CurrentVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Upgrade_CreatesVersionedSafetyBackupBeforeMigration()
    {
        var paths = new AppPaths(_root);
        var factory = new SqliteConnectionFactory(paths);
        await using (var connection = factory.Create())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE marker(value TEXT); INSERT INTO marker VALUES ('before'); CREATE TABLE clipboard_items(id TEXT PRIMARY KEY, item_type INTEGER, file_manifest TEXT); PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync();
        }
        var backup = new DatabaseSafetyBackup(paths, factory);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance, backup).InitializeAsync();
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "migration-backups"), "clipboard-v1-before-v8-*.db"));
        await using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly }.ConnectionString);
        await snapshot.OpenAsync();
        await using var check = snapshot.CreateCommand();
        check.CommandText = "SELECT value FROM marker;";
        Assert.Equal("before", await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UpgradeToVersionSeven_RemovesPresetTagsAndKeepsTodos()
    {
        var paths = new AppPaths(_root);
        var factory = new SqliteConnectionFactory(paths);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        await using (var connection = factory.Create())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO workspaces(id,workspace_type,name,created_at,updated_at)
                VALUES('11111111-1111-1111-1111-111111111111',0,'local',$now,$now);
                INSERT INTO todo_tags(id,workspace_id,name,color,is_preset,sort_key,created_at,updated_at)
                VALUES('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111','工作','#0F766E',1,0,$now,$now),
                      ('33333333-3333-3333-3333-333333333333','11111111-1111-1111-1111-111111111111','项目','#2563EB',0,1,$now,$now);
                INSERT INTO todo_items(id,workspace_id,title,is_completed,created_at,updated_at)
                VALUES('44444444-4444-4444-4444-444444444444','11111111-1111-1111-1111-111111111111','保留待办',0,$now,$now);
                INSERT INTO todo_item_tags(todo_id,tag_id,workspace_id)
                VALUES('44444444-4444-4444-4444-444444444444','22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111'),
                      ('44444444-4444-4444-4444-444444444444','33333333-3333-3333-3333-333333333333','11111111-1111-1111-1111-111111111111');
                PRAGMA user_version=6;
                """;
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }

        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();

        await using var verify = factory.Create();
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM todo_tags WHERE is_preset=1;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM todo_items WHERE title='保留待办';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT tag_id FROM todo_item_tags WHERE todo_id='44444444-4444-4444-4444-444444444444';";
        Assert.Equal("33333333-3333-3333-3333-333333333333", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UpgradeToVersionThree_ReclassifiesOnlySingleImageFileRecords()
    {
        var paths = new AppPaths(_root);
        var factory = new SqliteConnectionFactory(paths);
        await using (var connection = factory.Create())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE clipboard_items(id TEXT PRIMARY KEY, item_type INTEGER, file_manifest TEXT);
                INSERT INTO clipboard_items VALUES ('image', 2, '["C:\\Temp\\photo.PNG"]');
                INSERT INTO clipboard_items VALUES ('document', 2, '["C:\\Temp\\report.xlsx"]');
                INSERT INTO clipboard_items VALUES ('multiple', 2, '["C:\\Temp\\a.png","C:\\Temp\\b.png"]');
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();

        await using var verify = factory.Create();
        await verify.OpenAsync();
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT id, item_type FROM clipboard_items ORDER BY id;";
        await using var reader = await read.ExecuteReaderAsync();
        var values = new Dictionary<string, long>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0), reader.GetInt64(1));
        Assert.Equal(3, values["image"]);
        Assert.Equal(2, values["document"]);
        Assert.Equal(2, values["multiple"]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
