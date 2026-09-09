using ClipboardManager.Infrastructure.Paths;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteConnectionFactory(AppPaths paths)
{
    private const int CommandTimeoutSeconds = 15;

    public SqliteConnection Create()
    {
        paths.EnsureCreated();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared cache takes process-wide table locks that busy_timeout cannot break, so a
            // reader and a writer can deadlock instead of retrying. Keep every connection private.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            // Without a timeout a contended statement waits forever and freezes startup.
            DefaultTimeout = CommandTimeoutSeconds
        };
        return new SqliteConnection(builder.ConnectionString);
    }
}
