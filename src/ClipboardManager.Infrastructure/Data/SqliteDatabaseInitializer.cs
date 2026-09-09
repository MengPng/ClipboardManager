using ClipboardManager.Domain.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteDatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    ILogger<SqliteDatabaseInitializer> logger,
    DatabaseSafetyBackup? safetyBackup = null) : IDatabaseInitializer
{
    public const int CurrentVersion = 3;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);

        var version = await GetUserVersionAsync(connection, cancellationToken);
        if (version > CurrentVersion)
        {
            throw new InvalidOperationException($"数据库版本 {version} 高于应用支持版本 {CurrentVersion}。");
        }

        if (version > 0 && version < CurrentVersion)
        {
            var backupPath = await (safetyBackup ?? new DatabaseSafetyBackup(
                new ClipboardManager.Infrastructure.Paths.AppPaths(Path.GetDirectoryName(connection.DataSource)), connectionFactory))
                .CreateBeforeMigrationAsync(version, CurrentVersion, cancellationToken);
            logger.LogInformation("Created database migration safety backup {BackupPath}.", backupPath);
        }

        if (version < 1)
        {
            await ApplyVersion1Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=1;", cancellationToken);
            version = 1;
        }

        if (version < 2)
        {
            await ApplyVersion2Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=2;", cancellationToken);
            version = 2;
        }

        if (version < 3)
        {
            await ApplyVersion3Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=3;", cancellationToken);
        }

        logger.LogInformation("Local database schema is at version {Version}.", CurrentVersion);
    }

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    private static async Task ApplyVersion1Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN;

            CREATE TABLE IF NOT EXISTS identities (
                id TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                provider_subject TEXT NOT NULL,
                display_name TEXT NOT NULL,
                avatar_url TEXT NULL,
                created_at TEXT NOT NULL,
                last_signed_in_at TEXT NULL,
                UNIQUE(provider, provider_subject)
            );

            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY,
                owner_id TEXT NULL,
                device_name TEXT NOT NULL,
                platform TEXT NOT NULL,
                app_version TEXT NULL,
                created_at TEXT NOT NULL,
                last_seen_at TEXT NULL,
                is_revoked INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS workspaces (
                id TEXT PRIMARY KEY,
                workspace_type INTEGER NOT NULL,
                name TEXT NOT NULL,
                owner_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS workspace_members (
                workspace_id TEXT NOT NULL,
                identity_id TEXT NOT NULL,
                role TEXT NOT NULL,
                status TEXT NOT NULL,
                joined_at TEXT NOT NULL,
                PRIMARY KEY(workspace_id, identity_id),
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id)
            );

            CREATE TABLE IF NOT EXISTS clipboard_items (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                owner_id TEXT NULL,
                device_id TEXT NOT NULL,
                item_type INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                text_content TEXT NULL,
                preview_text TEXT NULL,
                source_app TEXT NULL,
                source_process TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                last_used_at TEXT NULL,
                deleted_at TEXT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                use_count INTEGER NOT NULL DEFAULT 0,
                visibility INTEGER NOT NULL DEFAULT 0,
                sync_status INTEGER NOT NULL DEFAULT 0,
                local_version INTEGER NOT NULL DEFAULT 1,
                server_version INTEGER NULL,
                file_manifest TEXT NULL,
                image_path TEXT NULL,
                thumbnail_path TEXT NULL,
                content_size INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id)
            );

            CREATE TABLE IF NOT EXISTS blobs (
                id TEXT PRIMARY KEY,
                clipboard_item_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                byte_size INTEGER NOT NULL,
                local_path TEXT NULL,
                thumbnail_path TEXT NULL,
                remote_blob_id TEXT NULL,
                upload_status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(clipboard_item_id) REFERENCES clipboard_items(id)
            );

            CREATE TABLE IF NOT EXISTS sync_operations (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                operation_type INTEGER NOT NULL,
                base_version INTEGER NOT NULL,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                synced_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_state (
                workspace_id TEXT PRIMARY KEY,
                remote_cursor TEXT NULL,
                last_sync_at TEXT NULL,
                last_success_at TEXT NULL,
                last_error TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_clipboard_items_workspace_active_time
                ON clipboard_items(workspace_id, deleted_at, captured_at DESC);
            CREATE INDEX IF NOT EXISTS ix_clipboard_items_workspace_type
                ON clipboard_items(workspace_id, item_type, deleted_at, captured_at DESC);
            CREATE INDEX IF NOT EXISTS ix_clipboard_items_workspace_favorite
                ON clipboard_items(workspace_id, is_favorite, deleted_at, captured_at DESC);
            CREATE INDEX IF NOT EXISTS ix_clipboard_items_workspace_pinned
                ON clipboard_items(workspace_id, is_pinned, deleted_at, captured_at DESC);
            CREATE INDEX IF NOT EXISTS ix_clipboard_items_content_hash
                ON clipboard_items(workspace_id, content_hash, item_type);
            CREATE INDEX IF NOT EXISTS ix_sync_operations_pending
                ON sync_operations(workspace_id, synced_at, created_at);

            COMMIT;
            """;

        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ApplyVersion2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN;
            CREATE TABLE IF NOT EXISTS collection_folders (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                parent_folder_id TEXT NULL,
                name TEXT NOT NULL,
                sort_key REAL NOT NULL,
                is_system INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                local_version INTEGER NOT NULL DEFAULT 1,
                server_version INTEGER NULL,
                sync_status INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id),
                FOREIGN KEY(parent_folder_id) REFERENCES collection_folders(id)
            );
            CREATE TABLE IF NOT EXISTS collection_items (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                folder_id TEXT NOT NULL,
                source_clipboard_item_id TEXT NULL,
                item_type INTEGER NOT NULL,
                title TEXT NULL,
                text_content TEXT NULL,
                url TEXT NULL,
                description TEXT NULL,
                local_path TEXT NULL,
                thumbnail_path TEXT NULL,
                file_manifest TEXT NULL,
                content_hash TEXT NULL,
                content_size INTEGER NOT NULL DEFAULT 0,
                sort_key REAL NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                local_version INTEGER NOT NULL DEFAULT 1,
                server_version INTEGER NULL,
                sync_status INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id),
                FOREIGN KEY(folder_id) REFERENCES collection_folders(id),
                FOREIGN KEY(source_clipboard_item_id) REFERENCES clipboard_items(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_system_folder
                ON collection_folders(workspace_id) WHERE is_system=1 AND deleted_at IS NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_folder_name
                ON collection_folders(workspace_id,parent_folder_id,name COLLATE NOCASE) WHERE deleted_at IS NULL;
            CREATE INDEX IF NOT EXISTS ix_collection_folders_order
                ON collection_folders(workspace_id,parent_folder_id,deleted_at,sort_key);
            CREATE INDEX IF NOT EXISTS ix_collection_items_order
                ON collection_items(workspace_id,folder_id,deleted_at,sort_key);
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ApplyVersion3Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Existing single-image CF_HDROP records keep their file manifest and restore
        // behavior; only the category discriminator changes.
        const string sql = """
            BEGIN;
            UPDATE clipboard_items
            SET item_type = 3
            WHERE item_type = 2
              AND json_valid(file_manifest) = 1
              AND json_array_length(file_manifest) = 1
              AND (
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.png' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.jpg' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.jpeg' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.jfif' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.gif' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.bmp' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.webp' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.tif' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.tiff' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.heic' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.heif' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.avif' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.ico' OR
                lower(json_extract(file_manifest, '$[0]')) LIKE '%.svg'
              );
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
