using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class ClipboardPayloadCaptureServiceTests
{
    [Fact]
    public async Task CaptureAsync_StoresImageAndFilePayloads()
    {
        var repository = new Repository();
        var settings = new Settings();
        var workspace = new WorkspaceService();
        var device = new DeviceStore();
        var identity = new IdentityProvider();
        var asset = new AssetStore();
        var textService = new ClipboardCaptureService(repository, settings, workspace, device, identity, new SelfWriteGuard());
        var service = new ClipboardPayloadCaptureService(textService, repository, workspace, device, identity, asset, settings, new SelfWriteGuard());

        var image = await service.CaptureAsync(new ClipboardPayload(ClipboardPayloadType.Image, ImageBytes: [1, 2, 3], ImageMimeType: "image/png"), new ClipboardSource("Test", "test"));
        var files = await service.CaptureAsync(new ClipboardPayload(ClipboardPayloadType.Files, Files: [Path.GetTempPath(), Path.GetTempPath()]), new ClipboardSource("Test", "test"));
        var imageFile = await service.CaptureAsync(new ClipboardPayload(ClipboardPayloadType.Files, Files: [Path.Combine(Path.GetTempPath(), "photo.PNG")]), new ClipboardSource("Test", "test"));

        Assert.Equal(ClipboardItemType.Image, image.Item!.ItemType);
        Assert.Equal("managed.png", image.Item.ImagePath);
        Assert.Equal(ClipboardItemType.Files, files.Item!.ItemType);
        Assert.Equal(ClipboardItemType.ImageFile, imageFile.Item!.ItemType);
        Assert.Contains(Path.GetFullPath(Path.GetTempPath()), System.Text.Json.JsonSerializer.Deserialize<string[]>(files.Item.FileManifest!)!);
        Assert.Equal(3, repository.Items.Count);
    }

    [Fact]
    public async Task CaptureAsync_IgnoresExpectedImageAndFileSelfWritesOnce()
    {
        var repository = new Repository(); var settings = new Settings(); var workspace = new WorkspaceService();
        var device = new DeviceStore(); var identity = new IdentityProvider(); var asset = new AssetStore(); var guard = new SelfWriteGuard();
        var text = new ClipboardCaptureService(repository, settings, workspace, device, identity, guard);
        var service = new ClipboardPayloadCaptureService(text, repository, workspace, device, identity, asset, settings, guard);
        guard.Expect(ClipboardPayloadType.Image, "image-hash");
        Assert.Equal(CaptureDisposition.IgnoredSelfWrite, (await service.CaptureAsync(new ClipboardPayload(ClipboardPayloadType.Image, ImageBytes: [1]), new ClipboardSource(null, null))).Disposition);
        Assert.Equal(CaptureDisposition.Created, (await service.CaptureAsync(new ClipboardPayload(ClipboardPayloadType.Image, ImageBytes: [1]), new ClipboardSource(null, null))).Disposition);
        var path = Path.GetFullPath(Path.GetTempPath());
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        guard.Expect(ClipboardPayloadType.Files, hash);
        Assert.Equal(CaptureDisposition.IgnoredSelfWrite, (await service.CaptureAsync(new ClipboardPayload(ClipboardPayloadType.Files, Files: [path]), new ClipboardSource(null, null))).Disposition);
    }

    private sealed class Repository : IClipboardHistoryRepository
    {
        public List<ClipboardItem> Items { get; } = [];
        public Task UpsertAsync(ClipboardItem item, CancellationToken cancellationToken = default) { Items.RemoveAll(x => x.Id == item.Id); Items.Add(item); return Task.CompletedTask; }
        public Task<IReadOnlyList<ClipboardItem>> SearchAsync(ClipboardQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClipboardItem>>(Items.Where(x => x.WorkspaceId == query.WorkspaceId && (query.ItemType is null || x.ItemType == query.ItemType)).ToArray());
        public Task<CaptureResult> CaptureTextAsync(TextCapture capture, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipboardItem?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.FirstOrDefault(x => x.Id == id));
        public Task SoftDeleteAsync(Guid workspaceId, Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFavoriteAsync(Guid workspaceId, Guid itemId, bool isFavorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPinnedAsync(Guid workspaceId, Guid itemId, bool isPinned, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkUsedAsync(Guid workspaceId, Guid itemId, DateTimeOffset usedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CleanupAsync(Guid workspaceId, DateTimeOffset olderThan, int maxActiveItems, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
    private sealed class Settings : IAppSettingsStore { private readonly AppSettings _s = new() { ExcludedProcesses = [] }; public Task<AppSettings> LoadAsync(CancellationToken c = default) => Task.FromResult(_s); public Task SaveAsync(AppSettings s, CancellationToken c = default) => Task.CompletedTask; }
    private sealed class WorkspaceService : IWorkspaceService { public static readonly Guid Id = Guid.NewGuid(); private readonly Workspace _w = new(Id, WorkspaceType.Local, "L", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); public Task<Workspace> GetCurrentAsync(CancellationToken c = default) => Task.FromResult(_w); public Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<Workspace>>([_w]); public Task SwitchAsync(Guid i, CancellationToken c = default) => Task.CompletedTask; }
    private sealed class DeviceStore : IDeviceIdentityStore { public Task<DeviceIdentity> GetOrCreateAsync(CancellationToken c = default) => Task.FromResult(new DeviceIdentity(Guid.NewGuid(), "D", "W", DateTimeOffset.UtcNow)); }
    private sealed class IdentityProvider : IIdentityProvider { public Task<IdentitySession?> GetSessionAsync(CancellationToken c = default) => Task.FromResult<IdentitySession?>(new IdentitySession(Guid.NewGuid(), "local", "x", "x", true)); public Task<string?> GetAccessTokenAsync(CancellationToken c = default) => Task.FromResult<string?>(null); public Task<IdentitySession> SignInAsync(SignInRequest r, CancellationToken c = default) => throw new NotSupportedException(); public Task SignOutAsync(CancellationToken c = default) => Task.CompletedTask; }
    private sealed class AssetStore : ILocalAssetStore { public Task<StoredAsset> StoreImageAsync(byte[] bytes, string mime, CancellationToken c = default) => Task.FromResult(new StoredAsset("managed.png", "thumb.png", "image-hash", bytes.Length, mime)); public Task<StoredAsset> ImportImageAsync(string p, CancellationToken c = default) => throw new NotSupportedException(); }
}
