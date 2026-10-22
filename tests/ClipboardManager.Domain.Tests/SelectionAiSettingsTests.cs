using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Tests;

public sealed class SelectionAiSettingsTests
{
    [Theory]
    [InlineData("https://user@example.test")]
    [InlineData("https://example.test?key=secret")]
    [InlineData("https://example.test#token")]
    public void RejectsUnsafeEndpoint(string endpoint) =>
        Assert.Throws<ArgumentException>(() => SelectionAiSettings.Validate(new() { BaseUrl = endpoint }));

    [Theory]
    [InlineData("http://127.0.0.1:8317/v1")]
    [InlineData("http://localhost:4000/v1")]
    [InlineData("http://[::1]:1234/v1")]
    public void LocalGatewayAcceptsOnlyLoopbackHttp(string endpoint) =>
        SelectionAiSettings.Validate(new() { Provider = "LocalGateway", BaseUrl = endpoint, Model = "local-model" });

    [Theory]
    [InlineData("http://192.168.1.2:8317/v1")]
    [InlineData("http://example.test/v1")]
    [InlineData("https://example.test/v1")]
    public void LocalGatewayRejectsNonLoopbackEndpoint(string endpoint) =>
        Assert.Throws<ArgumentException>(() =>
            SelectionAiSettings.Validate(new() { Provider = "LocalGateway", BaseUrl = endpoint, Model = "local-model" }));

    [Fact]
    public void RemoteCompatibilityModeStillRequiresHttps() =>
        Assert.Throws<ArgumentException>(() =>
            SelectionAiSettings.Validate(new() { Provider = "OpenAICompatible", BaseUrl = "http://127.0.0.1:8317/v1" }));

    [Fact]
    public void CredentialsAreEndpointScopedAndTemplatesMustHaveDistinctIds()
    {
        var config = new SelectionAiSettings();
        Assert.NotEqual(config.CredentialKey, (config with { BaseUrl = "https://other.invalid" }).CredentialKey);
        Assert.Equal(config.CredentialKey, (config with { BaseUrl = config.BaseUrl + "/" }).CredentialKey);
        Assert.Throws<ArgumentException>(() => SelectionAiSettings.Validate(config with { Templates = [config.Templates[0], config.Templates[0]] }));
        SelectionAiSettings.Validate(config with { Templates = [] });
    }

    [Fact]
    public void WebsiteActionRequiresPlainHttpAddress()
    {
        var config = new SelectionAiSettings();
        var website = new PromptTemplate(Guid.NewGuid(), "搜索", "{text}", PromptActions.OpenUrl, "https://example.test");
        SelectionAiSettings.Validate(config with { Templates = [website] });
        Assert.Throws<ArgumentException>(() => SelectionAiSettings.Validate(
            config with { Templates = [website with { Url = "file:///C:/secret.txt" }] }));
        Assert.Throws<ArgumentException>(() => SelectionAiSettings.Validate(
            config with { Templates = [website with { Url = "https://user@example.test" }] }));
    }
}
