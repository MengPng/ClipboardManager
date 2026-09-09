using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IClipboardHistoryRepository : IClipboardItemRepository
{
    Task<CaptureResult> CaptureTextAsync(TextCapture capture, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(Guid workspaceId, Guid itemId, bool isFavorite, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(Guid workspaceId, Guid itemId, bool isPinned, CancellationToken cancellationToken = default);

    Task MarkUsedAsync(Guid workspaceId, Guid itemId, DateTimeOffset usedAt, CancellationToken cancellationToken = default);

    Task<int> CleanupAsync(
        Guid workspaceId,
        DateTimeOffset olderThan,
        int maxActiveItems,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default);
}
