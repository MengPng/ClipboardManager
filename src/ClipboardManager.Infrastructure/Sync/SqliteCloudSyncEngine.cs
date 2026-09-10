using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace ClipboardManager.Infrastructure.Sync;

public sealed class SqliteCloudSyncEngine(
    SqliteConnectionFactory factory,
    IRemoteSyncProvider remote,
    ICloudMirrorStore mirrors) : ISyncEngine
{
    private readonly ConcurrentDictionary<Guid, SyncStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SyncStatus GetStatus(Guid workspaceId) => _statuses.TryGetValue(workspaceId, out var value)
        ? value : new(workspaceId, SyncState.Pending, "等待首次同步", null);

    public async Task<SyncResult> SynchronizeAsync(Guid workspaceId, SyncReason reason, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var mapping = await mirrors.GetMappingAsync(workspaceId, cancellationToken)
                ?? throw new InvalidOperationException("该云端空间尚未建立本地映射。");
            Set(workspaceId, SyncState.Pending, "正在同步");
            var pending = await ReadPendingAsync(workspaceId, cancellationToken);
            var pushed = 0;
            while (pending.Count > 0)
            {
                var result = await remote.PushAsync(pending, cancellationToken);
                pushed += await MarkPushedAsync(workspaceId, result.ServerVersions, cancellationToken);
                if (!result.Succeeded)
                {
                    await SaveConflictsAsync(workspaceId, result.Conflicts ?? [], cancellationToken);
                    return new(false, Set(workspaceId, SyncState.Conflict, "存在待处理冲突"), pushed);
                }
                if (result.ServerVersions.Count == 0) throw new InvalidDataException("服务端未确认操作。");
                pending = await ReadPendingAsync(workspaceId, cancellationToken);
            }

            var cursor = mapping.RemoteCursor;
            var pulled = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pull = await remote.PullAsync(workspaceId, cursor, cancellationToken);
                if (!pull.Succeeded) throw new InvalidOperationException(pull.Error ?? "云端拉取失败。");
                var next = pull.NextCursor ?? cursor ?? "0";
                if (pull.Changes.Count > 0 && next == cursor) throw new InvalidDataException("云端游标未推进。");
                await mirrors.ApplyPullBatchAsync(workspaceId, pull.Changes, next, DateTimeOffset.UtcNow, cancellationToken);
                pulled += pull.Changes.Count;
                cursor = next;
                if (pull.Changes.Count < 200) break;
            }
            var state = await CountConflictsAsync(workspaceId, cancellationToken) > 0 ? SyncState.Conflict : SyncState.Synced;
            var status = Set(workspaceId, state, state == SyncState.Conflict ? "存在待处理冲突" : "已同步");
            return new(state != SyncState.Conflict, status, pushed, pulled);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await mirrors.MarkStaleAsync(workspaceId, ex.GetType().Name, CancellationToken.None);
            var status = Set(workspaceId, SyncState.Failed, "离线或同步失败");
            return new(false, status);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<SyncOperation>> ReadPendingAsync(Guid workspaceId, CancellationToken ct)
    {
        await using var c=factory.Create(); await c.OpenAsync(ct); await using var command=c.CreateCommand();
        command.CommandText="""
            SELECT id,entity_type,entity_id,operation_type,base_version,payload,created_at
            FROM sync_operations WHERE workspace_id=$w AND synced_at IS NULL
              AND error_code IS NULL
              AND (next_attempt_at IS NULL OR next_attempt_at<=$now) ORDER BY rowid LIMIT 1
            """;
        command.Parameters.AddWithValue("$w",workspaceId.ToString("D")); command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        var result=new List<SyncOperation>(); await using var r=await command.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) result.Add(new(Guid.Parse(r.GetString(0)),workspaceId,r.GetString(1),Guid.Parse(r.GetString(2)),(SyncOperationType)r.GetInt32(3),r.GetInt64(4),r.GetString(5),DateTimeOffset.Parse(r.GetString(6))));
        return result;
    }

    private async Task<int> MarkPushedAsync(Guid workspaceId,IReadOnlyDictionary<Guid,long> versions,CancellationToken ct)
    {
        if(versions.Count==0)return 0; await using var c=factory.Create(); await c.OpenAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        foreach(var pair in versions)
        {
            await using var get=c.CreateCommand(); get.Transaction=(SqliteTransaction)tx; get.CommandText="SELECT entity_type,entity_id FROM sync_operations WHERE id=$id AND workspace_id=$w";
            get.Parameters.AddWithValue("$id",pair.Key.ToString("D"));get.Parameters.AddWithValue("$w",workspaceId.ToString("D")); string? type=null,id=null;
            await using(var r=await get.ExecuteReaderAsync(ct)){if(await r.ReadAsync(ct)){type=r.GetString(0);id=r.GetString(1);}}
            if(type is null)continue;
            await using var mark=c.CreateCommand();mark.Transaction=(SqliteTransaction)tx;mark.CommandText="UPDATE sync_operations SET synced_at=$now,last_error=NULL,error_code=NULL WHERE id=$id";mark.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));mark.Parameters.AddWithValue("$id",pair.Key.ToString("D"));await mark.ExecuteNonQueryAsync(ct);
            await using var entity=c.CreateCommand();entity.Transaction=(SqliteTransaction)tx;entity.CommandText=type=="collection_folder"?"UPDATE collection_folders SET server_version=$v,sync_status=2 WHERE workspace_id=$w AND id=$id":"UPDATE collection_items SET server_version=$v,sync_status=2 WHERE workspace_id=$w AND id=$id";
            entity.Parameters.AddWithValue("$v",pair.Value);entity.Parameters.AddWithValue("$w",workspaceId.ToString("D"));entity.Parameters.AddWithValue("$id",id);await entity.ExecuteNonQueryAsync(ct);
            await using var next=c.CreateCommand(); next.Transaction=(SqliteTransaction)tx;
            next.CommandText="UPDATE sync_operations SET base_version=$v WHERE id=(SELECT id FROM sync_operations WHERE workspace_id=$w AND entity_type=$t AND entity_id=$id AND synced_at IS NULL ORDER BY rowid LIMIT 1)";
            next.Parameters.AddWithValue("$v",pair.Value);next.Parameters.AddWithValue("$w",workspaceId.ToString("D"));next.Parameters.AddWithValue("$t",type);next.Parameters.AddWithValue("$id",id);await next.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return versions.Count;
    }

    private async Task SaveConflictsAsync(Guid workspaceId,IReadOnlyList<RemoteConflict> conflicts,CancellationToken ct)
    {
        await using var c=factory.Create();await c.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        foreach (var conflict in conflicts)
        {
            await using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT OR IGNORE INTO sync_conflicts(id,workspace_id,entity_type,entity_id,local_payload,remote_payload,local_version,server_version,detected_at)
                SELECT id,workspace_id,entity_type,entity_id,payload,$remote,base_version,$version,$now
                FROM sync_operations WHERE id=$id AND workspace_id=$w AND entity_id=$entity AND synced_at IS NULL;
                UPDATE sync_operations SET error_code='version_conflict',last_error='版本冲突'
                WHERE workspace_id=$w AND entity_id=$entity AND synced_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$id",conflict.OperationId.ToString("D"));cmd.Parameters.AddWithValue("$w",workspaceId.ToString("D"));
            cmd.Parameters.AddWithValue("$entity",conflict.EntityId.ToString("D"));cmd.Parameters.AddWithValue("$remote",conflict.RemotePayload);
            cmd.Parameters.AddWithValue("$version",conflict.ActualVersion);cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task<int> CountConflictsAsync(Guid workspaceId,CancellationToken ct){await using var c=factory.Create();await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM sync_conflicts WHERE workspace_id=$w AND resolved_at IS NULL";cmd.Parameters.AddWithValue("$w",workspaceId.ToString("D"));return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));}
    private SyncStatus Set(Guid id,SyncState state,string message){var value=new SyncStatus(id,state,message,DateTimeOffset.UtcNow);_statuses[id]=value;return value;}
}
