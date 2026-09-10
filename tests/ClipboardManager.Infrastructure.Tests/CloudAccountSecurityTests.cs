using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Cloud;
using System.Net;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class CloudAccountSecurityTests
{
    [Fact]
    public async Task ChangedEndpointNeverReceivesSavedRefreshToken()
    {
        var vault=new Vault { Value=Saved("https://original.invalid/") };
        var handler=new Handler();
        var service=new TencentCloudAccountService(new HttpClient(handler),vault,new Settings());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetSessionAsync());
        Assert.Equal(0,handler.Calls);
    }

    [Fact]
    public async Task FailedRemoteLogoutStillDeletesLocalSession()
    {
        var vault=new Vault { Value=Saved("https://cloud.invalid/") };
        var service=new TencentCloudAccountService(new HttpClient(new Handler()),vault,new Settings());
        await Assert.ThrowsAsync<HttpRequestException>(()=>service.SignOutAsync(true));
        Assert.Null(vault.Value);
    }

    private static string Saved(string endpoint)=>JsonSerializer.Serialize(new
    {
        userId=Guid.NewGuid(),email="test@example.invalid",displayName="test",accessToken="test-access",
        refreshToken="test-refresh",expiresAt=DateTimeOffset.UtcNow.AddMinutes(-1),endpoint
    });
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("unavailable")}); }
    }
    private sealed class Vault:ICredentialVault
    {
        public string? Value;
        public Task<string?> ReadAsync(string key,CancellationToken ct=default)=>Task.FromResult(Value);
        public Task WriteAsync(string key,string secret,CancellationToken ct=default){Value=secret;return Task.CompletedTask;}
        public Task DeleteAsync(string key,CancellationToken ct=default){Value=null;return Task.CompletedTask;}
    }
    private sealed class Settings:IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct=default)=>Task.FromResult(new AppSettings{CloudApiBaseUrl="https://cloud.invalid/"});
        public Task SaveAsync(AppSettings settings,CancellationToken ct=default)=>Task.CompletedTask;
    }
}
