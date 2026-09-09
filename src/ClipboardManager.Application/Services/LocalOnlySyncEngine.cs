using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class LocalOnlySyncEngine : ISyncEngine
{
    public SyncStatus GetStatus(Guid workspaceId) =>
        new(workspaceId, SyncState.LocalOnly, "仅保存在本机", null);

    public Task<SyncResult> SynchronizeAsync(
        Guid workspaceId,
        SyncReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = new SyncStatus(
            workspaceId,
            SyncState.LocalOnly,
            "当前版本未配置云同步，数据仅保存在本机。",
            DateTimeOffset.UtcNow);
        return Task.FromResult(new SyncResult(false, status));
    }
}
