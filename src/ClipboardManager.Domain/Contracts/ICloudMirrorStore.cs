using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ICloudMirrorStore
{
    Task<CloudWorkspaceMapping> EnsureMappingAsync(CloudWorkspace workspace, CancellationToken cancellationToken = default);
    Task<CloudWorkspaceMapping?> GetMappingAsync(Guid cloudWorkspaceId, CancellationToken cancellationToken = default);
    Task ApplyPullBatchAsync(Guid cloudWorkspaceId, IReadOnlyList<RemoteChange> changes, string nextCursor,
        DateTimeOffset synchronizedAt, CancellationToken cancellationToken = default);
    Task MarkStaleAsync(Guid cloudWorkspaceId, string? errorCode, CancellationToken cancellationToken = default);
}
