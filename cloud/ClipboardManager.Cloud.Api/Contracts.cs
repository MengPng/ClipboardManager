using System.Text.Json;

namespace ClipboardManager.Cloud.Api;

public sealed record SignInRequest(string Email, string Password, Guid DeviceId, string DeviceName);
public sealed record RefreshRequest(string RefreshToken);
public sealed record SignOutRequest(bool AllDevices);
public sealed record SessionResponse(Guid UserId, string Email, string DisplayName, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record IssuedTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt, DateTimeOffset RefreshExpiresAt);
public sealed record CloudPrincipal(Guid ProfileId, Guid SessionId, Guid DeviceId);
public sealed record CloudProfile(Guid Id, string Email, string DisplayName);
public sealed record WorkspaceResponse(Guid Id, string Type, string Name, string Role, long QuotaBytes, long UsedBytes);
public sealed record PushRequest(Guid WorkspaceId, IReadOnlyList<PushOperation> Operations);
public sealed record PushOperation(Guid Id, string EntityType, Guid EntityId, int OperationType, long BaseVersion, JsonElement Payload, DateTimeOffset CreatedAt);
public sealed record PushResponse(bool Succeeded, IReadOnlyDictionary<Guid, long> ServerVersions, IReadOnlyList<ConflictResponse> Conflicts);
public sealed record ConflictResponse(Guid OperationId, Guid EntityId, long ExpectedVersion, long ActualVersion, JsonElement? RemotePayload);
public sealed record PullChange(string EntityType, Guid EntityId, long ServerVersion, JsonElement? Payload, bool IsDeleted);
public sealed record PullResponse(bool Succeeded, string NextCursor, IReadOnlyList<PullChange> Changes);
public sealed record CreateWorkspaceRequest(string Name);
public sealed record ProvisionAccountRequest(string Email, string DisplayName, string Password);
public sealed record TeamMemberResponse(Guid UserId, string Email, string DisplayName, string Role, IReadOnlyList<Guid> GroupIds);
public sealed record CreateGroupRequest(string Name);
public sealed record MemberGroupResponse(Guid Id, Guid WorkspaceId, string Name, int MemberCount);
public sealed record UpdateGroupMembersRequest(IReadOnlyList<Guid> UserIds);
public sealed record CreateInvitationRequest(int LifetimeSeconds = 86400, int MaxUses = 1);
public sealed record InvitationResponse(Guid Id, Guid WorkspaceId, string Code, DateTimeOffset ExpiresAt, int MaxUses, int UsedCount, DateTimeOffset? RevokedAt);
public sealed record JoinInvitationRequest(string Code);
public sealed record UpdateMemberRoleRequest(string Role);
public sealed record DeviceResponse(Guid Id, string Name, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, bool IsCurrent);
