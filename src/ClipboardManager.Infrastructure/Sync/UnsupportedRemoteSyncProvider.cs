using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Sync;

public sealed class UnsupportedRemoteSyncProvider : IRemoteSyncProvider
{
    private const string Message = "当前版本未配置远端同步 Provider。";

    public Task<PushResult> PushAsync(
        IReadOnlyList<SyncOperation> operations,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PushResult(false, new Dictionary<Guid, long>(), Message));
    }

    public Task<PullResult> PullAsync(
        Guid workspaceId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PullResult(false, cursor, [], Message));
    }
}
