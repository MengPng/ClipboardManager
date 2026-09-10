using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Cloud;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class CloudModeServiceTests
{
    [Fact]
    public async Task CloudMode_RequiresHttpsEndpoint()
    {
        var store = new MemorySettings(new AppSettings());
        var service = new CloudModeService(store, new Account(new CloudSession(Guid.NewGuid(), "a@example.test", "A", DateTimeOffset.UtcNow.AddHours(1))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetModeAsync(AppMode.Cloud));
        Assert.Equal(AppMode.Local, service.Mode);
        Assert.False(store.Value.CloudModeEnabled);
    }

    [Fact]
    public async Task CloudMode_RequiresSession()
    {
        var store = new MemorySettings(new AppSettings { CloudApiBaseUrl = "https://cloud.example.test" });
        var service = new CloudModeService(store, new Account(null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetModeAsync(AppMode.Cloud));
        Assert.Equal(CloudConnectionState.NeedsSignIn, service.Status.State);
    }

    [Fact]
    public async Task CloudMode_PersistsOnlyAfterEndpointAndSessionAreValid()
    {
        var store = new MemorySettings(new AppSettings { CloudApiBaseUrl = "https://cloud.example.test" });
        var service = new CloudModeService(store, new Account(new CloudSession(Guid.NewGuid(), "a@example.test", "A", DateTimeOffset.UtcNow.AddHours(1))));

        await service.SetModeAsync(AppMode.Cloud);

        Assert.True(store.Value.CloudModeEnabled);
        Assert.Equal(CloudConnectionState.Connected, service.Status.State);
        Assert.Equal("已登录，尚未同步", service.Status.Message);
    }

    private sealed class MemorySettings(AppSettings value) : IAppSettingsStore
    {
        public AppSettings Value { get; private set; } = value;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class Account(CloudSession? session) : ICloudAccountService
    {
        public Task<CloudSession?> GetSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(session);
        public Task<CloudSession> SignInAsync(CloudSignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(bool allDevices, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
