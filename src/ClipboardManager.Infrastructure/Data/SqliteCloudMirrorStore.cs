using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Data;

public sealed class SqliteCloudMirrorStore(SqliteConnectionFactory factory) : ICloudMirrorStore
{
    public async Task<CloudWorkspaceMapping> EnsureMappingAsync(CloudWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.Type == WorkspaceType.Local) throw new ArgumentException("云端 Workspace 不能是 Local。", nameof(workspace));
        var now = DateTimeOffset.UtcNow;
        await using var connection = factory.Create(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var createWorkspace = connection.CreateCommand())
        {
            createWorkspace.Transaction = (SqliteTransaction)transaction;
            createWorkspace.CommandText = """
                INSERT INTO workspaces(id,workspace_type,name,owner_id,created_at,updated_at)
                VALUES($id,$type,$name,NULL,$now,$now)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,workspace_type=excluded.workspace_type,updated_at=excluded.updated_at;
                """;
            createWorkspace.Parameters.AddWithValue("$id", workspace.Id.ToString("D"));
            createWorkspace.Parameters.AddWithValue("$type", (int)workspace.Type);
            createWorkspace.Parameters.AddWithValue("$name", workspace.Name);
            createWorkspace.Parameters.AddWithValue("$now", now.ToString("O"));
            await createWorkspace.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var mapping = connection.CreateCommand())
        {
            mapping.Transaction = (SqliteTransaction)transaction;
            mapping.CommandText = """
                INSERT INTO cloud_workspace_mappings(cloud_workspace_id,local_workspace_id,workspace_type,display_name,role,cache_state,created_at,updated_at)
                VALUES($id,$id,$type,$name,$role,'empty',$now,$now)
                ON CONFLICT(cloud_workspace_id) DO UPDATE SET workspace_type=excluded.workspace_type,
                  display_name=excluded.display_name,role=excluded.role,updated_at=excluded.updated_at;
                """;
            mapping.Parameters.AddWithValue("$id", workspace.Id.ToString("D"));
            mapping.Parameters.AddWithValue("$type", (int)workspace.Type);
            mapping.Parameters.AddWithValue("$name", workspace.Name);
            mapping.Parameters.AddWithValue("$role", workspace.Role.ToString().ToLowerInvariant());
            mapping.Parameters.AddWithValue("$now", now.ToString("O"));
            await mapping.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (await GetMappingAsync(workspace.Id, cancellationToken))!;
    }

    public async Task<CloudWorkspaceMapping?> GetMappingAsync(Guid cloudWorkspaceId, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_workspace_id,workspace_type,display_name,role,remote_cursor,cache_state,last_success_at,last_error_code FROM cloud_workspace_mappings WHERE cloud_workspace_id=$id";
        command.Parameters.AddWithValue("$id", cloudWorkspaceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(cloudWorkspaceId, Guid.Parse(reader.GetString(0)), (WorkspaceType)reader.GetInt32(1), reader.GetString(2),
            Enum.Parse<WorkspaceRole>(reader.GetString(3), true), reader.IsDBNull(4) ? null : reader.GetString(4),
            Enum.Parse<CloudCacheState>(reader.GetString(5), true), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task ApplyPullBatchAsync(Guid cloudWorkspaceId, IReadOnlyList<RemoteChange> changes, string nextCursor,
        DateTimeOffset synchronizedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Create(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var change in changes)
        {
            await using (var pending = connection.CreateCommand())
            {
                pending.Transaction = (SqliteTransaction)transaction;
                pending.CommandText = "SELECT EXISTS(SELECT 1 FROM sync_operations WHERE workspace_id=$w AND entity_type=$t AND entity_id=$id AND synced_at IS NULL)";
                pending.Parameters.AddWithValue("$w", cloudWorkspaceId.ToString("D"));
                pending.Parameters.AddWithValue("$t", change.EntityType);
                pending.Parameters.AddWithValue("$id", change.EntityId.ToString("D"));
                if (Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken)) != 0)
                    throw new InvalidOperationException("存在未推送的本地更改，暂不覆盖本地镜像或推进游标。");
            }
            if (change.EntityType == "collection_folder") await ApplyFolderAsync(connection, (SqliteTransaction)transaction, cloudWorkspaceId, change, cancellationToken);
            else if (change.EntityType == "collection_item") await ApplyItemAsync(connection, (SqliteTransaction)transaction, cloudWorkspaceId, change, cancellationToken);
            else throw new InvalidDataException($"不支持的云端实体类型：{change.EntityType}");
        }
        await using var cursor = connection.CreateCommand(); cursor.Transaction = (SqliteTransaction)transaction;
        cursor.CommandText = """
            UPDATE cloud_workspace_mappings SET remote_cursor=$cursor,cache_state='current',last_success_at=$at,
              last_error_code=NULL,updated_at=$at WHERE cloud_workspace_id=$id;
            """;
        cursor.Parameters.AddWithValue("$cursor", nextCursor); cursor.Parameters.AddWithValue("$at", synchronizedAt.ToString("O"));
        cursor.Parameters.AddWithValue("$id", cloudWorkspaceId.ToString("D"));
        if (await cursor.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("云端 Workspace 尚未建立本地映射。");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkStaleAsync(Guid cloudWorkspaceId, string? errorCode, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cloud_workspace_mappings SET cache_state='stale',last_error_code=$error,updated_at=$now WHERE cloud_workspace_id=$id";
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", cloudWorkspaceId.ToString("D")); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyFolderAsync(SqliteConnection c, SqliteTransaction tx, Guid workspaceId, RemoteChange change, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(change.Payload);
        var root = payload.RootElement; var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO collection_folders(id,workspace_id,parent_folder_id,name,sort_key,is_system,created_at,updated_at,deleted_at,local_version,server_version,sync_status)
            VALUES($id,$workspace,$parent,$name,$sort,$system,$created,$updated,$deleted,1,$version,0)
            ON CONFLICT(id) DO UPDATE SET parent_folder_id=excluded.parent_folder_id,name=excluded.name,sort_key=excluded.sort_key,
              updated_at=excluded.updated_at,deleted_at=excluded.deleted_at,server_version=excluded.server_version,sync_status=0
            WHERE collection_folders.workspace_id=excluded.workspace_id AND COALESCE(collection_folders.server_version,0)<=excluded.server_version;
            """;
        Add(command, "$id", change.EntityId.ToString("D")); Add(command, "$workspace", workspaceId.ToString("D"));
        Add(command, "$parent", StringOrNull(root, "parentFolderId")); Add(command, "$name", StringOr(root, "name", "未命名"));
        Add(command, "$sort", DoubleOr(root, "sortKey", 0)); Add(command, "$system", BoolOr(root, "isSystem", false) ? 1 : 0);
        Add(command, "$created", StringOr(root, "createdAt", now)); Add(command, "$updated", StringOr(root, "updatedAt", now));
        Add(command, "$deleted", change.IsDeleted ? now : null); Add(command, "$version", change.ServerVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyItemAsync(SqliteConnection c, SqliteTransaction tx, Guid workspaceId, RemoteChange change, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(change.Payload); var root = payload.RootElement; var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO collection_items(id,workspace_id,folder_id,item_type,title,text_content,url,description,local_path,thumbnail_path,file_manifest,content_hash,content_size,sort_key,created_at,updated_at,deleted_at,local_version,server_version,sync_status)
            VALUES($id,$workspace,$folder,$type,$title,$text,$url,$description,$path,$thumbnail,$manifest,$hash,$size,$sort,$created,$updated,$deleted,1,$version,0)
            ON CONFLICT(id) DO UPDATE SET folder_id=excluded.folder_id,item_type=excluded.item_type,title=excluded.title,
              text_content=excluded.text_content,url=excluded.url,description=excluded.description,local_path=excluded.local_path,
              thumbnail_path=excluded.thumbnail_path,file_manifest=excluded.file_manifest,content_hash=excluded.content_hash,
              content_size=excluded.content_size,sort_key=excluded.sort_key,updated_at=excluded.updated_at,
              deleted_at=excluded.deleted_at,server_version=excluded.server_version,sync_status=0
            WHERE collection_items.workspace_id=excluded.workspace_id AND COALESCE(collection_items.server_version,0)<=excluded.server_version;
            """;
        Add(command, "$id", change.EntityId.ToString("D")); Add(command, "$workspace", workspaceId.ToString("D")); Add(command, "$folder", RequiredString(root, "folderId"));
        Add(command, "$type", IntOr(root, "itemType", 0)); Add(command, "$title", StringOrNull(root, "title")); Add(command, "$text", StringOrNull(root, "textContent"));
        Add(command, "$url", StringOrNull(root, "url")); Add(command, "$description", StringOrNull(root, "description")); Add(command, "$path", StringOrNull(root, "localPath"));
        Add(command, "$thumbnail", StringOrNull(root, "thumbnailPath")); Add(command, "$manifest", RawOrNull(root, "fileManifest")); Add(command, "$hash", StringOrNull(root, "contentHash"));
        Add(command, "$size", LongOr(root, "contentSize", 0)); Add(command, "$sort", DoubleOr(root, "sortKey", 0)); Add(command, "$created", StringOr(root, "createdAt", now));
        Add(command, "$updated", StringOr(root, "updatedAt", now)); Add(command, "$deleted", change.IsDeleted ? now : null); Add(command, "$version", change.ServerVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string RequiredString(JsonElement e, string p) => e.TryGetProperty(p, out var v) && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : throw new InvalidDataException($"缺少字段 {p}。");
    private static string StringOr(JsonElement e, string p, string fallback) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static string? StringOrNull(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? RawOrNull(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind is not JsonValueKind.Null ? v.GetRawText() : null;
    private static bool BoolOr(JsonElement e, string p, bool fallback) => e.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
    private static int IntOr(JsonElement e, string p, int fallback) => e.TryGetProperty(p, out var v) && v.TryGetInt32(out var x) ? x : fallback;
    private static long LongOr(JsonElement e, string p, long fallback) => e.TryGetProperty(p, out var v) && v.TryGetInt64(out var x) ? x : fallback;
    private static double DoubleOr(JsonElement e, string p, double fallback) => e.TryGetProperty(p, out var v) && v.TryGetDouble(out var x) ? x : fallback;
}
