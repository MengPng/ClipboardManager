using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ICollectionRepository
{
    Task<CollectionFolder> EnsureUncategorizedAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CollectionFolder>> GetFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<CollectionFolder> CreateFolderAsync(Guid workspaceId, string name, CancellationToken cancellationToken = default);
    Task RenameFolderAsync(Guid workspaceId, Guid folderId, string name, CancellationToken cancellationToken = default);
    Task DeleteFolderAsync(Guid workspaceId, Guid folderId, Guid fallbackFolderId, CancellationToken cancellationToken = default);
    Task ReorderFolderAsync(Guid workspaceId, Guid folderId, int targetIndex, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectionItem>> GetItemsAsync(Guid workspaceId, Guid folderId, string? searchText = null, CancellationToken cancellationToken = default);
    Task<CollectionItem> CreateItemAsync(Guid workspaceId, CreateCollectionItem item, CancellationToken cancellationToken = default);
    Task UpdateItemAsync(CollectionItem item, CancellationToken cancellationToken = default);
    Task MoveItemAsync(Guid workspaceId, Guid itemId, Guid targetFolderId, int targetIndex, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default);
}
