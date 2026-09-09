namespace ClipboardManager.Domain.Models;

public enum CollectionItemType
{
    Text = 0,
    Link = 1,
    Image = 2,
    File = 3
}

public sealed record CollectionFolder
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public Guid? ParentFolderId { get; init; }
    public required string Name { get; init; }
    public required decimal SortKey { get; init; }
    public bool IsSystem { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public long LocalVersion { get; init; } = 1;
    public long? ServerVersion { get; init; }
    public SyncState SyncStatus { get; init; } = SyncState.LocalOnly;
}

public sealed record CollectionItem
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required Guid FolderId { get; init; }
    public Guid? SourceClipboardItemId { get; init; }
    public required CollectionItemType ItemType { get; init; }
    public string? Title { get; init; }
    public string? TextContent { get; init; }
    public string? Url { get; init; }
    public string? Description { get; init; }
    public string? LocalPath { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? FileManifest { get; init; }
    public string? ContentHash { get; init; }
    public long ContentSize { get; init; }
    public required decimal SortKey { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public long LocalVersion { get; init; } = 1;
    public long? ServerVersion { get; init; }
    public SyncState SyncStatus { get; init; } = SyncState.LocalOnly;
}

public sealed record CreateCollectionItem(
    Guid FolderId,
    CollectionItemType ItemType,
    string? Title = null,
    string? TextContent = null,
    string? Url = null,
    string? Description = null,
    string? LocalPath = null,
    string? ThumbnailPath = null,
    string? FileManifest = null,
    string? ContentHash = null,
    long ContentSize = 0,
    Guid? SourceClipboardItemId = null,
    // The source history record may live in a different workspace than the snapshot,
    // which is how an explicit share into a team space keeps its provenance without
    // moving the original. Null means the source is expected in the target workspace.
    Guid? SourceWorkspaceId = null);
