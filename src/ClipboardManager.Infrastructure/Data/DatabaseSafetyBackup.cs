using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Infrastructure.Data;

public sealed class DatabaseSafetyBackup(AppPaths paths, SqliteConnectionFactory factory)
{
    public async Task<string?> CreateBeforeMigrationAsync(int fromVersion, int toVersion, CancellationToken cancellationToken = default)
    {
        if (fromVersion <= 0 || fromVersion >= toVersion || !File.Exists(paths.DatabasePath)) return null;
        var directory = Path.Combine(paths.Root, "migration-backups");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"clipboard-v{fromVersion}-before-v{toVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
        await using var source = factory.Create();
        await source.OpenAsync(cancellationToken);
        var builder = new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using var target = new SqliteConnection(builder.ConnectionString);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        return destination;
    }
}
