using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Cloud;

public sealed class TencentCloudAccountService(HttpClient http, ICredentialVault vault, IAppSettingsStore settings) : ICloudAccountService
{
    private const string SessionKey = "CloudSession";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim sessionGate = new(1, 1);

    public async Task<CloudSession?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
        var saved = await vault.ReadAsync(SessionKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(saved)) return null;
        await EnsureBaseAddressAsync(cancellationToken);
        var stored = JsonSerializer.Deserialize<StoredSession>(saved, Json);
        if (stored is null) return null;
        if (stored.Endpoint != http.BaseAddress!.AbsoluteUri)
            throw new UnauthorizedAccessException("会话不属于当前云端地址，请重新登录。");
        if (stored.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            if (string.IsNullOrWhiteSpace(stored.RefreshToken)) return null;
            stored = await PostSessionAsync("v1/auth/refresh", new { stored.RefreshToken }, cancellationToken);
            await SaveAsync(stored, cancellationToken);
        }
        ApplyBearer(stored.AccessToken);
        return stored.ToPublic();
        }
        finally { sessionGate.Release(); }
    }

    public async Task<CloudSession> SignInAsync(CloudSignInRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password)) throw new ArgumentException("邮箱和密码不能为空。");
        var stored = await PostSessionAsync("v1/auth/sign-in", request, cancellationToken);
        await SaveAsync(stored, cancellationToken);
        ApplyBearer(stored.AccessToken);
        return stored.ToPublic();
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        await EnsureBaseAddressAsync(cancellationToken);
        using var response = await http.PostAsJsonAsync("v1/auth/password-reset", new { email }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SignOutAsync(bool allDevices, CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
        var saved = await vault.ReadAsync(SessionKey, cancellationToken);
        if (saved is not null)
        {
            var stored = JsonSerializer.Deserialize<StoredSession>(saved, Json);
            if (stored is not null)
            {
                await EnsureBaseAddressAsync(cancellationToken);
                if (stored.Endpoint != http.BaseAddress!.AbsoluteUri)
                    throw new UnauthorizedAccessException("会话地址已变更；已清除本机会话，原服务会话需另行撤销。");
                ApplyBearer(stored.AccessToken);
                using var response = await http.PostAsJsonAsync("v1/auth/sign-out", new { allDevices }, cancellationToken);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) await EnsureSuccessAsync(response, cancellationToken);
            }
        }
        }
        finally
        {
            try
            {
                http.DefaultRequestHeaders.Authorization = null;
                await vault.DeleteAsync(SessionKey, CancellationToken.None);
            }
            finally { sessionGate.Release(); }
        }
    }

    private async Task<StoredSession> PostSessionAsync(string route, object body, CancellationToken cancellationToken)
    {
        await EnsureBaseAddressAsync(cancellationToken);
        using var response = await http.PostAsJsonAsync(route, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var session = await response.Content.ReadFromJsonAsync<StoredSession>(Json, cancellationToken) ?? throw new InvalidDataException("云端返回了无效会话。");
        return session with { Endpoint = http.BaseAddress!.AbsoluteUri };
    }

    private Task SaveAsync(StoredSession session, CancellationToken cancellationToken) => vault.WriteAsync(SessionKey, JsonSerializer.Serialize(session, Json), cancellationToken);
    private void ApplyBearer(string token) => http.DefaultRequestHeaders.Authorization = new("Bearer", token);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var problem = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"云端请求失败（{(int)response.StatusCode}）：{problem}", null, response.StatusCode);
    }

    private async Task EnsureBaseAddressAsync(CancellationToken cancellationToken)
    {
        var configured = (await settings.LoadAsync(cancellationToken)).CloudApiBaseUrl;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("请先配置有效的 HTTPS 云端服务地址。");
        var endpoint = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        if (http.BaseAddress is null) http.BaseAddress = endpoint;
        else if (http.BaseAddress != endpoint) throw new InvalidOperationException("云端地址已变更，请退出账号并重启软件。");
    }

    private sealed record StoredSession(Guid UserId, string Email, string DisplayName, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? Endpoint = null)
    {
        public CloudSession ToPublic() => new(UserId, Email, DisplayName, ExpiresAt);
    }
}
