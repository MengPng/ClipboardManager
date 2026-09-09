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
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "migration-backups"), "clipboard-v1-before-v3-*.db"));
        await using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly }.ConnectionString);
        await snapshot.OpenAsync();
        await using var check = snapshot.CreateCommand();
        check.CommandText = "SELECT value FROM marker;";
        Assert.Equal("before", await check.ExecuteScalarAsync());
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
