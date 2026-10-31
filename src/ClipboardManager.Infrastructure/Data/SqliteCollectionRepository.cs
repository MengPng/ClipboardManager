using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteCollectionRepository(SqliteConnectionFactory connectionFactory) : ICollectionRepository
{
    public async Task<CollectionFolder> EnsureUncategorizedAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT * FROM collection_folders WHERE workspace_id=$workspace AND is_system=1 AND deleted_at IS NULL LIMIT 1;";
        find.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken)) return ReadFolder(reader);
        }

        var now = DateTimeOffset.UtcNow;
        var folder = new CollectionFolder
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "未分类", SortKey = 0,
            IsSystem = true, CreatedAt = now, UpdatedAt = now
        };
        await InsertFolderAsync(connection, folder, cancellationToken, transaction);
        await QueueFolderAsync(connection, transaction, folder.WorkspaceId, folder.Id, SyncOperationType.Create, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return folder;
    }

    public async Task<IReadOnlyList<CollectionFolder>> GetFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM collection_folders WHERE workspace_id=$workspace AND deleted_at IS NULL ORDER BY sort_key, created_at, id;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        var result = new List<CollectionFolder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadFolder(reader));
        return result;
    }

    public async Task<CollectionFolder> CreateFolderAsync(Guid workspaceId, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureUniqueNameAsync(connection, workspaceId, name, null, cancellationToken, transaction);
        var ordered = await ReadOrderAsync(connection, transaction, workspaceId, null, cancellationToken);
        var key = await GetInsertionKeyAsync(connection, transaction, workspaceId, null, ordered, ordered.Count, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var folder = new CollectionFolder
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = name, SortKey = key,
            CreatedAt = now, UpdatedAt = now
        };
        await InsertFolderAsync(connection, folder, cancellationToken, transaction);
        await QueueFolderAsync(connection, transaction, folder.WorkspaceId, folder.Id, SyncOperationType.Create, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return folder;
    }

    public async Task RenameFolderAsync(Guid workspaceId, Guid folderId, string name, CancellationToken cancellationToken = default)
    {
        name = ValidateName(name);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureWritableFolderAsync(connection, transaction, workspaceId, folderId, cancellationToken);
        await EnsureUniqueNameAsync(connection, workspaceId, name, folderId, cancellationToken, transaction);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE collection_folders SET name=$name,updated_at=$now,local_version=local_version+1 WHERE workspace_id=$workspace AND id=$id AND is_system=0 AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$name", name);
        AddIdentity(command, workspaceId, folderId);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await QueueFolderAsync(connection, transaction, workspaceId, folderId, SyncOperationType.Update, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteFolderAsync(Guid workspaceId, Guid folderId, Guid fallbackFolderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureWritableFolderAsync(connection, transaction, workspaceId, folderId, cancellationToken);
        if (await RequireFolderAsync(connection, transaction, workspaceId, folderId, cancellationToken))
            throw new InvalidOperationException("系统文件夹不能删除。");
        await EnsureWritableFolderAsync(connection, transaction, workspaceId, fallbackFolderId, cancellationToken);
        if (!await RequireFolderAsync(connection, transaction, workspaceId, fallbackFolderId, cancellationToken))
            throw new InvalidOperationException("回退目标必须是同一空间的系统文件夹。");
        var source = await ReadOrderAsync(connection, transaction, workspaceId, folderId, cancellationToken);
        var target = await ReadOrderAsync(connection, transaction, workspaceId, fallbackFolderId, cancellationToken);
        foreach (var item in source)
        {
            var key = await GetInsertionKeyAsync(connection, transaction, workspaceId, fallbackFolderId, target, target.Count, cancellationToken);
            await SavePositionAsync(connection, transaction, workspaceId, fallbackFolderId, item.Id, key, cancellationToken);
            target.Add((item.Id, (double)key));
        }
        var now = Format(DateTimeOffset.UtcNow);
        await ExecuteAsync(connection, transaction,
            "UPDATE collection_folders SET deleted_at=$now,updated_at=$now,local_version=local_version+1 WHERE workspace_id=$workspace AND id=$folder AND is_system=0 AND deleted_at IS NULL;",
            cancellationToken, ("$now", now), ("$workspace", workspaceId.ToString("D")), ("$folder", folderId.ToString("D")));
        await QueueFolderAsync(connection, transaction, workspaceId, folderId, SyncOperationType.Delete, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReorderFolderAsync(Guid workspaceId, Guid folderId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await RequireFolderAsync(connection, transaction, workspaceId, folderId, cancellationToken);
        var ordered = await ReadOrderAsync(connection, transaction, workspaceId, null, cancellationToken);
        var current = ordered.FindIndex(x => x.Id == folderId);
        targetIndex = Math.Clamp(targetIndex, 0, ordered.Count - 1);
        if (current == targetIndex) return;
        ordered.RemoveAt(current);
        var key = await GetInsertionKeyAsync(connection, transaction, workspaceId, null, ordered, targetIndex, cancellationToken);
        await SavePositionAsync(connection, transaction, workspaceId, null, folderId, key, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid workspaceId, Guid folderId, string? searchText = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM collection_items WHERE workspace_id=$workspace AND folder_id=$folder AND deleted_at IS NULL" +
            (string.IsNullOrWhiteSpace(searchText) ? "" : " AND (title LIKE $search OR text_content LIKE $search OR url LIKE $search OR description LIKE $search)") +
            " ORDER BY sort_key, created_at, id;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$folder", folderId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(searchText)) command.Parameters.AddWithValue("$search", $"%{searchText.Trim()}%");
        var result = new List<CollectionItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadItem(reader));
        return result;
    }

    public async Task<CollectionItem> CreateItemAsync(Guid workspaceId, CreateCollectionItem input, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await RequireFolderAsync(connection, transaction, workspaceId, input.FolderId, cancellationToken);
        await EnsureWritableFolderAsync(connection, transaction, workspaceId, input.FolderId, cancellationToken);
        await RequireSourceAsync(connection, transaction, input.SourceWorkspaceId ?? workspaceId, input.SourceClipboardItemId, cancellationToken);
        var ordered = await ReadOrderAsync(connection, transaction, workspaceId, input.FolderId, cancellationToken);
        var key = await GetInsertionKeyAsync(connection, transaction, workspaceId, input.FolderId, ordered, ordered.Count, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var item = new CollectionItem
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, FolderId = input.FolderId,
            SourceClipboardItemId = input.SourceClipboardItemId, ItemType = input.ItemType,
            Title = input.Title, TextContent = input.TextContent, Url = input.Url, Description = input.Description,
            LocalPath = input.LocalPath, ThumbnailPath = input.ThumbnailPath, FileManifest = input.FileManifest,
            ContentHash = input.ContentHash, ContentSize = input.ContentSize, SortKey = key,
            CreatedAt = now, UpdatedAt = now
        };
        await WriteItemAsync(connection, transaction, item, false, cancellationToken);
        await QueueItemAsync(connection, transaction, workspaceId, item.Id, SyncOperationType.Create, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    public async Task UpdateItemAsync(CollectionItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await RequireFolderAsync(connection, transaction, item.WorkspaceId, item.FolderId, cancellationToken);
        await EnsureWritableFolderAsync(connection, transaction, item.WorkspaceId, item.FolderId, cancellationToken);
        await WriteItemAsync(connection, transaction, item with { UpdatedAt = DateTimeOffset.UtcNow }, true, cancellationToken);
        await QueueItemAsync(connection, transaction, item.WorkspaceId, item.Id, SyncOperationType.Update, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MoveItemAsync(Guid workspaceId, Guid itemId, Guid targetFolderId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await RequireFolderAsync(connection, transaction, workspaceId, targetFolderId, cancellationToken);
        await EnsureWritableFolderAsync(connection, transaction, workspaceId, targetFolderId, cancellationToken);
        await EnsureWritableItemAsync(connection, transaction, workspaceId, itemId, cancellationToken);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT folder_id FROM collection_items WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        AddIdentity(find, workspaceId, itemId);
        var sourceFolder = await find.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("收藏项不存在或不属于当前空间。");
        var ordered = await ReadOrderAsync(connection, transaction, workspaceId, targetFolderId, cancellationToken);
        var current = ordered.FindIndex(x => x.Id == itemId);
        if (current >= 0) ordered.RemoveAt(current);
        targetIndex = Math.Clamp(targetIndex, 0, ordered.Count);
        if (sourceFolder == targetFolderId.ToString("D") && current == targetIndex) return;
        var key = await GetInsertionKeyAsync(connection, transaction, workspaceId, targetFolderId, ordered, targetIndex, cancellationToken);
        await SavePositionAsync(connection, transaction, workspaceId, targetFolderId, itemId, key, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureWritableItemAsync(connection, transaction, workspaceId, itemId, cancellationToken);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE collection_items SET deleted_at=$now,updated_at=$now,local_version=local_version+1 WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        AddIdentity(command, workspaceId, itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await QueueItemAsync(connection, transaction, workspaceId, itemId, SyncOperationType.Delete, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<bool> RequireFolderAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, Guid folderId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_system FROM collection_folders WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        AddIdentity(command, workspaceId, folderId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null) throw new InvalidOperationException("文件夹不存在或不属于当前空间。");
        return Convert.ToInt32(result) != 0;
    }

    private const string ManagedReadOnlyMessage = "受管文件夹来自订阅，内容只读。可以“复制到我的配置”后再修改。";

    /// <summary>订阅产生的受管文件夹只允许订阅服务改写，用户路径一律拒绝。</summary>
    private static async Task EnsureWritableFolderAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, Guid folderId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT managed_subscription_id FROM collection_folders WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        AddIdentity(command, workspaceId, folderId);
        var managed = await command.ExecuteScalarAsync(cancellationToken);
        if (managed is string { Length: > 0 }) throw new InvalidOperationException(ManagedReadOnlyMessage);
    }

    private static async Task EnsureWritableItemAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT f.managed_subscription_id FROM collection_items i JOIN collection_folders f ON f.id=i.folder_id WHERE i.workspace_id=$workspace AND i.id=$id AND i.deleted_at IS NULL;";
        AddIdentity(command, workspaceId, itemId);
        var managed = await command.ExecuteScalarAsync(cancellationToken);
        if (managed is string { Length: > 0 }) throw new InvalidOperationException(ManagedReadOnlyMessage);
    }

    /// <summary>
    /// Validates the provenance record against the workspace the HISTORY belongs to,
    /// which is not necessarily the workspace receiving the snapshot.
    /// </summary>
    private static async Task RequireSourceAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid sourceWorkspaceId, Guid? sourceId, CancellationToken cancellationToken)
    {
        if (sourceId is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM clipboard_items WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;";
        AddIdentity(command, sourceWorkspaceId, sourceId.Value);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("来源历史不存在或不属于来源空间。");
    }

    private static async Task<List<(Guid Id, double Key)>> ReadOrderAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid workspaceId, Guid? folderId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = folderId is null
            ? "SELECT id,sort_key FROM collection_folders WHERE workspace_id=$workspace AND deleted_at IS NULL ORDER BY sort_key,created_at,id;"
            : "SELECT id,sort_key FROM collection_items WHERE workspace_id=$workspace AND folder_id=$folder AND deleted_at IS NULL ORDER BY sort_key,created_at,id;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        if (folderId is not null) command.Parameters.AddWithValue("$folder", folderId.Value.ToString("D"));
        var result = new List<(Guid, double)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add((Guid.Parse(reader.GetString(0)), reader.GetDouble(1)));
        return result;
    }

    private static async Task<decimal> GetInsertionKeyAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, Guid? folderId, List<(Guid Id, double Key)> ordered, int index, CancellationToken cancellationToken)
    {
        // REAL is binary64. Keep generated keys integral and well inside both its
        // exact-integer range and the decimal reader's 15-digit conversion precision.
        // Rebalance only when the insertion gap (or the safe range) is exhausted.
        decimal? Candidate()
        {
            var left = index > 0 ? ordered[index - 1].Key : (double?)null;
            var right = index < ordered.Count ? ordered[index].Key : (double?)null;
            var key = left is null ? (right is null ? 1024d : right.Value - 1024d)
                : right is null ? left.Value + 1024d : Math.Floor(left.Value / 2d + right.Value / 2d);
            if (!double.IsFinite(key) || Math.Abs(key) > 1_000_000_000_000d ||
                (left is not null && key <= left.Value) || (right is not null && key >= right.Value)) return null;
            return (decimal)key;
        }
        if (Candidate() is { } candidate) return candidate;
        for (var i = 0; i < ordered.Count; i++)
        {
            var key = (i + 1) * 1024m;
            if (ordered[i].Key != (double)key)
                await SavePositionAsync(connection, transaction, workspaceId, folderId, ordered[i].Id, key, cancellationToken);
            ordered[i] = (ordered[i].Id, (double)key);
        }
        return Candidate() ?? throw new InvalidOperationException("无法生成安全的排序键。");
    }

    private static async Task SavePositionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workspaceId, Guid? folderId, Guid id, decimal key, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, folderId is null
            ? "UPDATE collection_folders SET sort_key=$key,updated_at=$now,local_version=local_version+1 WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;"
            : "UPDATE collection_items SET folder_id=$folder,sort_key=$key,updated_at=$now,local_version=local_version+1 WHERE workspace_id=$workspace AND id=$id AND deleted_at IS NULL;",
            cancellationToken, ("$key", (double)key), ("$now", Format(DateTimeOffset.UtcNow)),
            ("$workspace", workspaceId.ToString("D")), ("$id", id.ToString("D")), ("$folder", Db(folderId?.ToString("D"))));
        if (folderId is null) await QueueFolderAsync(connection, transaction, workspaceId, id, SyncOperationType.Update, cancellationToken);
        else await QueueItemAsync(connection, transaction, workspaceId, id, SyncOperationType.Update, cancellationToken);
    }

    private static async Task InsertFolderAsync(SqliteConnection connection, CollectionFolder folder, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO collection_folders (id,workspace_id,parent_folder_id,name,sort_key,is_system,created_at,updated_at,deleted_at,local_version,server_version,sync_status) VALUES ($id,$workspace,$parent,$name,$sort,$system,$created,$updated,NULL,1,NULL,0);";
        command.Parameters.AddWithValue("$id", folder.Id.ToString("D"));
        command.Parameters.AddWithValue("$workspace", folder.WorkspaceId.ToString("D"));
        command.Parameters.AddWithValue("$parent", Db(folder.ParentFolderId?.ToString("D")));
        command.Parameters.AddWithValue("$name", folder.Name);
        command.Parameters.AddWithValue("$sort", folder.SortKey);
        command.Parameters.AddWithValue("$system", folder.IsSystem ? 1 : 0);
        command.Parameters.AddWithValue("$created", Format(folder.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(folder.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteItemAsync(SqliteConnection connection, SqliteTransaction transaction, CollectionItem item, bool update, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Editing is not insertion, undelete, ownership transfer, or reordering.
        // Folder changes use MoveItemAsync so placement remains transactional.
        command.CommandText = update ? """
            UPDATE collection_items SET title=$title,text_content=$text,url=$url,description=$description,
                local_path=$path,thumbnail_path=$thumb,file_manifest=$manifest,content_hash=$hash,
                content_size=$size,updated_at=$updated,local_version=local_version+1
            WHERE id=$id AND workspace_id=$workspace AND folder_id=$folder AND deleted_at IS NULL;
            """ : """
            INSERT INTO collection_items (id,workspace_id,folder_id,source_clipboard_item_id,item_type,title,text_content,url,description,local_path,thumbnail_path,file_manifest,content_hash,content_size,sort_key,created_at,updated_at,deleted_at,local_version,server_version,sync_status)
            VALUES ($id,$workspace,$folder,$source,$type,$title,$text,$url,$description,$path,$thumb,$manifest,$hash,$size,$sort,$created,$updated,$deleted,$version,$server,0);
            """;
        foreach (var pair in new (string, object?)[]
        {
            ("$id",item.Id.ToString("D")),("$workspace",item.WorkspaceId.ToString("D")),("$folder",item.FolderId.ToString("D")),("$source",item.SourceClipboardItemId?.ToString("D")),
            ("$type",(int)item.ItemType),("$title",item.Title),("$text",item.TextContent),("$url",item.Url),("$description",item.Description),("$path",item.LocalPath),("$thumb",item.ThumbnailPath),("$manifest",item.FileManifest),("$hash",item.ContentHash),("$size",item.ContentSize),("$sort",item.SortKey),("$created",Format(item.CreatedAt)),("$updated",Format(item.UpdatedAt)),("$deleted",item.DeletedAt is null?null:Format(item.DeletedAt.Value)),("$version",item.LocalVersion),("$server",item.ServerVersion)
        }) command.Parameters.AddWithValue(pair.Item1, Db(pair.Item2));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("收藏项不存在、已删除或不属于指定空间和文件夹。");
    }

    private static async Task<bool> IsCloudWorkspaceAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId, CancellationToken ct)
    {
        await using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="SELECT role FROM cloud_workspace_mappings WHERE cloud_workspace_id=$id";
        command.Parameters.AddWithValue("$id",workspaceId.ToString("D"));
        var role = await command.ExecuteScalarAsync(ct) as string;
        if (role == "member") throw new UnauthorizedAccessException("当前成员只有读取权限。");
        return role is not null;
    }

    private static async Task QueueFolderAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId, Guid id, SyncOperationType type, CancellationToken ct)
    {
        if (!await IsCloudWorkspaceAsync(connection,transaction,workspaceId,ct)) return;
        await using var read=connection.CreateCommand(); read.Transaction=transaction;
        read.CommandText="SELECT parent_folder_id,name,sort_key,is_system,created_at,updated_at,deleted_at,COALESCE(server_version,0) FROM collection_folders WHERE workspace_id=$w AND id=$id";
        read.Parameters.AddWithValue("$w",workspaceId.ToString("D")); read.Parameters.AddWithValue("$id",id.ToString("D"));
        await using var r=await read.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return;
        var payload=JsonSerializer.Serialize(new { parentFolderId=r.IsDBNull(0)?null:r.GetString(0), name=r.GetString(1), sortKey=r.GetDouble(2), isSystem=r.GetInt32(3)!=0, scopeAllMembers=true, createdAt=r.GetString(4), updatedAt=r.GetString(5) });
        var baseVersion=r.GetInt64(7); await r.DisposeAsync();
        await InsertOperationAsync(connection,transaction,workspaceId,"collection_folder",id,type,baseVersion,payload,ct);
        await ExecuteAsync(connection,transaction,"UPDATE collection_folders SET sync_status=1 WHERE workspace_id=$workspace AND id=$id",ct,("$workspace",workspaceId.ToString("D")),("$id",id.ToString("D")));
    }

    private static async Task QueueItemAsync(SqliteConnection connection, SqliteTransaction transaction, Guid workspaceId, Guid id, SyncOperationType type, CancellationToken ct)
    {
        if (!await IsCloudWorkspaceAsync(connection,transaction,workspaceId,ct)) return;
        await using var read=connection.CreateCommand(); read.Transaction=transaction;
        read.CommandText="SELECT folder_id,item_type,title,text_content,url,description,local_path,thumbnail_path,file_manifest,content_hash,content_size,sort_key,created_at,updated_at,COALESCE(server_version,0) FROM collection_items WHERE workspace_id=$w AND id=$id";
        read.Parameters.AddWithValue("$w",workspaceId.ToString("D")); read.Parameters.AddWithValue("$id",id.ToString("D"));
        await using var r=await read.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return;
        string? S(int n)=>r.IsDBNull(n)?null:r.GetString(n);
        var payload=JsonSerializer.Serialize(new { folderId=r.GetString(0), itemType=r.GetInt32(1), title=S(2), textContent=S(3), url=S(4), description=S(5), localPath=S(6), thumbnailPath=S(7), fileManifest=S(8), contentHash=S(9), contentSize=r.GetInt64(10), sortKey=r.GetDouble(11), createdAt=r.GetString(12), updatedAt=r.GetString(13) });
        var baseVersion=r.GetInt64(14); await r.DisposeAsync();
        await InsertOperationAsync(connection,transaction,workspaceId,"collection_item",id,type,baseVersion,payload,ct);
        await ExecuteAsync(connection,transaction,"UPDATE collection_items SET sync_status=1 WHERE workspace_id=$workspace AND id=$id",ct,("$workspace",workspaceId.ToString("D")),("$id",id.ToString("D")));
    }

    private static async Task InsertOperationAsync(SqliteConnection connection,SqliteTransaction transaction,Guid workspaceId,string entityType,Guid entityId,SyncOperationType type,long baseVersion,string payload,CancellationToken ct)
    {
        await using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="INSERT INTO sync_operations(id,workspace_id,entity_type,entity_id,operation_type,base_version,payload,created_at) VALUES($id,$w,$type,$entity,$op,$base,$payload,$created)";
        command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$w",workspaceId.ToString("D")); command.Parameters.AddWithValue("$type",entityType);
        command.Parameters.AddWithValue("$entity",entityId.ToString("D")); command.Parameters.AddWithValue("$op",(int)type); command.Parameters.AddWithValue("$base",baseVersion); command.Parameters.AddWithValue("$payload",payload); command.Parameters.AddWithValue("$created",Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static CollectionFolder ReadFolder(SqliteDataReader r) => new()
    {
        Id=Guid.Parse(r.GetString(r.GetOrdinal("id"))), WorkspaceId=Guid.Parse(r.GetString(r.GetOrdinal("workspace_id"))), ParentFolderId=GuidOrNull(r,"parent_folder_id"), Name=r.GetString(r.GetOrdinal("name")), SortKey=Convert.ToDecimal(r.GetDouble(r.GetOrdinal("sort_key"))), IsSystem=r.GetInt32(r.GetOrdinal("is_system"))!=0, ManagedSubscriptionId=GuidOrNull(r,"managed_subscription_id"), CreatedAt=Date(r,"created_at")!.Value, UpdatedAt=Date(r,"updated_at")!.Value, DeletedAt=Date(r,"deleted_at"), LocalVersion=r.GetInt64(r.GetOrdinal("local_version")), ServerVersion=LongOrNull(r,"server_version"), SyncStatus=(SyncState)r.GetInt32(r.GetOrdinal("sync_status"))
    };

    private static CollectionItem ReadItem(SqliteDataReader r) => new()
    {
        Id=Guid.Parse(r.GetString(r.GetOrdinal("id"))), WorkspaceId=Guid.Parse(r.GetString(r.GetOrdinal("workspace_id"))), FolderId=Guid.Parse(r.GetString(r.GetOrdinal("folder_id"))), SourceClipboardItemId=GuidOrNull(r,"source_clipboard_item_id"), ItemType=(CollectionItemType)r.GetInt32(r.GetOrdinal("item_type")), Title=StringOrNull(r,"title"), TextContent=StringOrNull(r,"text_content"), Url=StringOrNull(r,"url"), Description=StringOrNull(r,"description"), LocalPath=StringOrNull(r,"local_path"), ThumbnailPath=StringOrNull(r,"thumbnail_path"), FileManifest=StringOrNull(r,"file_manifest"), ContentHash=StringOrNull(r,"content_hash"), ContentSize=r.GetInt64(r.GetOrdinal("content_size")), SortKey=Convert.ToDecimal(r.GetDouble(r.GetOrdinal("sort_key"))), CreatedAt=Date(r,"created_at")!.Value, UpdatedAt=Date(r,"updated_at")!.Value, DeletedAt=Date(r,"deleted_at"), LocalVersion=r.GetInt64(r.GetOrdinal("local_version")), ServerVersion=LongOrNull(r,"server_version"), SyncStatus=(SyncState)r.GetInt32(r.GetOrdinal("sync_status"))
    };

    private static async Task EnsureUniqueNameAsync(SqliteConnection connection, Guid workspaceId, string name, Guid? exceptId, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="SELECT COUNT(*) FROM collection_folders WHERE workspace_id=$workspace AND name=$name COLLATE NOCASE AND deleted_at IS NULL AND ($except IS NULL OR id<>$except);"; command.Parameters.AddWithValue("$workspace",workspaceId.ToString("D")); command.Parameters.AddWithValue("$name",name); command.Parameters.AddWithValue("$except",Db(exceptId?.ToString("D"))); if(Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken))>0) throw new InvalidOperationException("同级文件夹名称不能重复。");
    }
    private static string ValidateName(string name) { name=name?.Trim()??""; if(name.Length is <1 or >80) throw new ArgumentException("文件夹名称长度必须为 1 到 80 个字符。",nameof(name)); return name; }
    private static void AddIdentity(SqliteCommand c,Guid w,Guid id){c.Parameters.AddWithValue("$workspace",w.ToString("D"));c.Parameters.AddWithValue("$id",id.ToString("D"));}
    private static async Task ExecuteAsync(SqliteConnection c,SqliteTransaction t,string sql,CancellationToken ct,params (string,object)[] p){await using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Item1,x.Item2);await cmd.ExecuteNonQueryAsync(ct);}
    private static object Db(object? v)=>v??DBNull.Value; private static string Format(DateTimeOffset v)=>v.ToUniversalTime().ToString("O");
    private static string? StringOrNull(SqliteDataReader r,string n){var o=r.GetOrdinal(n);return r.IsDBNull(o)?null:r.GetString(o);} private static Guid? GuidOrNull(SqliteDataReader r,string n){var s=StringOrNull(r,n);return s is null?null:Guid.Parse(s);} private static long? LongOrNull(SqliteDataReader r,string n){var o=r.GetOrdinal(n);return r.IsDBNull(o)?null:r.GetInt64(o);} private static DateTimeOffset? Date(SqliteDataReader r,string n){var s=StringOrNull(r,n);return s is null?null:DateTimeOffset.Parse(s,null,DateTimeStyles.RoundtripKind);}
}
