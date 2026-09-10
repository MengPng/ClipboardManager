namespace ClipboardManager.Domain.Models;

public enum SyncReason
{
    Startup = 0,
    UserRequested = 1,
    LocalChange = 2,
    Scheduled = 3
}

public sealed record SyncStatus(Guid WorkspaceId, SyncState State, string Message, DateTimeOffset? LastAttemptAt);

public sealed record SyncResult(bool Succeeded, SyncStatus Status, int PushedCount = 0, int PulledCount = 0);

public enum SyncOperationType
{
    Create = 0,
    Update = 1,
    Delete = 2,
    Restore = 3
}

public sealed record SyncOperation(
    Guid Id,
    Guid WorkspaceId,
    string EntityType,
    Guid EntityId,
    SyncOperationType OperationType,
    long BaseVersion,
    string Payload,
    DateTimeOffset CreatedAt);

public sealed record PushResult(bool Succeeded, IReadOnlyDictionary<Guid, long> ServerVersions, string? Error = null, IReadOnlyList<RemoteConflict>? Conflicts = null);
public sealed record RemoteConflict(Guid OperationId, Guid EntityId, long ExpectedVersion, long ActualVersion, string RemotePayload);

public sealed record PullResult(bool Succeeded, string? NextCursor, IReadOnlyList<RemoteChange> Changes, string? Error = null);

public sealed record RemoteChange(string EntityType, Guid EntityId, long ServerVersion, string Payload, bool IsDeleted);
