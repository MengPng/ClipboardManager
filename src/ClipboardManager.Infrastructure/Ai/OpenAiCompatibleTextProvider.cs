using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Ai;

public sealed class OpenAiCompatibleTextProvider(HttpClient client, ICredentialVault credentials) : ITextAiProvider
{
    public async Task<AiTextResponse> CompleteAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        SelectionAiSettings.Validate(request.Settings);
        var secret = await credentials.ReadAsync(request.Settings.CredentialKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret) && request.Settings.Provider != "LocalGateway")
            throw new AiRequestException("尚未设置 API Key，请在划词与 AI 设置中保存密钥。");
        var endpoint = request.Settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(secret))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        message.Content = JsonContent.Create(new
        {
            model = request.Settings.Model,
            messages = new[]
            {
                new { role = "system", content = "Return plain text only. Do not use Markdown headings, emphasis markers, code fences, tables, or other Markdown formatting. Preserve meaningful paragraph breaks." },
                new { role = "user", content = request.Prompt }
            },
            stream = false
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new AiRequestException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 鉴权失败，请检查密钥与接口权限。",
                    HttpStatusCode.TooManyRequests => "模型请求过于频繁或额度不足，请稍后重试并检查服务商额度。",
                    HttpStatusCode.NotFound => "未找到模型或接口，请检查 API 地址与模型名称。",
                    _ => $"模型服务返回 HTTP {(int)response.StatusCode}，请稍后重试。"
                });
            // Never surface response bodies in errors: providers can echo input text.
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token);
            using var json = JsonDocument.Parse(bytes);
            var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text)) throw new AiRequestException("模型返回了空结果。");
            return new AiTextResponse(text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AiRequestException("模型请求超时，可重新发送。"); }
        catch (HttpRequestException) when (request.Settings.Provider == "LocalGateway")
        {
            var address = new Uri(request.Settings.BaseUrl);
            throw new AiRequestException($"无法连接本地网关（{address.Host}:{address.Port}），请确认网关已启动，端口与网关界面一致。");
        }
        catch (HttpRequestException)
        { throw new AiRequestException("无法连接模型服务，请检查网络及 API 地址。"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new AiRequestException("模型返回格式不兼容，请检查接口是否支持 Chat Completions。"); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int limit = 2 * 1024 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken)) > 0)
        {
            if (buffer.Length + count > limit) throw new AiRequestException("模型结果过大，请缩短原文或调整提示词。");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }
}
