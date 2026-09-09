namespace ClipboardManager.Domain.Models;

public enum ClipboardItemType
{
    Text = 0,
    Image = 1,
    Files = 2
}

public enum ClipboardVisibility
{
    Private = 0,
    Workspace = 1
}

public enum SyncState
{
    LocalOnly = 0,
    Pending = 1,
    Synced = 2,
    Failed = 3,
    Conflict = 4
}

public sealed record ClipboardItem
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public Guid? OwnerId { get; init; }
    public required Guid DeviceId { get; init; }
    public required ClipboardItemType ItemType { get; init; }
    public required string ContentHash { get; init; }
    public string? TextContent { get; init; }
    public string? PreviewText { get; init; }
    public string? SourceApp { get; init; }
    public string? SourceProcess { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsPinned { get; init; }
    public long UseCount { get; init; }
    public ClipboardVisibility Visibility { get; init; } = ClipboardVisibility.Private;
    public SyncState SyncStatus { get; init; } = SyncState.LocalOnly;
    public long LocalVersion { get; init; } = 1;
    public long? ServerVersion { get; init; }
    public string? FileManifest { get; init; }
    public string? ImagePath { get; init; }
    public string? ThumbnailPath { get; init; }
    public long ContentSize { get; init; }
}
