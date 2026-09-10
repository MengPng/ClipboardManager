using Npgsql;
using System.Reflection;

namespace ClipboardManager.Cloud.Api;

public sealed class DatabaseMigrator(NpgsqlDataSource dataSource)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var bootstrap = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations(
              version text PRIMARY KEY,
              applied_at timestamptz NOT NULL DEFAULT now()
            );
            """, connection)) await bootstrap.ExecuteNonQueryAsync(cancellationToken);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(x => x.Contains(".Migrations.") && x.EndsWith(".sql")).OrderBy(x => x))
        {
            var version = Path.GetFileNameWithoutExtension(name.Split(".Migrations.", 2)[1]);
            await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=$1)", connection);
            check.Parameters.AddWithValue(version);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken))!) continue;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Migration resource {name} is missing.");
            using var reader = new StreamReader(stream); var sql = await reader.ReadToEndAsync(cancellationToken);
            // Historical scripts also support psql and contain an outer transaction.
            // The runner owns the transaction so the version marker commits atomically.
            sql = sql.Trim();
            if (sql.StartsWith("BEGIN;", StringComparison.OrdinalIgnoreCase)) sql = sql[6..].TrimStart();
            if (sql.EndsWith("COMMIT;", StringComparison.OrdinalIgnoreCase)) sql = sql[..^7].TrimEnd();
            await using (var apply = new NpgsqlCommand(sql, connection, transaction)) await apply.ExecuteNonQueryAsync(cancellationToken);
            await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES($1)", connection, transaction))
            { mark.Parameters.AddWithValue(version); await mark.ExecuteNonQueryAsync(cancellationToken); }
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
