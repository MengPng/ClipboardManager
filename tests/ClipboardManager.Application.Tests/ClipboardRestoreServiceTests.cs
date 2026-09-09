using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class ClipboardRestoreServiceTests
{
    [Fact]
    public async Task RestoreAsync_ArmSelfWriteGuardAcrossPayloadTypes_AndClearsOnFailure()
    {
        var clipboard = new FakeSystemClipboard();
        var guard = new SelfWriteGuard();
        var history = new FakeHistoryRepository();
        var historyService = new ClipboardHistoryService(history, new WorkspaceService(), new SettingsStore());
        var service = new ClipboardRestoreService(clipboard, historyService, guard);

        var now = DateTimeOffset.UtcNow;
        var textItem = new ClipboardItem
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.Id, DeviceId = Guid.NewGuid(),
            ItemType = ClipboardItemType.Text, TextContent = "hello", ContentHash = TextContentService.ComputeHash("hello"),
            CreatedAt = now, UpdatedAt = now, CapturedAt = now
        };
        await service.RestoreAsync(textItem);
        Assert.Equal("hello", clipboard.LastWrittenText);
        Assert.True(guard.TryConsume(ClipboardPayloadType.Text, textItem.ContentHash));

        var imageItem = new ClipboardItem
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.Id, DeviceId = Guid.NewGuid(),
            ItemType = ClipboardItemType.Image, ImagePath = "C:\\test\\image.png", ContentHash = "image-hash",
            CreatedAt = now, UpdatedAt = now, CapturedAt = now
        };
        await service.RestoreAsync(imageItem);
        Assert.Equal("C:\\test\\image.png", clipboard.LastWrittenImage);
        Assert.True(guard.TryConsume(ClipboardPayloadType.Image, imageItem.ContentHash));

        var filesItem = new ClipboardItem
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceService.Id, DeviceId = Guid.NewGuid(),
            ItemType = ClipboardItemType.Files, FileManifest = "[\"C:\\\\a.txt\",\"C:\\\\b.txt\"]", ContentHash = "files-hash",
            CreatedAt = now, UpdatedAt = now, CapturedAt = now
        };
        await service.RestoreAsync(filesItem);
        Assert.Equal(2, clipboard.LastWrittenFiles?.Count);
        Assert.True(guard.TryConsume(ClipboardPayloadType.Files, filesItem.ContentHash));

        var imageFileItem = filesItem with
        {
            Id = Guid.NewGuid(),
            ItemType = ClipboardItemType.ImageFile,
            FileManifest = "[\"C:\\\\photo.png\"]",
            ContentHash = "image-file-hash"
        };
        await service.RestoreAsync(imageFileItem);
        Assert.Equal("C:\\photo.png", Assert.Single(clipboard.LastWrittenFiles!));
        Assert.True(guard.TryConsume(ClipboardPayloadType.Files, imageFileItem.ContentHash));

        // When clipboard write fails, the guard must be cleared so next real copy isn't swallowed
        clipboard.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(textItem));
        Assert.False(guard.TryConsume(ClipboardPayloadType.Text, textItem.ContentHash));
    }

    private sealed class FakeSystemClipboard : ISystemClipboard
    {
        public string? LastWrittenText { get; private set; }
        public string? LastWrittenImage { get; private set; }
        public IReadOnlyList<string>? LastWrittenFiles { get; private set; }
        public bool FailNext { get; set; }

        public Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClipboardPayload(ClipboardPayloadType.None));
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(LastWrittenText);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("clipboard busy"); }
            LastWrittenText = text;
            return Task.CompletedTask;
        }
        public Task WriteImageAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("clipboard busy"); }
            LastWrittenImage = imagePath;
            return Task.CompletedTask;
        }
        public Task WriteFilesAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("clipboard busy"); }
            LastWrittenFiles = files;
            return Task.CompletedTask;
        }
    }

    private sealed class WorkspaceService : IWorkspaceService
    {
        public static readonly Guid Id = Guid.NewGuid();
        private readonly Workspace _w = new(Id, WorkspaceType.Local, "Local", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Workspace>>([_w]);
        public Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_w);
        public Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SettingsStore : IAppSettingsStore
    {
        private readonly AppSettings _s = new();
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_s);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeHistoryRepository : IClipboardHistoryRepository
    {
        public Task<CaptureResult> CaptureTextAsync(TextCapture capture, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CleanupAsync(Guid workspaceId, DateTimeOffset olderThan, int maxActiveItems, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<ClipboardItem?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ClipboardItem?>(null);
        public Task MarkUsedAsync(Guid workspaceId, Guid itemId, DateTimeOffset usedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ClipboardItem>> SearchAsync(ClipboardQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClipboardItem>>([]);
        public Task SetFavoriteAsync(Guid workspaceId, Guid itemId, bool isFavorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPinnedAsync(Guid workspaceId, Guid itemId, bool isPinned, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteAsync(Guid workspaceId, Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertAsync(ClipboardItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
