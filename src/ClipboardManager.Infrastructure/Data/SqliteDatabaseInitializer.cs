using ClipboardManager.Domain.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteDatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    ILogger<SqliteDatabaseInitializer> logger,
    DatabaseSafetyBackup? safetyBackup = null) : IDatabaseInitializer
{
    public const int CurrentVersion = 8;

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
            version = 3;
        }

        if (version < 4)
        {
            await ApplyVersion4Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=4;", cancellationToken);
            version = 4;
        }

        if (version < 5)
        {
            await ApplyVersion5Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=5;", cancellationToken);
            version = 5;
        }

        if (version < 6)
        {
            await ApplyVersion6Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=6;", cancellationToken);
            version = 6;
        }

        if (version < 7)
        {
            await ApplyVersion7Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=7;", cancellationToken);
            version = 7;
        }

        if (version < 8)
        {
            await ApplyVersion8Async(connection, cancellationToken);
            await ExecuteAsync(connection, "PRAGMA user_version=8;", cancellationToken);
        }

        // 0.9.0 多文件夹发布新增的两列。0.9.0 尚未正式发布而 schema 号保持 8，已经跑过第一版 v8 的库不会再进入
        // ApplyVersion8Async，所以这里每次启动都做一次幂等的列补齐，而不是再提高 schema 号。
        await EnsureConfigSyncColumnsAsync(connection, cancellationToken);

        logger.LogInformation("Local database schema is at version {Version}.", CurrentVersion);
    }

    private static async Task EnsureConfigSyncColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, "published_config", "folder_scope", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "collection_folders", "managed_remote_id", "TEXT NULL", cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition,
        CancellationToken cancellationToken)
    {
        await using var tableExists = connection.CreateCommand();
        tableExists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table;";
        tableExists.Parameters.AddWithValue("$table", table);
        if (Convert.ToInt32(await tableExists.ExecuteScalarAsync(cancellationToken)) == 0) return;
        await using var columnExists = connection.CreateCommand();
        columnExists.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        columnExists.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt32(await columnExists.ExecuteScalarAsync(cancellationToken)) > 0) return;
        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken);
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

    private static async Task ApplyVersion4Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN;
            CREATE TABLE IF NOT EXISTS sync_operations (
                id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL, operation_type INTEGER NOT NULL, base_version INTEGER NOT NULL,
                payload TEXT NOT NULL, created_at TEXT NOT NULL, attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL, synced_at TEXT NULL
            );
            ALTER TABLE sync_operations ADD COLUMN next_attempt_at TEXT NULL;
            ALTER TABLE sync_operations ADD COLUMN error_code TEXT NULL;
            ALTER TABLE sync_operations ADD COLUMN lease_owner TEXT NULL;
            ALTER TABLE sync_operations ADD COLUMN lease_expires_at TEXT NULL;

            CREATE TABLE IF NOT EXISTS sync_conflicts (
                id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL, local_payload TEXT NOT NULL, remote_payload TEXT NOT NULL,
                local_version INTEGER NOT NULL, server_version INTEGER NOT NULL,
                device_name TEXT NULL, actor_name TEXT NULL, detected_at TEXT NOT NULL,
                resolved_at TEXT NULL, resolution TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS cloud_workspaces_cache (
                id TEXT PRIMARY KEY, workspace_type INTEGER NOT NULL, name TEXT NOT NULL,
                role TEXT NOT NULL, quota_bytes INTEGER NOT NULL, used_bytes INTEGER NOT NULL,
                cached_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cloud_members_cache (
                workspace_id TEXT NOT NULL, identity_id TEXT NOT NULL, display_name TEXT NOT NULL,
                email TEXT NOT NULL, role TEXT NOT NULL, status TEXT NOT NULL, cached_at TEXT NOT NULL,
                PRIMARY KEY(workspace_id, identity_id)
            );
            CREATE TABLE IF NOT EXISTS cloud_groups_cache (
                id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, name TEXT NOT NULL,
                member_ids_json TEXT NOT NULL, cached_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS blob_transfers (
                id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, blob_id TEXT NOT NULL,
                direction TEXT NOT NULL, local_path TEXT NOT NULL, content_hash TEXT NOT NULL,
                byte_size INTEGER NOT NULL, transferred_bytes INTEGER NOT NULL DEFAULT 0,
                upload_session_id TEXT NULL, state TEXT NOT NULL, attempt_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NULL, last_error_code TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sync_operations_retry
                ON sync_operations(workspace_id, synced_at, next_attempt_at, created_at);
            CREATE INDEX IF NOT EXISTS ix_sync_conflicts_open
                ON sync_conflicts(workspace_id, resolved_at, detected_at DESC);
            CREATE INDEX IF NOT EXISTS ix_blob_transfers_pending
                ON blob_transfers(workspace_id, state, next_attempt_at, created_at);
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ApplyVersion5Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN;
            CREATE TABLE IF NOT EXISTS cloud_workspace_mappings (
                cloud_workspace_id TEXT PRIMARY KEY,
                local_workspace_id TEXT NOT NULL UNIQUE,
                workspace_type INTEGER NOT NULL,
                display_name TEXT NOT NULL,
                role TEXT NOT NULL,
                remote_cursor TEXT NULL,
                cache_state TEXT NOT NULL DEFAULT 'empty',
                last_success_at TEXT NULL,
                last_error_code TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(local_workspace_id) REFERENCES workspaces(id)
            );
            CREATE INDEX IF NOT EXISTS ix_cloud_workspace_mappings_state
                ON cloud_workspace_mappings(cache_state, updated_at);
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ApplyVersion6Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            BEGIN;
            CREATE TABLE IF NOT EXISTS todo_tags (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                name TEXT NOT NULL,
                color TEXT NOT NULL,
                is_preset INTEGER NOT NULL DEFAULT 0,
                sort_key REAL NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id)
            );
            CREATE TABLE IF NOT EXISTS todo_items (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                title TEXT NOT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                completed_at TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id)
            );
            CREATE TABLE IF NOT EXISTS todo_item_tags (
                todo_id TEXT NOT NULL,
                tag_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                PRIMARY KEY(todo_id, tag_id),
                FOREIGN KEY(todo_id) REFERENCES todo_items(id),
                FOREIGN KEY(tag_id) REFERENCES todo_tags(id),
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_todo_tags_name
                ON todo_tags(workspace_id, name COLLATE NOCASE) WHERE deleted_at IS NULL;
            CREATE INDEX IF NOT EXISTS ix_todo_tags_workspace
                ON todo_tags(workspace_id, deleted_at, sort_key);
            CREATE INDEX IF NOT EXISTS ix_todo_items_workspace
                ON todo_items(workspace_id, deleted_at, is_completed, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_todo_item_tags_tag
                ON todo_item_tags(workspace_id, tag_id);
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ApplyVersion7Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('workspaces','todo_tags','todo_item_tags');";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) < 3) return;

        const string sql = """
            BEGIN;
            DELETE FROM todo_item_tags
            WHERE tag_id IN (SELECT id FROM todo_tags WHERE is_preset=1);
            DELETE FROM todo_tags WHERE is_preset=1;
            COMMIT;
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ApplyVersion8Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // 配置订阅同步（0.9.0）。旧库可能没有 collection_folders（未完整初始化的残缺库），此时只建新表。
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM pragma_table_info('collection_folders') WHERE name='managed_subscription_id';";
        var hasColumn = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0;
        await using var folders = connection.CreateCommand();
        folders.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='collection_folders';";
        var hasFolders = Convert.ToInt32(await folders.ExecuteScalarAsync(cancellationToken)) > 0;

        var sql = """
            BEGIN;
            CREATE TABLE IF NOT EXISTS published_config (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                folder_id TEXT NOT NULL,
                name TEXT NOT NULL,
                current_version INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                package_path TEXT NOT NULL,
                package_bytes INTEGER NOT NULL DEFAULT 0,
                manifest_json TEXT NOT NULL,
                auto_publish INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id)
            );
            CREATE TABLE IF NOT EXISTS paired_peer (
                id TEXT PRIMARY KEY,
                role INTEGER NOT NULL,
                name TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                last_address TEXT NULL,
                paired_at TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_paired_peer_active
                ON paired_peer(role, fingerprint) WHERE status=0;
            CREATE TABLE IF NOT EXISTS subscription (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                peer_id TEXT NOT NULL,
                published_id TEXT NOT NULL,
                name TEXT NOT NULL,
                local_version INTEGER NOT NULL,
                managed_folder_id TEXT NOT NULL,
                auto_update INTEGER NOT NULL DEFAULT 0,
                stay_on_current INTEGER NOT NULL DEFAULT 0,
                skipped_version INTEGER NULL,
                last_checked_at TEXT NULL,
                latest_known_version INTEGER NULL,
                last_error TEXT NULL,
                manifest_json TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(workspace_id) REFERENCES workspaces(id),
                FOREIGN KEY(peer_id) REFERENCES paired_peer(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_subscription_source
                ON subscription(workspace_id, peer_id, published_id);
            CREATE TABLE IF NOT EXISTS config_snapshot (
                subscription_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                snapshot_path TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(subscription_id, version),
                FOREIGN KEY(subscription_id) REFERENCES subscription(id) ON DELETE CASCADE
            );
            """;
        if (hasFolders && !hasColumn)
            sql += "ALTER TABLE collection_folders ADD COLUMN managed_subscription_id TEXT NULL;\n";
        sql += "COMMIT;";
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
