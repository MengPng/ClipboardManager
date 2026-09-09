using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IClipboardItemRepository
{
    Task<ClipboardItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClipboardItem>> SearchAsync(
        ClipboardQuery query,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(ClipboardItem item, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(
        Guid workspaceId,
        Guid id,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default);
}
