using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

/// <summary>
/// Config-space operations. Every method carries its target workspace explicitly rather
/// than reading an ambient "current workspace": the UI can show 个人配置 or 团队配置, but
/// <see cref="IWorkspaceService.GetCurrentAsync"/> stays pinned to the local capture
/// workspace so a view switch can never redirect clipboard capture or history.
/// </summary>
public sealed class CollectionService(
    ICollectionRepository repository,
    IWorkspaceService workspaceService,
    ILocalAssetStore assetStore)
{
    public async Task<(IReadOnlyList<CollectionFolder> Folders, CollectionFolder Uncategorized)> GetFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var uncategorized = await repository.EnsureUncategorizedAsync(workspaceId, cancellationToken);
        return (await repository.GetFoldersAsync(workspaceId, cancellationToken), uncategorized);
    }

    public Task<CollectionFolder> CreateFolderAsync(Guid workspaceId, string name, CancellationToken cancellationToken = default) =>
        repository.CreateFolderAsync(workspaceId, name, cancellationToken);

    public Task RenameFolderAsync(Guid workspaceId, Guid folderId, string name, CancellationToken cancellationToken = default) =>
        repository.RenameFolderAsync(workspaceId, folderId, name, cancellationToken);

    public async Task DeleteFolderAsync(Guid workspaceId, Guid folderId, CancellationToken cancellationToken = default)
    {
        var uncategorized = await repository.EnsureUncategorizedAsync(workspaceId, cancellationToken);
        await repository.DeleteFolderAsync(workspaceId, folderId, uncategorized.Id, cancellationToken);
    }

    public Task ReorderFolderAsync(Guid workspaceId, Guid folderId, int targetIndex, CancellationToken cancellationToken = default) =>
        repository.ReorderFolderAsync(workspaceId, folderId, targetIndex, cancellationToken);

    public Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid workspaceId, Guid folderId, string? search = null, CancellationToken cancellationToken = default) =>
        repository.GetItemsAsync(workspaceId, folderId, search, cancellationToken);

    public Task<CollectionItem> AddTextAsync(Guid workspaceId, Guid folderId, string title, string text, CancellationToken cancellationToken = default) =>
        repository.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, CollectionItemType.Text, title, text, ContentHash: TextContentService.ComputeHash(text), ContentSize: TextContentService.GetUtf8ByteCount(text)), cancellationToken);

    public Task<CollectionItem> AddLinkAsync(Guid workspaceId, Guid folderId, string title, string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("链接必须是有效的 http 或 https 地址。", nameof(url));
        return repository.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, CollectionItemType.Link, title, Url: uri.AbsoluteUri, ContentHash: TextContentService.ComputeHash(uri.AbsoluteUri), ContentSize: uri.AbsoluteUri.Length), cancellationToken);
    }

    public async Task<CollectionItem> AddImageAsync(Guid workspaceId, Guid folderId, string sourcePath, string? title = null, CancellationToken cancellationToken = default)
    {
        var asset = await assetStore.ImportImageAsync(sourcePath, cancellationToken);
        return await repository.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, CollectionItemType.Image, title ?? Path.GetFileName(sourcePath), LocalPath: asset.LocalPath, ThumbnailPath: asset.ThumbnailPath, ContentHash: asset.ContentHash, ContentSize: asset.ByteSize), cancellationToken);
    }

    public Task<CollectionItem> AddFileAsync(Guid workspaceId, Guid folderId, string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("文件或目录不存在。", path);
        return repository.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, CollectionItemType.File, Path.GetFileName(path), LocalPath: path, FileManifest: System.Text.Json.JsonSerializer.Serialize(new[] { path })), cancellationToken);
    }

    /// <summary>
    /// Copies a history record into <paramref name="workspaceId"/>. The history item is
    /// validated against the workspace history actually lives in — the local capture
    /// workspace — so collecting a local record into 团队配置 is allowed. It creates an
    /// independent snapshot that keeps <c>SourceClipboardItemId</c> for provenance; the
    /// original history record is never moved or re-homed.
    /// </summary>
    public async Task<CollectionItem> AddSnapshotAsync(Guid workspaceId, Guid folderId, ClipboardItem historyItem, CancellationToken cancellationToken = default)
    {
        var historyWorkspace = await workspaceService.GetCurrentAsync(cancellationToken);
        if (historyItem.WorkspaceId != historyWorkspace.Id || historyItem.DeletedAt is not null)
            throw new InvalidOperationException("历史记录不属于本机空间或已删除。");

        if (historyItem.ItemType == ClipboardItemType.ImageFile)
        {
            var paths = historyItem.FileManifest is null
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<string[]>(historyItem.FileManifest) ?? [];
            if (!ClipboardFileClassifier.IsSingleImageFile(paths) || !File.Exists(paths[0]))
                throw new FileNotFoundException("图片文件已不存在，无法存入配置。", paths.FirstOrDefault());
            var asset = await assetStore.ImportImageAsync(paths[0], cancellationToken);
            return await repository.CreateItemAsync(workspaceId, new CreateCollectionItem(
                folderId, CollectionItemType.Image, historyItem.PreviewText,
                LocalPath: asset.LocalPath, ThumbnailPath: asset.ThumbnailPath,
                ContentHash: asset.ContentHash, ContentSize: asset.ByteSize,
                SourceClipboardItemId: historyItem.Id, SourceWorkspaceId: historyWorkspace.Id), cancellationToken);
        }

        return await repository.CreateItemAsync(workspaceId, new CreateCollectionItem(folderId, historyItem.ItemType switch
        {
            ClipboardItemType.Text => Uri.TryCreate(historyItem.TextContent, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? CollectionItemType.Link : CollectionItemType.Text,
            ClipboardItemType.Image => CollectionItemType.Image,
            _ => CollectionItemType.File
        }, historyItem.PreviewText, historyItem.TextContent, historyItem.ItemType == ClipboardItemType.Text && Uri.IsWellFormedUriString(historyItem.TextContent, UriKind.Absolute) ? historyItem.TextContent : null, LocalPath: historyItem.ImagePath, ThumbnailPath: historyItem.ThumbnailPath, FileManifest: historyItem.FileManifest, ContentHash: historyItem.ContentHash, ContentSize: historyItem.ContentSize, SourceClipboardItemId: historyItem.Id, SourceWorkspaceId: historyWorkspace.Id), cancellationToken);
    }

    /// <summary>
    /// The `Ctrl+D` path. It resolves its own target so it can only ever land in the
    /// capture workspace's 未分类 folder: AGENTS.md requires putting content into a team
    /// space to be an explicit user action, so no default may resolve to
    /// <see cref="WorkspaceType.Team"/>.
    /// </summary>
    public async Task<CollectionItem> QuickCollectAsync(ClipboardItem historyItem, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        return await CollectToUncategorizedAsync(workspace.Id, historyItem, cancellationToken);
    }

    /// <summary>
    /// Explicitly copies a history record into the target config space's system folder.
    /// The caller must choose the target workspace; this is what lets the UI offer separate
    /// “个人配置” and “团队配置” commands without relying on ambient tab state.
    /// </summary>
    public async Task<CollectionItem> CollectToUncategorizedAsync(Guid workspaceId, ClipboardItem historyItem, CancellationToken cancellationToken = default)
    {
        var uncategorized = await repository.EnsureUncategorizedAsync(workspaceId, cancellationToken);
        return await AddSnapshotAsync(workspaceId, uncategorized.Id, historyItem, cancellationToken);
    }

    public async Task UpdateContentAsync(Guid workspaceId, CollectionItem item, string title, string content, CancellationToken cancellationToken = default)
    {
        if (item.WorkspaceId != workspaceId || item.DeletedAt is not null)
            throw new InvalidOperationException("配置项不属于目标空间或已删除。");
        if (item.ItemType is not (CollectionItemType.Text or CollectionItemType.Link))
            throw new InvalidOperationException("仅支持编辑文本和链接内容。");
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("内容不能为空。", nameof(content));
        if (item.ItemType == CollectionItemType.Link)
        {
            if (!Uri.TryCreate(content.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("链接必须是有效的 http 或 https 地址。", nameof(content));
            content = uri.AbsoluteUri;
        }
        await repository.UpdateItemAsync(item with
        {
            Title = title.Trim(),
            TextContent = item.ItemType == CollectionItemType.Text ? content : null,
            Url = item.ItemType == CollectionItemType.Link ? content : null,
            ContentHash = TextContentService.ComputeHash(content),
            ContentSize = TextContentService.GetUtf8ByteCount(content)
        }, cancellationToken);
    }

    public async Task UpdateTitleAsync(Guid workspaceId, CollectionItem item, string title, CancellationToken cancellationToken = default)
    {
        if (item.WorkspaceId != workspaceId || item.DeletedAt is not null)
            throw new InvalidOperationException("配置项不属于目标空间或已删除。");
        title = title.Trim();
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("配置名称不能为空。", nameof(title));
        if (title.Length > 200) throw new ArgumentException("配置名称不能超过 200 个字符。", nameof(title));
        await repository.UpdateItemAsync(item with { Title = title }, cancellationToken);
    }

    public Task MoveItemAsync(Guid workspaceId, Guid itemId, Guid folderId, int targetIndex, CancellationToken cancellationToken = default) =>
        repository.MoveItemAsync(workspaceId, itemId, folderId, targetIndex, cancellationToken);

    /// <summary>
    /// Moves an item one position towards the start (<paramref name="offset"/> -1) or the
    /// end (+1) of its folder. The index is resolved against the full folder contents, not
    /// a search-filtered view, and the target index excludes the moved item itself, which
    /// is the contract <see cref="MoveItemAsync"/> expects. Returns false when the item is
    /// already at that boundary, so a caller at the edge is a no-op rather than an error.
    /// </summary>
    public async Task<bool> MoveItemByOffsetAsync(Guid workspaceId, Guid itemId, Guid folderId, int offset, CancellationToken cancellationToken = default)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset), "只支持上移或下移一位。");
        var all = await repository.GetItemsAsync(workspaceId, folderId, null, cancellationToken);
        var current = -1;
        for (var i = 0; i < all.Count; i++) if (all[i].Id == itemId) { current = i; break; }
        if (current < 0) throw new InvalidOperationException("配置项不存在或不属于该文件夹。");
        var target = current + offset;
        if (target < 0 || target >= all.Count) return false;
        await repository.MoveItemAsync(workspaceId, itemId, folderId, target, cancellationToken);
        return true;
    }

    public Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default) =>
        repository.DeleteItemAsync(workspaceId, itemId, cancellationToken);
}
