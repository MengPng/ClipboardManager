using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Tests;

public sealed class ClipboardCaptureServiceTests
{
    [Fact]
    public async Task CaptureText_RejectsWhitespaceOversizeExcludedAndSelfWrite()
    {
        var fixture = new CaptureFixture(new AppSettings
        {
            MaxTextBytes = 4,
            ExcludedProcesses = ["SecretApp"]
        });

        Assert.Equal(CaptureDisposition.IgnoredEmpty,
            (await fixture.Service.CaptureTextAsync("  \r\n", new ClipboardSource(null, null))).Disposition);
        Assert.Equal(CaptureDisposition.IgnoredTooLarge,
            (await fixture.Service.CaptureTextAsync("12345", new ClipboardSource(null, null))).Disposition);
        Assert.Equal(CaptureDisposition.IgnoredExcludedApplication,
            (await fixture.Service.CaptureTextAsync("ok", new ClipboardSource(null, "SecretApp.exe"))).Disposition);

        fixture.Guard.Expect(TextContentService.ComputeHash("same"));
        Assert.Equal(CaptureDisposition.IgnoredSelfWrite,
            (await fixture.Service.CaptureTextAsync("same", new ClipboardSource(null, "Editor"))).Disposition);
        Assert.Empty(fixture.Repository.Captures);
    }

    [Fact]
    public async Task CaptureText_PreservesOriginalTextAndCreatesPreview()
    {
        var fixture = new CaptureFixture(new AppSettings { MaxTextBytes = 1024, ExcludedProcesses = [] });
        const string text = "  first\r\n\tsecond  ";

        var result = await fixture.Service.CaptureTextAsync(text, new ClipboardSource("Editor", "editor"));

        Assert.Equal(CaptureDisposition.Created, result.Disposition);
        Assert.Equal(text, fixture.Repository.Captures.Single().Text);
        Assert.Equal("first second", fixture.Repository.Captures.Single().Preview);
    }

    private sealed class CaptureFixture
    {
        public CaptureFixture(AppSettings settings)
        {
            Repository = new FakeRepository();
            Guard = new SelfWriteGuard();
            Service = new ClipboardCaptureService(
                Repository,
                new FakeSettingsStore(settings),
                new FakeWorkspaceService(),
                new FakeDeviceStore(),
                new FakeIdentityProvider(),
                Guard);
        }

        public FakeRepository Repository { get; }
        public SelfWriteGuard Guard { get; }
        public ClipboardCaptureService Service { get; }
    }

    private sealed class FakeRepository : IClipboardHistoryRepository
    {
        public List<TextCapture> Captures { get; } = [];
        public Task<CaptureResult> CaptureTextAsync(TextCapture capture, CancellationToken cancellationToken = default)
        {
            Captures.Add(capture);
            var item = new ClipboardItem
            {
                Id = Guid.NewGuid(), WorkspaceId = capture.WorkspaceId, OwnerId = capture.OwnerId,
                DeviceId = capture.DeviceId, ItemType = ClipboardItemType.Text, ContentHash = capture.ContentHash,
                TextContent = capture.Text, PreviewText = capture.Preview, CreatedAt = capture.CapturedAt,
                UpdatedAt = capture.CapturedAt, CapturedAt = capture.CapturedAt, ContentSize = capture.ContentSize
            };
            return Task.FromResult(new CaptureResult(CaptureDisposition.Created, item));
        }
        public Task<ClipboardItem?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ClipboardItem?>(null);
        public Task<IReadOnlyList<ClipboardItem>> SearchAsync(ClipboardQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClipboardItem>>([]);
        public Task UpsertAsync(ClipboardItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SoftDeleteAsync(Guid workspaceId, Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFavoriteAsync(Guid workspaceId, Guid itemId, bool isFavorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPinnedAsync(Guid workspaceId, Guid itemId, bool isPinned, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkUsedAsync(Guid workspaceId, Guid itemId, DateTimeOffset usedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CleanupAsync(Guid workspaceId, DateTimeOffset olderThan, int maxActiveItems, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FakeSettingsStore(AppSettings settings) : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        private readonly Workspace _workspace = new(Guid.NewGuid(), WorkspaceType.Local, "Local", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Workspace>>([_workspace]);
        public Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_workspace);
        public Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDeviceStore : IDeviceIdentityStore
    {
        private readonly DeviceIdentity _device = new(Guid.NewGuid(), "test", "test", DateTimeOffset.UtcNow);
        public Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(_device);
    }

    private sealed class FakeIdentityProvider : IIdentityProvider
    {
        private readonly IdentitySession _session = new(Guid.NewGuid(), "local", "test", "test", true);
        public Task<IdentitySession?> GetSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult<IdentitySession?>(_session);
        public Task<IdentitySession> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) => Task.FromResult(_session);
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
