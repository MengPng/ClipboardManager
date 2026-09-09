using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class CollectionService(
    ICollectionRepository repository,
    IWorkspaceService workspaceService,
    ILocalAssetStore assetStore)
{
    public async Task<(IReadOnlyList<CollectionFolder> Folders, CollectionFolder Uncategorized)> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var uncategorized = await repository.EnsureUncategorizedAsync(workspace.Id, cancellationToken);
        return (await repository.GetFoldersAsync(workspace.Id, cancellationToken), uncategorized);
    }

    public async Task<CollectionFolder> CreateFolderAsync(string name, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        return await repository.CreateFolderAsync(workspace.Id, name, cancellationToken);
    }

    public async Task RenameFolderAsync(Guid folderId, string name, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.RenameFolderAsync(workspace.Id, folderId, name, cancellationToken);
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var uncategorized = await repository.EnsureUncategorizedAsync(workspace.Id, cancellationToken);
        await repository.DeleteFolderAsync(workspace.Id, folderId, uncategorized.Id, cancellationToken);
    }

    public async Task ReorderFolderAsync(Guid folderId, int targetIndex, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.ReorderFolderAsync(workspace.Id, folderId, targetIndex, cancellationToken);
    }

    public async Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid folderId, string? search = null, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        return await repository.GetItemsAsync(workspace.Id, folderId, search, cancellationToken);
    }

    public Task<CollectionItem> AddTextAsync(Guid folderId, string title, string text, CancellationToken cancellationToken = default) =>
        AddAsync(new CreateCollectionItem(folderId, CollectionItemType.Text, title, text, ContentHash: TextContentService.ComputeHash(text), ContentSize: TextContentService.GetUtf8ByteCount(text)), cancellationToken);

    public Task<CollectionItem> AddLinkAsync(Guid folderId, string title, string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("链接必须是有效的 http 或 https 地址。", nameof(url));
        return AddAsync(new CreateCollectionItem(folderId, CollectionItemType.Link, title, Url: uri.AbsoluteUri, ContentHash: TextContentService.ComputeHash(uri.AbsoluteUri), ContentSize: uri.AbsoluteUri.Length), cancellationToken);
    }

    public async Task<CollectionItem> AddImageAsync(Guid folderId, string sourcePath, string? title = null, CancellationToken cancellationToken = default)
    {
        var asset = await assetStore.ImportImageAsync(sourcePath, cancellationToken);
        return await AddAsync(new CreateCollectionItem(folderId, CollectionItemType.Image, title ?? Path.GetFileName(sourcePath), LocalPath: asset.LocalPath, ThumbnailPath: asset.ThumbnailPath, ContentHash: asset.ContentHash, ContentSize: asset.ByteSize), cancellationToken);
    }

    public Task<CollectionItem> AddFileAsync(Guid folderId, string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("文件或目录不存在。", path);
        return AddAsync(new CreateCollectionItem(folderId, CollectionItemType.File, Path.GetFileName(path), LocalPath: path, FileManifest: System.Text.Json.JsonSerializer.Serialize(new[] { path })), cancellationToken);
    }

    public async Task<CollectionItem> AddSnapshotAsync(Guid folderId, ClipboardItem historyItem, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        if (historyItem.WorkspaceId != workspace.Id || historyItem.DeletedAt is not null)
            throw new InvalidOperationException("历史记录不属于当前空间或已删除。");
        return await repository.CreateItemAsync(workspace.Id, new CreateCollectionItem(folderId, historyItem.ItemType switch
        {
            ClipboardItemType.Text => Uri.TryCreate(historyItem.TextContent, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? CollectionItemType.Link : CollectionItemType.Text,
            ClipboardItemType.Image => CollectionItemType.Image,
            _ => CollectionItemType.File
        }, historyItem.PreviewText, historyItem.TextContent, historyItem.ItemType == ClipboardItemType.Text && Uri.IsWellFormedUriString(historyItem.TextContent, UriKind.Absolute) ? historyItem.TextContent : null, LocalPath: historyItem.ImagePath, ThumbnailPath: historyItem.ThumbnailPath, FileManifest: historyItem.FileManifest, ContentHash: historyItem.ContentHash, ContentSize: historyItem.ContentSize, SourceClipboardItemId: historyItem.Id), cancellationToken);
    }

    public async Task UpdateContentAsync(CollectionItem item, string title, string content, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        if (item.WorkspaceId != workspace.Id || item.DeletedAt is not null)
            throw new InvalidOperationException("收藏不属于当前空间或已删除。");
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

    public async Task MoveItemAsync(Guid itemId, Guid folderId, int targetIndex, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.MoveItemAsync(workspace.Id, itemId, folderId, targetIndex, cancellationToken);
    }

    public async Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        await repository.DeleteItemAsync(workspace.Id, itemId, cancellationToken);
    }

    private async Task<CollectionItem> AddAsync(CreateCollectionItem item, CancellationToken cancellationToken)
    {
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        return await repository.CreateItemAsync(workspace.Id, item, cancellationToken);
    }
}
