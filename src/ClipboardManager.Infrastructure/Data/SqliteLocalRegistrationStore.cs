using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteLocalRegistrationStore(SqliteConnectionFactory connectionFactory) : ILocalRegistrationStore
{
    public async Task EnsureRegisteredAsync(
        IdentitySession identity,
        DeviceIdentity device,
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, (SqliteTransaction)transaction, """
            INSERT INTO identities (id, provider, provider_subject, display_name, created_at, last_signed_in_at)
            VALUES ($id, $provider, $subject, $name, $createdAt, $now)
            ON CONFLICT(id) DO UPDATE SET display_name = excluded.display_name, last_signed_in_at = excluded.last_signed_in_at;
            """, cancellationToken,
            ("$id", identity.IdentityId.ToString("D")),
            ("$provider", identity.Provider),
            ("$subject", identity.Subject),
            ("$name", identity.DisplayName),
            ("$createdAt", device.CreatedAt.ToUniversalTime().ToString("O")),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));

        await ExecuteAsync(connection, (SqliteTransaction)transaction, """
            INSERT INTO devices (id, owner_id, device_name, platform, app_version, created_at, last_seen_at, is_revoked)
            VALUES ($id, $ownerId, $name, $platform, $version, $createdAt, $now, 0)
            ON CONFLICT(id) DO UPDATE SET owner_id = excluded.owner_id, device_name = excluded.device_name,
                platform = excluded.platform, app_version = excluded.app_version, last_seen_at = excluded.last_seen_at;
            """, cancellationToken,
            ("$id", device.DeviceId.ToString("D")),
            ("$ownerId", identity.IdentityId.ToString("D")),
            ("$name", device.DeviceName),
            ("$platform", device.Platform),
            ("$version", typeof(SqliteLocalRegistrationStore).Assembly.GetName().Version?.ToString() ?? "0.0.0"),
            ("$createdAt", device.CreatedAt.ToUniversalTime().ToString("O")),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));

        await ExecuteAsync(connection, (SqliteTransaction)transaction, """
            INSERT INTO workspaces (id, workspace_type, name, owner_id, created_at, updated_at)
            VALUES ($id, $type, $name, $ownerId, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, updated_at = excluded.updated_at;
            """, cancellationToken,
            ("$id", workspace.Id.ToString("D")),
            ("$type", (int)workspace.Type),
            ("$name", workspace.Name),
            ("$ownerId", identity.IdentityId.ToString("D")),
            ("$createdAt", workspace.CreatedAt.ToUniversalTime().ToString("O")),
            ("$updatedAt", DateTimeOffset.UtcNow.ToString("O")));

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
