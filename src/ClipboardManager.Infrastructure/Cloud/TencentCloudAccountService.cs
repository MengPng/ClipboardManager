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

    public async Task<CloudSession?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var saved = await vault.ReadAsync(SessionKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(saved)) return null;
        var stored = JsonSerializer.Deserialize<StoredSession>(saved, Json);
        if (stored is null) return null;
        if (stored.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            if (string.IsNullOrWhiteSpace(stored.RefreshToken)) return null;
            stored = await PostSessionAsync("v1/auth/refresh", new { stored.RefreshToken }, cancellationToken);
            await SaveAsync(stored, cancellationToken);
        }
        ApplyBearer(stored.AccessToken);
        return stored.ToPublic();
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
        var saved = await vault.ReadAsync(SessionKey, cancellationToken);
        if (saved is not null)
        {
            var stored = JsonSerializer.Deserialize<StoredSession>(saved, Json);
            if (stored is not null)
            {
                await EnsureBaseAddressAsync(cancellationToken);
                ApplyBearer(stored.AccessToken);
                using var response = await http.PostAsJsonAsync("v1/auth/sign-out", new { allDevices }, cancellationToken);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) await EnsureSuccessAsync(response, cancellationToken);
            }
        }
        http.DefaultRequestHeaders.Authorization = null;
        await vault.DeleteAsync(SessionKey, cancellationToken);
    }

    private async Task<StoredSession> PostSessionAsync(string route, object body, CancellationToken cancellationToken)
    {
        await EnsureBaseAddressAsync(cancellationToken);
        using var response = await http.PostAsJsonAsync(route, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<StoredSession>(Json, cancellationToken) ?? throw new InvalidDataException("云端返回了无效会话。");
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
        http.BaseAddress = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    private sealed record StoredSession(Guid UserId, string Email, string DisplayName, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
    {
        public CloudSession ToPublic() => new(UserId, Email, DisplayName, ExpiresAt);
    }
}
