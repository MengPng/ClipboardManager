using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ITodoRepository
{
    Task<IReadOnlyList<TodoTag>> GetTagsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<TodoTag> CreateTagAsync(Guid workspaceId, string name, string color, CancellationToken cancellationToken = default);
    Task<TodoTag> UpdateTagAsync(Guid workspaceId, Guid tagId, string name, string color, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(Guid workspaceId, Guid tagId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TodoItem>> GetItemsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<TodoItem> CreateItemAsync(
        Guid workspaceId,
        string title,
        IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default);
    Task<TodoItem> UpdateItemAsync(
        Guid workspaceId,
        Guid itemId,
        string title,
        IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default);
    Task<TodoItem> SetCompletedAsync(
        Guid workspaceId,
        Guid itemId,
        bool completed,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task DeleteItemAsync(Guid workspaceId, Guid itemId, CancellationToken cancellationToken = default);
    Task<int> ClearCompletedAsync(Guid workspaceId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);
}
