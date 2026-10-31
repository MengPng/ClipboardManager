namespace ClipboardManager.Domain.Models;

public sealed record ClipboardQuery(
    Guid WorkspaceId,
    string? SearchText = null,
    ClipboardItemType? ItemType = null,
    bool FavoritesOnly = false,
    bool PinnedOnly = false,
    int Offset = 0,
    int Limit = 100,
    bool LinksOnly = false);
