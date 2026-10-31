using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteClipboardHistoryRepository(SqliteConnectionFactory connectionFactory)
    : IClipboardHistoryRepository
{
    public async Task<ClipboardItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM clipboard_items WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<IReadOnlyList<ClipboardItem>> SearchAsync(
        ClipboardQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filters = new List<string>
        {
            "workspace_id = $workspaceId",
            "deleted_at IS NULL"
        };
        command.Parameters.AddWithValue("$workspaceId", query.WorkspaceId.ToString("D"));
        if (query.ItemType is not null)
        {
            if (query.ItemType == ClipboardItemType.Image)
            {
                filters.Add("item_type IN ($imageType, $imageFileType)");
                command.Parameters.AddWithValue("$imageType", (int)ClipboardItemType.Image);
                command.Parameters.AddWithValue("$imageFileType", (int)ClipboardItemType.ImageFile);
            }
            else
            {
                filters.Add("item_type = $itemType");
                command.Parameters.AddWithValue("$itemType", (int)query.ItemType.Value);
            }
        }

        if (query.LinksOnly)
        {
            // Same rule as the list's link icon: one http(s) URL with no whitespace inside, at most 2048 characters.
            filters.Add("item_type = $linkTextType");
            filters.Add("length(trim(COALESCE(text_content, preview_text, ''), char(32, 9, 10, 13))) <= 2048");
            filters.Add("(trim(COALESCE(text_content, preview_text, ''), char(32, 9, 10, 13)) LIKE 'http://%' OR trim(COALESCE(text_content, preview_text, ''), char(32, 9, 10, 13)) LIKE 'https://%')");
            filters.Add("trim(COALESCE(text_content, preview_text, ''), char(32, 9, 10, 13)) NOT GLOB '*[ ' || char(9, 10, 13) || ']*'");
            command.Parameters.AddWithValue("$linkTextType", (int)ClipboardItemType.Text);
        }

        if (query.FavoritesOnly)
        {
            filters.Add("is_favorite = 1");
        }

        if (query.PinnedOnly)
        {
            filters.Add("is_pinned = 1");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filters.Add("(text_content LIKE $search ESCAPE '\\' OR preview_text LIKE $search ESCAPE '\\' OR source_app LIKE $search ESCAPE '\\' OR source_process LIKE $search ESCAPE '\\')");
            command.Parameters.AddWithValue("$search", $"%{EscapeLike(query.SearchText.Trim())}%");
        }

        command.CommandText = $"""
            SELECT * FROM clipboard_items
            WHERE {string.Join(" AND ", filters)}
            ORDER BY is_pinned DESC, captured_at DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);

        var items = new List<ClipboardItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task UpsertAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clipboard_items (
                id, workspace_id, owner_id, device_id, item_type, content_hash,
                text_content, preview_text, source_app, source_process, created_at,
                updated_at, captured_at, last_used_at, deleted_at, is_favorite,
                is_pinned, use_count, visibility, sync_status, local_version,
                server_version, file_manifest, image_path, thumbnail_path, content_size)
            VALUES (
                $id, $workspaceId, $ownerId, $deviceId, $itemType, $contentHash,
                $textContent, $previewText, $sourceApp, $sourceProcess, $createdAt,
                $updatedAt, $capturedAt, $lastUsedAt, $deletedAt, $isFavorite,
                $isPinned, $useCount, $visibility, $syncStatus, $localVersion,
                $serverVersion, $fileManifest, $imagePath, $thumbnailPath, $contentSize)
            ON CONFLICT(id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                owner_id = excluded.owner_id,
                device_id = excluded.device_id,
                item_type = excluded.item_type,
                content_hash = excluded.content_hash,
                text_content = excluded.text_content,
                preview_text = excluded.preview_text,
                source_app = excluded.source_app,
                source_process = excluded.source_process,
                updated_at = excluded.updated_at,
                captured_at = excluded.captured_at,
                last_used_at = excluded.last_used_at,
                deleted_at = excluded.deleted_at,
                is_favorite = excluded.is_favorite,
                is_pinned = excluded.is_pinned,
                use_count = excluded.use_count,
                visibility = excluded.visibility,
                sync_status = excluded.sync_status,
                local_version = excluded.local_version,
                server_version = excluded.server_version,
                file_manifest = excluded.file_manifest,
                image_path = excluded.image_path,
                thumbnail_path = excluded.thumbnail_path,
                content_size = excluded.content_size;
            """;
        AddItemParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CaptureResult> CaptureTextAsync(
        TextCapture capture,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        ClipboardItem? existing;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = """
                SELECT * FROM clipboard_items
                WHERE workspace_id = $workspaceId AND item_type = $itemType AND content_hash = $hash
                ORDER BY deleted_at IS NULL DESC, captured_at DESC
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$workspaceId", capture.WorkspaceId.ToString("D"));
            find.Parameters.AddWithValue("$itemType", (int)ClipboardItemType.Text);
            find.Parameters.AddWithValue("$hash", capture.ContentHash);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            existing = await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
        }

        ClipboardItem item;
        CaptureDisposition disposition;
        if (existing is null)
        {
            item = new ClipboardItem
            {
                Id = Guid.NewGuid(),
                WorkspaceId = capture.WorkspaceId,
                OwnerId = capture.OwnerId,
                DeviceId = capture.DeviceId,
                ItemType = ClipboardItemType.Text,
                ContentHash = capture.ContentHash,
                TextContent = capture.Text,
                PreviewText = capture.Preview,
                SourceApp = capture.SourceApp,
                SourceProcess = capture.SourceProcess,
                CreatedAt = capture.CapturedAt,
                UpdatedAt = capture.CapturedAt,
                CapturedAt = capture.CapturedAt,
                ContentSize = capture.ContentSize
            };
            disposition = CaptureDisposition.Created;
        }
        else
        {
            disposition = existing.DeletedAt is null ? CaptureDisposition.Merged : CaptureDisposition.Restored;
            item = existing with
            {
                OwnerId = capture.OwnerId ?? existing.OwnerId,
                DeviceId = capture.DeviceId,
                TextContent = capture.Text,
                PreviewText = capture.Preview,
                SourceApp = capture.SourceApp,
                SourceProcess = capture.SourceProcess,
                UpdatedAt = capture.CapturedAt,
                CapturedAt = capture.CapturedAt,
                DeletedAt = null,
                ContentSize = capture.ContentSize,
                LocalVersion = existing.LocalVersion + 1,
                SyncStatus = SyncState.LocalOnly
            };
        }

        await UpsertWithinTransactionAsync(connection, (SqliteTransaction)transaction, item, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CaptureResult(disposition, item);
    }

    public Task SoftDeleteAsync(Guid workspaceId, Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) =>
        ExecuteWorkspaceUpdateAsync(
            "UPDATE clipboard_items SET deleted_at = $value, updated_at = $value, local_version = local_version + 1 WHERE workspace_id = $workspaceId AND id = $id AND deleted_at IS NULL;",
            workspaceId,
            id,
            deletedAt,
            cancellationToken);

    public Task SetFavoriteAsync(
        Guid workspaceId,
        Guid itemId,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        ExecuteWorkspaceUpdateAsync(
            "UPDATE clipboard_items SET is_favorite = $value, updated_at = $now, local_version = local_version + 1 WHERE workspace_id = $workspaceId AND id = $id AND deleted_at IS NULL;",
            workspaceId,
            itemId,
            isFavorite,
            cancellationToken);

    public Task SetPinnedAsync(
        Guid workspaceId,
        Guid itemId,
        bool isPinned,
        CancellationToken cancellationToken = default) =>
        ExecuteWorkspaceUpdateAsync(
            "UPDATE clipboard_items SET is_pinned = $value, updated_at = $now, local_version = local_version + 1 WHERE workspace_id = $workspaceId AND id = $id AND deleted_at IS NULL;",
            workspaceId,
            itemId,
            isPinned,
            cancellationToken);

    public Task MarkUsedAsync(
        Guid workspaceId,
        Guid itemId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken = default) =>
        ExecuteWorkspaceUpdateAsync(
            "UPDATE clipboard_items SET last_used_at = $value, use_count = use_count + 1, updated_at = $value, local_version = local_version + 1 WHERE workspace_id = $workspaceId AND id = $id AND deleted_at IS NULL;",
            workspaceId,
            itemId,
            usedAt,
            cancellationToken);

    public async Task<int> CleanupAsync(
        Guid workspaceId,
        DateTimeOffset olderThan,
        int maxActiveItems,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var total = 0;
        await using (var expired = connection.CreateCommand())
        {
            expired.Transaction = (SqliteTransaction)transaction;
            expired.CommandText = """
                UPDATE clipboard_items
                SET deleted_at = $deletedAt, updated_at = $deletedAt, local_version = local_version + 1
                WHERE workspace_id = $workspaceId AND deleted_at IS NULL
                  AND is_favorite = 0 AND is_pinned = 0 AND captured_at < $olderThan;
                """;
            expired.Parameters.AddWithValue("$deletedAt", Format(deletedAt));
            expired.Parameters.AddWithValue("$workspaceId", workspaceId.ToString("D"));
            expired.Parameters.AddWithValue("$olderThan", Format(olderThan));
            total += await expired.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var overflow = connection.CreateCommand())
        {
            overflow.Transaction = (SqliteTransaction)transaction;
            overflow.CommandText = """
                UPDATE clipboard_items
                SET deleted_at = $deletedAt, updated_at = $deletedAt, local_version = local_version + 1
                WHERE id IN (
                    SELECT id FROM clipboard_items
                    WHERE workspace_id = $workspaceId AND deleted_at IS NULL
                      AND is_favorite = 0 AND is_pinned = 0
                    ORDER BY captured_at DESC
                    LIMIT -1 OFFSET $maxItems
                );
                """;
            overflow.Parameters.AddWithValue("$deletedAt", Format(deletedAt));
            overflow.Parameters.AddWithValue("$workspaceId", workspaceId.ToString("D"));
            overflow.Parameters.AddWithValue("$maxItems", Math.Max(0, maxActiveItems));
            total += await overflow.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return total;
    }

    private async Task ExecuteWorkspaceUpdateAsync(
        string sql,
        Guid? workspaceId,
        Guid itemId,
        object value,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", itemId.ToString("D"));
        if (workspaceId is not null)
        {
            command.Parameters.AddWithValue("$workspaceId", workspaceId.Value.ToString("D"));
        }

        command.Parameters.AddWithValue("$value", value switch
        {
            bool boolean => boolean ? 1 : 0,
            DateTimeOffset dateTime => Format(dateTime),
            _ => value
        });
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task UpsertWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItem item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clipboard_items (
                id, workspace_id, owner_id, device_id, item_type, content_hash,
                text_content, preview_text, source_app, source_process, created_at,
                updated_at, captured_at, last_used_at, deleted_at, is_favorite,
                is_pinned, use_count, visibility, sync_status, local_version,
                server_version, file_manifest, image_path, thumbnail_path, content_size)
            VALUES (
                $id, $workspaceId, $ownerId, $deviceId, $itemType, $contentHash,
                $textContent, $previewText, $sourceApp, $sourceProcess, $createdAt,
                $updatedAt, $capturedAt, $lastUsedAt, $deletedAt, $isFavorite,
                $isPinned, $useCount, $visibility, $syncStatus, $localVersion,
                $serverVersion, $fileManifest, $imagePath, $thumbnailPath, $contentSize)
            ON CONFLICT(id) DO UPDATE SET
                owner_id = excluded.owner_id, device_id = excluded.device_id,
                text_content = excluded.text_content, preview_text = excluded.preview_text,
                source_app = excluded.source_app, source_process = excluded.source_process,
                updated_at = excluded.updated_at, captured_at = excluded.captured_at,
                deleted_at = excluded.deleted_at, content_size = excluded.content_size,
                local_version = excluded.local_version, sync_status = excluded.sync_status;
            """;
        AddItemParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddItemParameters(SqliteCommand command, ClipboardItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$workspaceId", item.WorkspaceId.ToString("D"));
        command.Parameters.AddWithValue("$ownerId", Db(item.OwnerId?.ToString("D")));
        command.Parameters.AddWithValue("$deviceId", item.DeviceId.ToString("D"));
        command.Parameters.AddWithValue("$itemType", (int)item.ItemType);
        command.Parameters.AddWithValue("$contentHash", item.ContentHash);
        command.Parameters.AddWithValue("$textContent", Db(item.TextContent));
        command.Parameters.AddWithValue("$previewText", Db(item.PreviewText));
        command.Parameters.AddWithValue("$sourceApp", Db(item.SourceApp));
        command.Parameters.AddWithValue("$sourceProcess", Db(item.SourceProcess));
        command.Parameters.AddWithValue("$createdAt", Format(item.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(item.UpdatedAt));
        command.Parameters.AddWithValue("$capturedAt", Format(item.CapturedAt));
        command.Parameters.AddWithValue("$lastUsedAt", Db(item.LastUsedAt is null ? null : Format(item.LastUsedAt.Value)));
        command.Parameters.AddWithValue("$deletedAt", Db(item.DeletedAt is null ? null : Format(item.DeletedAt.Value)));
        command.Parameters.AddWithValue("$isFavorite", item.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$isPinned", item.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$useCount", item.UseCount);
        command.Parameters.AddWithValue("$visibility", (int)item.Visibility);
        command.Parameters.AddWithValue("$syncStatus", (int)item.SyncStatus);
        command.Parameters.AddWithValue("$localVersion", item.LocalVersion);
        command.Parameters.AddWithValue("$serverVersion", Db(item.ServerVersion));
        command.Parameters.AddWithValue("$fileManifest", Db(item.FileManifest));
        command.Parameters.AddWithValue("$imagePath", Db(item.ImagePath));
        command.Parameters.AddWithValue("$thumbnailPath", Db(item.ThumbnailPath));
        command.Parameters.AddWithValue("$contentSize", item.ContentSize);
    }

    private static ClipboardItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        WorkspaceId = Guid.Parse(reader.GetString(reader.GetOrdinal("workspace_id"))),
        OwnerId = ReadGuid(reader, "owner_id"),
        DeviceId = Guid.Parse(reader.GetString(reader.GetOrdinal("device_id"))),
        ItemType = (ClipboardItemType)reader.GetInt32(reader.GetOrdinal("item_type")),
        ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
        TextContent = ReadString(reader, "text_content"),
        PreviewText = ReadString(reader, "preview_text"),
        SourceApp = ReadString(reader, "source_app"),
        SourceProcess = ReadString(reader, "source_process"),
        CreatedAt = ReadDate(reader, "created_at")!.Value,
        UpdatedAt = ReadDate(reader, "updated_at")!.Value,
        CapturedAt = ReadDate(reader, "captured_at")!.Value,
        LastUsedAt = ReadDate(reader, "last_used_at"),
        DeletedAt = ReadDate(reader, "deleted_at"),
        IsFavorite = reader.GetInt32(reader.GetOrdinal("is_favorite")) != 0,
        IsPinned = reader.GetInt32(reader.GetOrdinal("is_pinned")) != 0,
        UseCount = reader.GetInt64(reader.GetOrdinal("use_count")),
        Visibility = (ClipboardVisibility)reader.GetInt32(reader.GetOrdinal("visibility")),
        SyncStatus = (SyncState)reader.GetInt32(reader.GetOrdinal("sync_status")),
        LocalVersion = reader.GetInt64(reader.GetOrdinal("local_version")),
        ServerVersion = ReadLong(reader, "server_version"),
        FileManifest = ReadString(reader, "file_manifest"),
        ImagePath = ReadString(reader, "image_path"),
        ThumbnailPath = ReadString(reader, "thumbnail_path"),
        ContentSize = reader.GetInt64(reader.GetOrdinal("content_size"))
    };

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string? ReadString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? ReadGuid(SqliteDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return value is null ? null : Guid.Parse(value);
    }

    private static long? ReadLong(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, string name)
    {
        var value = ReadString(reader, name);
        return value is null ? null : DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }
}
