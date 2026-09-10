using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace ClipboardManager.Cloud.Api;

public sealed partial class CloudRepository
{
    private const long PersonalQuota = 1024L * 1024 * 1024;
    private const long TeamQuota = 5L * 1024 * 1024 * 1024;

    public async Task<CloudProfile> ProvisionAccountAsync(ProvisionAccountRequest request, CancellationToken ct)
    {
        var email = new System.Net.Mail.MailAddress(request.Email.Trim()).Address.ToLowerInvariant();
        var name = SafeName(request.DisplayName, "显示名称", 80);
        if (request.Password.Length is < 12 or > 256) throw new ArgumentException("密码长度必须为 12 至 256 字符。");
        var salt = RandomNumberGenerator.GetBytes(32);
        var digest = Rfc2898DeriveBytes.Pbkdf2(request.Password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        var encoded = $"pbkdf2-sha256$600000${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var id = Guid.NewGuid(); var workspace = Guid.NewGuid();
        await ExecuteAsync(c, tx, "INSERT INTO profiles(id,email,display_name,password_hash) VALUES($1,$2,$3,$4)", ct,id,email,name,encoded);
        await ExecuteAsync(c, tx, "INSERT INTO workspaces(id,kind,name,owner_id) VALUES($1,'personal',$2,$3)", ct,workspace,"个人配置",id);
        await ExecuteAsync(c, tx, "INSERT INTO workspace_members(workspace_id,profile_id,role) VALUES($1,$2,'owner')", ct,workspace,id);
        await ExecuteAsync(c, tx, "INSERT INTO workspace_quotas(workspace_id,quota_bytes) VALUES($1,$2)", ct,workspace,PersonalQuota);
        await ExecuteAsync(c, tx, "INSERT INTO audit_events(actor_id,event_type,target_type,target_id) VALUES($1,'account.provisioned','profile',$1)", ct,id);
        await tx.CommitAsync(ct);
        return new(id,email,name);
    }

    public async Task<WorkspaceResponse> CreateTeamWorkspaceAsync(CloudPrincipal principal, string name, CancellationToken ct)
    {
        name = SafeName(name, "团队名称", 80);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var id = Guid.NewGuid();
        await ExecuteAsync(c, tx, "INSERT INTO workspaces(id,kind,name,owner_id) VALUES($1,'team',$2,$3)", ct, id, name, principal.ProfileId);
        await ExecuteAsync(c, tx, "INSERT INTO workspace_members(workspace_id,profile_id,role) VALUES($1,$2,'owner')", ct, id, principal.ProfileId);
        await ExecuteAsync(c, tx, "INSERT INTO workspace_quotas(workspace_id,quota_bytes) VALUES($1,$2)", ct, id, TeamQuota);
        await AuditAsync(c, tx, principal, id, "workspace.created", "workspace", id, ct);
        await tx.CommitAsync(ct);
        return new(id, "team", name, "owner", TeamQuota, 0);
    }

    public async Task<IReadOnlyList<TeamMemberResponse>> GetMembersAsync(Guid profileId, Guid workspaceId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await RequireMemberAsync(c, null, workspaceId, profileId, ct);
        await using var command = new NpgsqlCommand("""
            SELECT p.id,p.email,p.display_name,m.role::text,
              COALESCE(array_agg(g.id) FILTER (WHERE g.id IS NOT NULL),'{}')
            FROM workspace_members m JOIN profiles p ON p.id=m.profile_id
            LEFT JOIN member_group_members mgm ON mgm.profile_id=p.id
            LEFT JOIN member_groups g ON g.id=mgm.group_id AND g.workspace_id=m.workspace_id AND g.deleted_at IS NULL
            WHERE m.workspace_id=$1 AND m.removed_at IS NULL GROUP BY p.id,p.email,p.display_name,m.role,m.joined_at ORDER BY m.joined_at
            """, c);
        command.Parameters.AddWithValue(workspaceId);
        var result = new List<TeamMemberResponse>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<Guid[]>(4)));
        return result;
    }

    public async Task<IReadOnlyList<MemberGroupResponse>> GetGroupsAsync(Guid profileId, Guid workspaceId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await RequireMemberAsync(c, null, workspaceId, profileId, ct);
        await using var command = new NpgsqlCommand("""
            SELECT g.id,g.workspace_id,g.name,count(mgm.profile_id)::int FROM member_groups g
            LEFT JOIN member_group_members mgm ON mgm.group_id=g.id
            WHERE g.workspace_id=$1 AND g.deleted_at IS NULL GROUP BY g.id ORDER BY g.name
            """, c);
        command.Parameters.AddWithValue(workspaceId);
        var result = new List<MemberGroupResponse>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
    }

    public async Task<MemberGroupResponse> CreateGroupAsync(CloudPrincipal principal, Guid workspaceId, string name, CancellationToken ct)
    {
        name = SafeName(name, "分组名称", 80);
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await RequireManagerAsync(c, tx, workspaceId, principal.ProfileId, false, ct);
        var id = Guid.NewGuid();
        await ExecuteAsync(c, tx, "INSERT INTO member_groups(id,workspace_id,name) VALUES($1,$2,$3)", ct, id, workspaceId, name);
        await AuditAsync(c, tx, principal, workspaceId, "group.created", "group", id, ct);
        await tx.CommitAsync(ct);
        return new(id, workspaceId, name, 0);
    }

    public async Task SetGroupMembersAsync(CloudPrincipal principal, Guid workspaceId, Guid groupId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count > 1000 || userIds.Distinct().Count() != userIds.Count) throw new ArgumentException("成员列表无效。");
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await RequireManagerAsync(c, tx, workspaceId, principal.ProfileId, false, ct);
        var belongs = await ScalarAsync<bool>(c, tx, "SELECT EXISTS(SELECT 1 FROM member_groups WHERE id=$1 AND workspace_id=$2 AND deleted_at IS NULL)", ct, groupId, workspaceId);
        if (!belongs) throw new KeyNotFoundException("分组不存在。");
        foreach (var userId in userIds) await RequireMemberAsync(c, tx, workspaceId, userId, ct);
        await ExecuteAsync(c, tx, "DELETE FROM member_group_members WHERE group_id=$1", ct, groupId);
        foreach (var userId in userIds) await ExecuteAsync(c, tx, "INSERT INTO member_group_members(group_id,profile_id) VALUES($1,$2)", ct, groupId, userId);
        await AuditAsync(c, tx, principal, workspaceId, "group.members_updated", "group", groupId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<InvitationResponse> CreateInvitationAsync(CloudPrincipal principal, Guid workspaceId, int lifetimeSeconds, int maxUses, CancellationToken ct)
    {
        if (lifetimeSeconds is < 300 or > 2592000) throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
        if (maxUses is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxUses));
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await RequireManagerAsync(c, tx, workspaceId, principal.ProfileId, false, ct);
        var id = Guid.NewGuid(); var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); var expires = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds);
        await ExecuteAsync(c, tx, "INSERT INTO workspace_invitations(id,workspace_id,code_hash,expires_at,max_uses,created_by) VALUES($1,$2,$3,$4,$5,$6)",
            ct, id, workspaceId, SHA256.HashData(Encoding.UTF8.GetBytes(code)), expires, maxUses, principal.ProfileId);
        await AuditAsync(c, tx, principal, workspaceId, "invitation.created", "invitation", id, ct);
        await tx.CommitAsync(ct);
        return new(id, workspaceId, code, expires, maxUses, 0, null);
    }

    public async Task<WorkspaceResponse> JoinInvitationAsync(CloudPrincipal principal, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128) throw new ArgumentException("邀请码无效。");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()));
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        Guid invitationId, workspaceId; string role;
        await using (var command = new NpgsqlCommand("""
            SELECT id,workspace_id,role::text FROM workspace_invitations
            WHERE code_hash=$1 AND revoked_at IS NULL AND expires_at>now() AND used_count<max_uses FOR UPDATE
            """, c, tx))
        {
            command.Parameters.AddWithValue(hash); await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("邀请码已过期、已撤销或已用完。");
            invitationId=reader.GetGuid(0); workspaceId=reader.GetGuid(1); role=reader.GetString(2);
        }
        var existing = await ScalarAsync<bool>(c, tx, "SELECT EXISTS(SELECT 1 FROM workspace_members WHERE workspace_id=$1 AND profile_id=$2 AND removed_at IS NULL)", ct, workspaceId, principal.ProfileId);
        if (existing)
        {
            var current = await ReadWorkspaceAsync(c, tx, principal.ProfileId, workspaceId, ct);
            await tx.CommitAsync(ct);
            return current;
        }
        await ExecuteAsync(c, tx, """
            INSERT INTO workspace_members(workspace_id,profile_id,role,removed_at) VALUES($1,$2,$3::workspace_role,NULL)
            ON CONFLICT(workspace_id,profile_id) DO UPDATE SET role=excluded.role,removed_at=NULL,joined_at=now()
            """, ct, workspaceId, principal.ProfileId, role);
        await ExecuteAsync(c, tx, "UPDATE workspace_invitations SET used_count=used_count+1 WHERE id=$1", ct, invitationId);
        await AuditAsync(c, tx, principal, workspaceId, "invitation.joined", "invitation", invitationId, ct);
        var result = await ReadWorkspaceAsync(c, tx, principal.ProfileId, workspaceId, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task RevokeInvitationAsync(CloudPrincipal principal, Guid workspaceId, Guid invitationId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await RequireManagerAsync(c, tx, workspaceId, principal.ProfileId, false, ct);
        var count = await ExecuteAsync(c, tx, "UPDATE workspace_invitations SET revoked_at=now() WHERE id=$1 AND workspace_id=$2 AND revoked_at IS NULL", ct, invitationId, workspaceId);
        if (count != 1) throw new KeyNotFoundException("邀请码不存在或已撤销。");
        await AuditAsync(c, tx, principal, workspaceId, "invitation.revoked", "invitation", invitationId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task UpdateMemberRoleAsync(CloudPrincipal principal, Guid workspaceId, Guid memberId, string role, CancellationToken ct)
    {
        role = role.Trim().ToLowerInvariant(); if (role is not ("admin" or "member")) throw new ArgumentException("角色只能是 Admin 或 Member。");
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await RequireManagerAsync(c, tx, workspaceId, principal.ProfileId, true, ct);
        var targetRole = await RequireMemberAsync(c, tx, workspaceId, memberId, ct);
        if (targetRole == "owner") throw new InvalidOperationException("不能通过此接口变更 Owner。");
        await ExecuteAsync(c, tx, "UPDATE workspace_members SET role=$1::workspace_role WHERE workspace_id=$2 AND profile_id=$3 AND removed_at IS NULL", ct, role, workspaceId, memberId);
        await AuditAsync(c, tx, principal, workspaceId, "member.role_updated", "profile", memberId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task RemoveMemberAsync(CloudPrincipal principal, Guid workspaceId, Guid memberId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await RequireManagerAsync(c, tx, workspaceId, principal.ProfileId, false, ct);
        var targetRole = await RequireMemberAsync(c, tx, workspaceId, memberId, ct);
        if (targetRole == "owner") throw new InvalidOperationException("不能移除 Owner。");
        await ExecuteAsync(c, tx, "UPDATE workspace_members SET removed_at=now() WHERE workspace_id=$1 AND profile_id=$2", ct, workspaceId, memberId);
        await ExecuteAsync(c, tx, "DELETE FROM member_group_members mgm USING member_groups g WHERE mgm.group_id=g.id AND g.workspace_id=$1 AND mgm.profile_id=$2", ct, workspaceId, memberId);
        await AuditAsync(c, tx, principal, workspaceId, "member.removed", "profile", memberId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(CloudPrincipal principal, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var command = new NpgsqlCommand("SELECT id,device_name,last_seen_at,created_at,revoked_at FROM devices WHERE profile_id=$1 ORDER BY last_seen_at DESC NULLS LAST", c);
        command.Parameters.AddWithValue(principal.ProfileId); var result = new List<DeviceResponse>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetFieldValue<DateTimeOffset>(2),reader.GetFieldValue<DateTimeOffset>(3),reader.IsDBNull(4)?null:reader.GetFieldValue<DateTimeOffset>(4),reader.GetGuid(0)==principal.DeviceId));
        return result;
    }

    public async Task RevokeDeviceAsync(CloudPrincipal principal, Guid deviceId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        if (deviceId == principal.DeviceId) throw new InvalidOperationException("当前设备请使用退出登录。");
        var changed = await ExecuteAsync(c, tx, "UPDATE devices SET revoked_at=now() WHERE id=$1 AND profile_id=$2 AND revoked_at IS NULL", ct, deviceId, principal.ProfileId);
        if (changed != 1) throw new KeyNotFoundException("设备不存在或已撤销。");
        await ExecuteAsync(c, tx, "UPDATE auth_sessions SET revoked_at=now() WHERE device_id=$1 AND revoked_at IS NULL", ct, deviceId);
        await AuditAsync(c, tx, principal, null, "device.revoked", "device", deviceId, ct); await tx.CommitAsync(ct);
    }

    private static async Task<string> RequireMemberAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid workspaceId, Guid profileId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT m.role::text FROM workspace_members m JOIN workspaces w ON w.id=m.workspace_id WHERE m.workspace_id=$1 AND m.profile_id=$2 AND m.removed_at IS NULL AND w.deleted_at IS NULL", c, tx);
        command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(profileId);
        return await command.ExecuteScalarAsync(ct) as string ?? throw new UnauthorizedAccessException("不是该团队成员。");
    }
    private static async Task RequireManagerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid workspaceId, Guid profileId, bool ownerOnly, CancellationToken ct)
    {
        await using (var team = new NpgsqlCommand("SELECT id FROM workspaces WHERE id=$1 AND kind='team' AND deleted_at IS NULL FOR UPDATE", c, tx))
        {
            team.Parameters.AddWithValue(workspaceId);
            if (await team.ExecuteScalarAsync(ct) is null) throw new KeyNotFoundException("团队不存在。");
        }
        var role = await RequireMemberAsync(c, tx, workspaceId, profileId, ct);
        if (ownerOnly ? role != "owner" : role is not ("owner" or "admin")) throw new UnauthorizedAccessException("没有团队管理权限。");
    }
    private static async Task<WorkspaceResponse> ReadWorkspaceAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid profileId, Guid workspaceId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT w.id,w.kind::text,w.name,m.role::text,q.quota_bytes,q.used_bytes FROM workspaces w JOIN workspace_members m ON m.workspace_id=w.id JOIN workspace_quotas q ON q.workspace_id=w.id WHERE w.id=$1 AND m.profile_id=$2 AND m.removed_at IS NULL", c, tx);
        command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(profileId); await using var r=await command.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) throw new UnauthorizedAccessException(); return new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5));
    }
    private static async Task<int> ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] values)
    { await using var command=new NpgsqlCommand(sql,c,tx); for(var i=0;i<values.Length;i++) command.Parameters.AddWithValue(values[i]); return await command.ExecuteNonQueryAsync(ct); }
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection c,NpgsqlTransaction tx,string sql,CancellationToken ct,params object[] values)
    { await using var command=new NpgsqlCommand(sql,c,tx); for(var i=0;i<values.Length;i++) command.Parameters.AddWithValue(values[i]); return (T)(await command.ExecuteScalarAsync(ct))!; }
    private static async Task AuditAsync(NpgsqlConnection c,NpgsqlTransaction tx,CloudPrincipal actor,Guid? workspace,string eventType,string targetType,Guid target,CancellationToken ct) =>
        _ = await ExecuteAsync(c,tx,"INSERT INTO audit_events(workspace_id,actor_id,device_id,event_type,target_type,target_id) VALUES($1,$2,$3,$4,$5,$6)",ct,(object?)workspace??DBNull.Value,actor.ProfileId,actor.DeviceId,eventType,targetType,target);
    private static string SafeName(string value,string field,int max){ value=value?.Trim()??string.Empty; if(value.Length is 0 || value.Length>max) throw new ArgumentException($"{field}长度必须为 1-{max}。"); return value; }
}
