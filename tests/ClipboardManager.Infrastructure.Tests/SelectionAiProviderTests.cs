using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Ai;
using ClipboardManager.Infrastructure.Paths;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class SelectionAiProviderTests
{
    [Fact]
    public async Task ProviderCanBeResolvedFromCompositionRoot()
    {
        await Task.Run(() =>
        {
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddInfrastructure(Path.GetTempPath());
            using var services = registrations.BuildServiceProvider();
            Assert.IsType<OpenAiCompatibleTextProvider>(services.GetRequiredService<ITextAiProvider>());
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }
    [Theory]
    [InlineData("DeepSeek", "https://api.deepseek.com", "deepseek-flash")]
    [InlineData("OpenAICompatible", "https://example.invalid/v1/", "custom-model")]
    [InlineData("LocalGateway", "http://127.0.0.1:8317/v1/", "local-model")]
    public async Task SendsExactActionBodyToConfiguredEndpoint(string provider, string url, string model)
    {
        var settings = new SelectionAiSettings { Provider = provider, BaseUrl = url, Model = model };
        var handler = new Handler(async (request, _) =>
        {
            Assert.Equal(url.TrimEnd('/') + "/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(model, json.RootElement.GetProperty("model").GetString());
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Contains("plain text", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal("确认后的完整正文", messages[1].GetProperty("content").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            return Response(HttpStatusCode.OK, """{"choices":[{"message":{"content":"结果"}}]}""");
        });
        var result = await new OpenAiCompatibleTextProvider(new HttpClient(handler), new Vault(settings.CredentialKey))
            .CompleteAsync(new(settings, "确认后的完整正文"));
        Assert.Equal("结果", result.Text);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LocalGatewayWithoutSavedKeyOmitsAuthorization()
    {
        var settings = new SelectionAiSettings { Provider = "LocalGateway", BaseUrl = "http://localhost:8317/v1" };
        var handler = new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Response(HttpStatusCode.OK, """{"choices":[{"message":{"content":"local result"}}]}"""));
        });
        var provider = new OpenAiCompatibleTextProvider(new HttpClient(handler), new Vault("unrelated-address"));
        Assert.Equal("local result", (await provider.CompleteAsync(new(settings, "confirmed local text"))).Text);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LocalConnectionFailureIdentifiesPortWithoutLeakingPayload()
    {
        var settings = new SelectionAiSettings { Provider = "LocalGateway", BaseUrl = "http://127.0.0.1:8317/v1" };
        var handler = new Handler((_, _) => throw new HttpRequestException("sensitive-input test-key"));
        var provider = new OpenAiCompatibleTextProvider(new HttpClient(handler), new Vault(settings.CredentialKey));
        var error = await Assert.ThrowsAsync<AiRequestException>(() => provider.CompleteAsync(new(settings, "sensitive-input")));
        Assert.Contains("127.0.0.1:8317", error.Message);
        Assert.Contains("端口", error.Message);
        Assert.DoesNotContain("sensitive-input", error.ToString());
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, "鉴权")]
    [InlineData(403, "鉴权")]
    [InlineData(429, "额度")]
    [InlineData(404, "地址")]
    [InlineData(500, "HTTP 500")]
    [InlineData(302, "HTTP 302")]
    public async Task ErrorDoesNotExposeProviderEchoOrSecret(int status, string expected)
    {
        var settings = new SelectionAiSettings();
        var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)status, "sensitive-input test-key")));
        var error = await Assert.ThrowsAsync<AiRequestException>(() =>
            new OpenAiCompatibleTextProvider(new HttpClient(handler), new Vault(settings.CredentialKey)).CompleteAsync(new(settings, "sensitive-input")));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("sensitive-input", error.ToString());
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EndpointChangeDoesNotReuseCredentialAndDoesNotSend()
    {
        var old = new SelectionAiSettings();
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        var client = new OpenAiCompatibleTextProvider(new HttpClient(handler), new Vault(old.CredentialKey));
        await Assert.ThrowsAsync<AiRequestException>(() => client.CompleteAsync(new(old with { BaseUrl = "https://unrelated.invalid" }, "text")));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationTimeoutNetworkAndMalformedResponseAreRecoverable()
    {
        var settings = new SelectionAiSettings();
        async Task<AiRequestException> Error(Handler handler) =>
            await Assert.ThrowsAsync<AiRequestException>(() =>
                new OpenAiCompatibleTextProvider(new HttpClient(handler), new Vault(settings.CredentialKey)).CompleteAsync(new(settings, "text")));
        Assert.Contains("超时", (await Error(new Handler((_, _) => throw new TaskCanceledException()))).Message);
        Assert.Contains("网络", (await Error(new Handler((_, _) => throw new HttpRequestException("sensitive-input")))).Message);
        Assert.Contains("格式", (await Error(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}"))))).Message);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenAiCompatibleTextProvider(new HttpClient(new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct))), new Vault(settings.CredentialKey))
            .CompleteAsync(new(settings, "text"), cancelled.Token));
    }

    [Fact]
    public async Task TemplateEditsOrderAndIndependentSwitchesSurviveSettingsRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "selection-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonAppSettingsStore(new AppPaths(root));
            var defaults = await store.LoadAsync();
            var templates = defaults.SelectionAi.Templates.Reverse().ToList();
            templates.RemoveAt(1);
            templates[0] = templates[0] with { Name = "用户改名", Body = "用户提示词：{text}" };
            templates.Insert(0, new PromptTemplate(Guid.NewGuid(), "新增", "{text}"));
            var ai = defaults.SelectionAi with { HotkeyEnabled = false, FloatingToolbarEnabled = true, Templates = templates };
            await store.SaveAsync(defaults with { SelectionAi = ai });
            var loaded = (await store.LoadAsync()).SelectionAi;
            Assert.Equal(templates, loaded.Templates);
            Assert.False(loaded.HotkeyEnabled); Assert.True(loaded.FloatingToolbarEnabled);
            Assert.DoesNotContain("test-key", await File.ReadAllTextAsync(new AppPaths(root).SettingsPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    private sealed class Vault(string expectedKey) : ICredentialVault
    {
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<string?>(key == expectedKey ? "test-key" : null); }
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
