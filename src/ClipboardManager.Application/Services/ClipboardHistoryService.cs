using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class ClipboardHistoryService(
    IClipboardHistoryRepository repository,
    IWorkspaceService workspaceService,
    IAppSettingsStore settingsStore)
{
    public async Task<IReadOnlyList<ClipboardItem>> SearchAsync(
        string? searchText = null,
        bool favoritesOnly = false,
        bool pinnedOnly = false,
        ClipboardItemType? itemType = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        return await repository.SearchAsync(
            new ClipboardQuery(
                workspace.Id,
                searchText,
                itemType,
                favoritesOnly,
                pinnedOnly,
                0,
                limit),
            cancellationToken);
    }

    public async Task SetFavoriteAsync(Guid itemId, bool value, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.SetFavoriteAsync(workspace.Id, itemId, value, cancellationToken);
    }

    public async Task SetPinnedAsync(Guid itemId, bool value, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.SetPinnedAsync(workspace.Id, itemId, value, cancellationToken);
    }

    public async Task DeleteAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.SoftDeleteAsync(workspace.Id, itemId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task MarkUsedAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.MarkUsedAsync(workspace.Id, itemId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<HistoryCleanupPreview> PreviewCleanupAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var all = await repository.SearchAsync(new ClipboardQuery(workspace.Id, Limit: Math.Max(settings.MaxHistoryItems + 10000, 100000)), cancellationToken);
        var protectedCount = all.Count(item => item.IsPinned || item.IsFavorite);
        var olderThan = DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays);
        var unprotected = all.Where(item => !item.IsPinned && !item.IsFavorite).OrderByDescending(item => item.CapturedAt).ToArray();
        var eligible = unprotected.Select((item, index) => (item, index))
            .Count(entry => entry.item.CapturedAt < olderThan || entry.index >= settings.MaxHistoryItems);
        return new HistoryCleanupPreview(workspace.Id, all.Count, protectedCount, eligible, olderThan, settings.MaxHistoryItems);
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return await repository.CleanupAsync(
            workspace.Id,
            now.AddDays(-settings.RetentionDays),
            settings.MaxHistoryItems,
            now,
            cancellationToken);
    }
}
