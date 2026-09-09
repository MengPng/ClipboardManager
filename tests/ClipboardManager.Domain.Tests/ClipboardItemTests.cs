using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Tests;

public sealed class ClipboardItemTests
{
    [Fact]
    public void NewItem_DefaultsToPrivateLocalOnlyState()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new ClipboardItem
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            ItemType = ClipboardItemType.Text,
            ContentHash = "hash",
            CreatedAt = now,
            UpdatedAt = now,
            CapturedAt = now
        };

        Assert.Equal(ClipboardVisibility.Private, item.Visibility);
        Assert.Equal(SyncState.LocalOnly, item.SyncStatus);
        Assert.Null(item.DeletedAt);
    }
}
