namespace ClipboardManager.Domain.Models;

public enum AppMode { Local = 0, Cloud = 1 }
public enum WorkspaceRole { Owner = 0, Admin = 1, Member = 2 }
public enum CloudConnectionState { LocalOnly = 0, NeedsConfiguration = 1, NeedsSignIn = 2, Offline = 3, Connected = 4, Synchronizing = 5, Synchronized = 6, Conflict = 7 }

public sealed record CloudSession(Guid UserId, string Email, string DisplayName, DateTimeOffset ExpiresAt);
public sealed record CloudSignInRequest(string Email, string Password, Guid DeviceId, string DeviceName);
public sealed record CloudWorkspace(Guid Id, WorkspaceType Type, string Name, WorkspaceRole Role, long QuotaBytes, long UsedBytes);
public sealed record CloudStatus(CloudConnectionState State, string Message, DateTimeOffset? LastSuccessfulSyncAt = null, int PendingOperations = 0, int ConflictCount = 0);
public sealed record TeamMember(Guid UserId, string Email, string DisplayName, WorkspaceRole Role, IReadOnlyList<Guid> GroupIds);
public sealed record MemberGroup(Guid Id, Guid WorkspaceId, string Name, int MemberCount);
public sealed record TeamInvitation(Guid Id, Guid WorkspaceId, DateTimeOffset ExpiresAt, int MaxUses, int UsedCount, DateTimeOffset? RevokedAt, string? Code = null);
public sealed record CloudDevice(Guid Id, string Name, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, bool IsCurrent);

public static class CloudLimits
{
    public const long MaximumBlobBytes = 100L * 1024 * 1024;
    public const long DefaultPersonalQuotaBytes = 1L * 1024 * 1024 * 1024;
    public const long DefaultTeamQuotaBytes = 5L * 1024 * 1024 * 1024;
}
