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
            command.CommandText = "CREATE TABLE marker(value TEXT); INSERT INTO marker VALUES ('before'); PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync();
        }
        var backup = new DatabaseSafetyBackup(paths, factory);
        await new SqliteDatabaseInitializer(factory, NullLogger<SqliteDatabaseInitializer>.Instance, backup).InitializeAsync();
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "migration-backups"), "clipboard-v1-before-v2-*.db"));
        await using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly }.ConnectionString);
        await snapshot.OpenAsync();
        await using var check = snapshot.CreateCommand();
        check.CommandText = "SELECT value FROM marker;";
        Assert.Equal("before", await check.ExecuteScalarAsync());
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
