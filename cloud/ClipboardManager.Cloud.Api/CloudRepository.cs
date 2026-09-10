using Npgsql;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClipboardManager.Cloud.Api;

public sealed partial class CloudRepository(NpgsqlDataSource dataSource)
{
    public async Task<CloudProfile?> ValidatePasswordAsync(string email, string password, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT id,email,display_name,password_hash FROM profiles WHERE lower(email)=lower($1) AND disabled_at IS NULL");
        command.Parameters.AddWithValue(email.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var profile = new CloudProfile(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        return VerifyPassword(password, reader.GetString(3)) ? profile : null;
    }

    public async Task CreateSessionAsync(Guid profileId, Guid deviceId, string deviceName, IssuedTokens tokens, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var device = new NpgsqlCommand("""
            INSERT INTO devices(id,profile_id,device_name,refresh_token_hash,last_seen_at)
            VALUES($1,$2,$3,$4,now())
            ON CONFLICT(id) DO UPDATE SET device_name=excluded.device_name, refresh_token_hash=excluded.refresh_token_hash,
              last_seen_at=now(), revoked_at=NULL WHERE devices.profile_id=excluded.profile_id
            """, connection, transaction))
        {
            device.Parameters.AddWithValue(deviceId); device.Parameters.AddWithValue(profileId);
            device.Parameters.AddWithValue(SafeDeviceName(deviceName)); device.Parameters.AddWithValue(TokenService.Hash(tokens.RefreshToken));
            if (await device.ExecuteNonQueryAsync(ct) != 1) throw new UnauthorizedAccessException("设备不属于当前账号。");
        }
        await InsertSessionAsync(connection, transaction, profileId, deviceId, tokens, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<CloudProfile?> RotateSessionAsync(string refreshToken, IssuedTokens next, CancellationToken ct)
    {
        byte[] hash;
        try { hash = TokenService.Hash(refreshToken); } catch (FormatException) { return null; }
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid profileId, deviceId, sessionId;
        await using (var find = new NpgsqlCommand("""
            SELECT s.id,s.profile_id,s.device_id FROM auth_sessions s JOIN devices d ON d.id=s.device_id
            WHERE s.refresh_token_hash=$1 AND s.revoked_at IS NULL AND s.refresh_expires_at>now() AND d.revoked_at IS NULL FOR UPDATE
            """, connection, transaction))
        {
            find.Parameters.AddWithValue(hash);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            sessionId = reader.GetGuid(0); profileId = reader.GetGuid(1); deviceId = reader.GetGuid(2);
        }
        await using (var revoke = new NpgsqlCommand("UPDATE auth_sessions SET revoked_at=now() WHERE id=$1", connection, transaction))
        { revoke.Parameters.AddWithValue(sessionId); await revoke.ExecuteNonQueryAsync(ct); }
        await InsertSessionAsync(connection, transaction, profileId, deviceId, next, ct);
        CloudProfile profile;
        await using (var get = new NpgsqlCommand("SELECT id,email,display_name FROM profiles WHERE id=$1 AND disabled_at IS NULL", connection, transaction))
        {
            get.Parameters.AddWithValue(profileId);
            await using var reader = await get.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            profile = new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        }
        await transaction.CommitAsync(ct);
        return profile;
    }

    public async Task<CloudPrincipal?> ValidateAccessTokenAsync(string token, CancellationToken ct)
    {
        byte[] hash;
        try { hash = TokenService.Hash(token); } catch (FormatException) { return null; }
        await using var command = dataSource.CreateCommand("""
            SELECT s.profile_id,s.id,s.device_id FROM auth_sessions s
            JOIN profiles p ON p.id=s.profile_id JOIN devices d ON d.id=s.device_id
            WHERE s.access_token_hash=$1 AND s.revoked_at IS NULL AND s.access_expires_at>now()
              AND p.disabled_at IS NULL AND d.revoked_at IS NULL
            """);
        command.Parameters.AddWithValue(hash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)) : null;
    }

    public async Task RevokeSessionsAsync(Guid profileId, Guid sessionId, bool allDevices, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(allDevices
            ? "UPDATE auth_sessions SET revoked_at=now() WHERE profile_id=$1 AND revoked_at IS NULL"
            : "UPDATE auth_sessions SET revoked_at=now() WHERE profile_id=$1 AND id=$2 AND revoked_at IS NULL");
        command.Parameters.AddWithValue(profileId); if (!allDevices) command.Parameters.AddWithValue(sessionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<WorkspaceResponse>> GetWorkspacesAsync(Guid profileId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetUserAsync(connection, profileId, transaction, ct);
        await using var command = new NpgsqlCommand("""
            SELECT w.id,w.kind::text,w.name,m.role::text,q.quota_bytes,q.used_bytes
            FROM workspaces w JOIN workspace_members m ON m.workspace_id=w.id
            JOIN workspace_quotas q ON q.workspace_id=w.id
            WHERE m.profile_id=$1 AND m.removed_at IS NULL AND w.deleted_at IS NULL ORDER BY w.kind,w.name
            """, connection, transaction);
        command.Parameters.AddWithValue(profileId);
        var result = new List<WorkspaceResponse>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5)));
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<PullResponse> PullAsync(Guid profileId, Guid workspaceId, long cursor, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await RequireMemberAsync(connection, transaction, workspaceId, profileId, ct);
        await SetUserAsync(connection, profileId, transaction, ct);
        await using var command = new NpgsqlCommand("""
            SELECT sequence,entity_type,entity_id,version,payload,deleted_at IS NOT NULL
            FROM sync_changes WHERE workspace_id=$1 AND sequence>$2 ORDER BY sequence LIMIT 200
            """, connection, transaction);
        command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(cursor);
        var changes = new List<PullChange>(); var next = cursor;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            next = reader.GetInt64(0);
            changes.Add(new(reader.GetString(1), reader.GetGuid(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)), reader.GetBoolean(5)));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return new(true, next.ToString(), changes);
    }

    public async Task<PushResponse> PushAsync(CloudPrincipal principal, PushRequest request, CancellationToken ct)
    {
        if (request.Operations.Count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(request.Operations));
        if (request.Operations.Select(x => x.Id).Distinct().Count() != request.Operations.Count) throw new ArgumentException("操作 ID 重复。");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serialize a workspace's version checks and writes, including first creation.
        await using (var mutex = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction))
        {
            mutex.Parameters.AddWithValue(request.WorkspaceId.ToString("D"));
            await mutex.ExecuteNonQueryAsync(ct);
        }
        await SetUserAsync(connection, principal.ProfileId, transaction, ct);
        if (!await CanEditAsync(connection, transaction, request.WorkspaceId, principal.ProfileId, ct)) throw new UnauthorizedAccessException("没有写入权限。");
        var versions = new Dictionary<Guid, long>(); var conflicts = new List<ConflictResponse>();
        foreach (var operation in request.Operations)
        {
            if (operation.OperationType is < 0 or > 2 || operation.EntityType is not ("collection_folder" or "collection_item") || operation.EntityId == Guid.Empty)
                throw new ArgumentException("无效的同步操作。");
            var previous = await GetOperationVersionAsync(connection, transaction, operation.Id, ct);
            if (previous is not null) { versions[operation.Id] = previous.Value; continue; }
            var current = await GetEntityVersionAsync(connection, transaction, request.WorkspaceId, operation.EntityType, operation.EntityId, ct);
            if (operation.BaseVersion != current)
            {
                conflicts.Add(new(operation.Id, operation.EntityId, operation.BaseVersion, current,
                    await GetEntityPayloadAsync(connection, transaction, request.WorkspaceId, operation.EntityType, operation.EntityId, ct)));
                continue;
            }
            var version = current + 1;
            await InsertChangeAsync(connection, transaction, principal, request.WorkspaceId, operation, version, ct);
            versions[operation.Id] = version;
        }
        await transaction.CommitAsync(ct);
        return new(conflicts.Count == 0, versions, conflicts);
    }

    private static async Task InsertSessionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid profileId, Guid deviceId, IssuedTokens value, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO auth_sessions(profile_id,device_id,access_token_hash,refresh_token_hash,access_expires_at,refresh_expires_at)
            VALUES($1,$2,$3,$4,$5,$6)
            """, c, tx);
        command.Parameters.AddWithValue(profileId); command.Parameters.AddWithValue(deviceId);
        command.Parameters.AddWithValue(TokenService.Hash(value.AccessToken)); command.Parameters.AddWithValue(TokenService.Hash(value.RefreshToken));
        command.Parameters.AddWithValue(value.AccessExpiresAt); command.Parameters.AddWithValue(value.RefreshExpiresAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetUserAsync(NpgsqlConnection c, Guid userId, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.user_id',$1,true)", c, tx);
        command.Parameters.AddWithValue(userId.ToString()); await command.ExecuteNonQueryAsync(ct);
        await using var role = new NpgsqlCommand("SET LOCAL ROLE clipboardmanager_app", c, tx);
        await role.ExecuteNonQueryAsync(ct);
    }
    private static async Task<bool> CanEditAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid workspace, Guid profile, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM workspace_members WHERE workspace_id=$1 AND profile_id=$2 AND removed_at IS NULL AND role IN ('owner','admin'))", c, tx);
        command.Parameters.AddWithValue(workspace); command.Parameters.AddWithValue(profile); return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
    private static async Task<long?> GetOperationVersionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid operationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT result_version FROM sync_operations WHERE operation_id=$1", c, tx);
        command.Parameters.AddWithValue(operationId); var value = await command.ExecuteScalarAsync(ct); return value is null or DBNull ? null : Convert.ToInt64(value);
    }
    private static async Task<long> GetEntityVersionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid workspace, string type, Guid entity, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT COALESCE(max(version),0) FROM sync_changes WHERE workspace_id=$1 AND entity_type=$2 AND entity_id=$3", c, tx);
        command.Parameters.AddWithValue(workspace); command.Parameters.AddWithValue(type); command.Parameters.AddWithValue(entity); return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
    private static async Task<JsonElement?> GetEntityPayloadAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid workspace, string type, Guid entity, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT payload FROM sync_changes WHERE workspace_id=$1 AND entity_type=$2 AND entity_id=$3 ORDER BY version DESC LIMIT 1", c, tx);
        command.Parameters.AddWithValue(workspace); command.Parameters.AddWithValue(type); command.Parameters.AddWithValue(entity); var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : JsonSerializer.Deserialize<JsonElement>((string)value);
    }
    private static async Task InsertChangeAsync(NpgsqlConnection c, NpgsqlTransaction tx, CloudPrincipal principal, Guid workspace, PushOperation operation, long version, CancellationToken ct)
    {
        var deleted = operation.OperationType == 2;
        await MaterializeEntityAsync(c, tx, principal.ProfileId, workspace, operation, version, deleted, ct);
        await using (var change = new NpgsqlCommand("""
            INSERT INTO sync_changes(workspace_id,entity_type,entity_id,version,payload,deleted_at,actor_id)
            VALUES($1,$2,$3,$4,$5::jsonb,CASE WHEN $6 THEN now() ELSE NULL END,$7)
            """, c, tx))
        {
            change.Parameters.AddWithValue(workspace); change.Parameters.AddWithValue(operation.EntityType); change.Parameters.AddWithValue(operation.EntityId);
            change.Parameters.AddWithValue(version); change.Parameters.AddWithValue(operation.Payload.GetRawText()); change.Parameters.AddWithValue(deleted); change.Parameters.AddWithValue(principal.ProfileId);
            await change.ExecuteNonQueryAsync(ct);
        }
        await using var record = new NpgsqlCommand("""
            INSERT INTO sync_operations(operation_id,workspace_id,actor_id,device_id,entity_type,entity_id,operation_type,base_version,result_version,result)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,jsonb_build_object('accepted',true,'version',$9))
            """, c, tx);
        record.Parameters.AddWithValue(operation.Id); record.Parameters.AddWithValue(workspace); record.Parameters.AddWithValue(principal.ProfileId);
        record.Parameters.AddWithValue(principal.DeviceId); record.Parameters.AddWithValue(operation.EntityType); record.Parameters.AddWithValue(operation.EntityId);
        record.Parameters.AddWithValue(operation.OperationType.ToString()); record.Parameters.AddWithValue(operation.BaseVersion); record.Parameters.AddWithValue(version);
        await record.ExecuteNonQueryAsync(ct);
    }

    private static async Task MaterializeEntityAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid actorId, Guid workspace,
        PushOperation operation, long version, bool deleted, CancellationToken ct)
    {
        if (operation.Payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("同步载荷必须是 JSON 对象。");
        if (operation.EntityType == "collection_folder")
        {
            var name = RequiredString(operation.Payload, "name", 120);
            var parent = OptionalGuid(operation.Payload, "parentFolderId");
            if (parent == operation.EntityId) throw new ArgumentException("文件夹不能作为自己的父目录。");
            if (parent.HasValue) await RequireFolderAsync(c, tx, workspace, parent.Value, ct);
            var sortKey = RequiredDecimal(operation.Payload, "sortKey");
            var scopeAll = OptionalBoolean(operation.Payload, "scopeAllMembers", true);
            await using var command = new NpgsqlCommand("""
                INSERT INTO collection_folders(id,workspace_id,parent_folder_id,name,sort_key,scope_all_members,version,actor_id,updated_at,deleted_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,now(),CASE WHEN $9 THEN now() ELSE NULL END)
                ON CONFLICT(id) DO UPDATE SET parent_folder_id=excluded.parent_folder_id,name=excluded.name,
                  sort_key=excluded.sort_key,scope_all_members=excluded.scope_all_members,version=excluded.version,
                  actor_id=excluded.actor_id,updated_at=now(),deleted_at=excluded.deleted_at
                WHERE collection_folders.workspace_id=excluded.workspace_id
                """, c, tx);
            command.Parameters.AddWithValue(operation.EntityId); command.Parameters.AddWithValue(workspace);
            command.Parameters.AddWithValue((object?)parent ?? DBNull.Value); command.Parameters.AddWithValue(name);
            command.Parameters.AddWithValue(sortKey); command.Parameters.AddWithValue(scopeAll);
            command.Parameters.AddWithValue(version); command.Parameters.AddWithValue(actorId); command.Parameters.AddWithValue(deleted);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new UnauthorizedAccessException("实体不属于当前 Workspace。");
            return;
        }

        if (operation.EntityType == "collection_item")
        {
            var folderId = RequiredGuid(operation.Payload, "folderId");
            await RequireFolderAsync(c, tx, workspace, folderId, ct);
            var itemType = RequiredInt(operation.Payload, "itemType") switch
            {
                0 => "text", 1 => "link", 2 => "image", 3 => "file",
                _ => throw new ArgumentException("不支持的配置项类型。")
            };
            var sortKey = RequiredDecimal(operation.Payload, "sortKey");
            await using var command = new NpgsqlCommand("""
                INSERT INTO collection_items(id,workspace_id,folder_id,item_type,payload,sort_key,version,actor_id,updated_at,deleted_at)
                VALUES($1,$2,$3,$4,$5::jsonb,$6,$7,$8,now(),CASE WHEN $9 THEN now() ELSE NULL END)
                ON CONFLICT(id) DO UPDATE SET folder_id=excluded.folder_id,item_type=excluded.item_type,payload=excluded.payload,
                  sort_key=excluded.sort_key,version=excluded.version,actor_id=excluded.actor_id,updated_at=now(),deleted_at=excluded.deleted_at
                WHERE collection_items.workspace_id=excluded.workspace_id
                """, c, tx);
            command.Parameters.AddWithValue(operation.EntityId); command.Parameters.AddWithValue(workspace); command.Parameters.AddWithValue(folderId);
            command.Parameters.AddWithValue(itemType); command.Parameters.AddWithValue(operation.Payload.GetRawText()); command.Parameters.AddWithValue(sortKey);
            command.Parameters.AddWithValue(version); command.Parameters.AddWithValue(actorId); command.Parameters.AddWithValue(deleted);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new UnauthorizedAccessException("实体不属于当前 Workspace。");
            return;
        }

        throw new ArgumentException($"不支持的同步实体类型：{operation.EntityType}");
    }

    private static async Task RequireFolderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid workspace, Guid folder, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM collection_folders WHERE id=$1 AND workspace_id=$2 AND deleted_at IS NULL)",c,tx);
        command.Parameters.AddWithValue(folder);command.Parameters.AddWithValue(workspace);
        if (!(bool)(await command.ExecuteScalarAsync(ct))!) throw new ArgumentException("目标目录不存在或不属于当前空间。");
    }

    private static string RequiredString(JsonElement payload, string name, int maxLength)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"缺少字段 {name}。");
        var result = value.GetString()!.Trim();
        if (result.Length > maxLength) throw new ArgumentException($"字段 {name} 超出长度限制。");
        return result;
    }
    private static Guid RequiredGuid(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var result)
            ? result : throw new ArgumentException($"字段 {name} 不是有效 UUID。");
    private static Guid? OptionalGuid(JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var result) ? result : throw new ArgumentException($"字段 {name} 不是有效 UUID。");
    private static decimal RequiredDecimal(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result) ? result : throw new ArgumentException($"字段 {name} 不是有效数值。");
    private static int RequiredInt(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : throw new ArgumentException($"字段 {name} 不是有效整数。");
    private static bool OptionalBoolean(JsonElement payload, string name, bool fallback) =>
        !payload.TryGetProperty(name, out var value) ? fallback : value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new ArgumentException($"字段 {name} 不是有效布尔值。");
    private static string SafeDeviceName(string value) => string.IsNullOrWhiteSpace(value) ? "Windows device" : value.Trim()[..Math.Min(value.Trim().Length, 80)];
    private static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 100_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]); var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
