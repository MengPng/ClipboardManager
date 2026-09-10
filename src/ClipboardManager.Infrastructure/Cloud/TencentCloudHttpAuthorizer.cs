using ClipboardManager.Domain.Contracts;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Cloud;

public sealed class TencentCloudHttpAuthorizer(IAppSettingsStore settings, ICloudAccountService accounts, ICredentialVault vault)
{
    public async Task ConfigureAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var configured = (await settings.LoadAsync(cancellationToken)).CloudApiBaseUrl;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("请先配置有效的 HTTPS 云端服务地址。");
        var endpoint = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        if (http.BaseAddress is null) http.BaseAddress = endpoint;
        else if (http.BaseAddress != endpoint) throw new InvalidOperationException("云端地址已变更，请退出账号并重启软件。");

        if (await accounts.GetSessionAsync(cancellationToken) is null) throw new UnauthorizedAccessException("需要登录。");
        var saved = await vault.ReadAsync("CloudSession", cancellationToken) ?? throw new UnauthorizedAccessException("需要登录。");
        using var document = JsonDocument.Parse(saved);
        if (!document.RootElement.TryGetProperty("accessToken", out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("本机会话无效。");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value.GetString());
    }
}
