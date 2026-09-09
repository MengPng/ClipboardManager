namespace ClipboardManager.Domain.Models;

public sealed record HistoryCleanupPreview(
    Guid WorkspaceId,
    int ActiveItems,
    int ProtectedPinnedOrFavorite,
    int EligibleItems,
    DateTimeOffset OlderThan,
    int MaxActiveItems);
