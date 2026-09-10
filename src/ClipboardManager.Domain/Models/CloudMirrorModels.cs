namespace ClipboardManager.Domain.Models;

public enum CloudCacheState { Empty = 0, Current = 1, Stale = 2, Synchronizing = 3, Error = 4 }

public sealed record CloudWorkspaceMapping(
    Guid CloudWorkspaceId,
    Guid LocalWorkspaceId,
    WorkspaceType WorkspaceType,
    string DisplayName,
    WorkspaceRole Role,
    string? RemoteCursor,
    CloudCacheState CacheState,
    DateTimeOffset? LastSuccessAt,
    string? LastErrorCode);
