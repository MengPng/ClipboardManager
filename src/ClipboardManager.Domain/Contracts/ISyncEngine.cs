using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ISyncEngine
{
    SyncStatus GetStatus(Guid workspaceId);

    Task<SyncResult> SynchronizeAsync(
        Guid workspaceId,
        SyncReason reason,
        CancellationToken cancellationToken = default);
}

public interface IRemoteSyncProvider
{
    Task<PushResult> PushAsync(
        IReadOnlyList<SyncOperation> operations,
        CancellationToken cancellationToken = default);

    Task<PullResult> PullAsync(
        Guid workspaceId,
        string? cursor,
        CancellationToken cancellationToken = default);
}
