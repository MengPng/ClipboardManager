using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Tests;

public sealed class AppSettingsValidatorTests
{
    [Fact]
    public void ValidSettings_AreAccepted() => AppSettingsValidator.Validate(new AppSettings());

    [Theory]
    [InlineData(0, 30)]
    [InlineData(100001, 30)]
    [InlineData(1000, 0)]
    [InlineData(1000, 3651)]
    public void InvalidHistoryLimits_AreRejected(int items, int days) =>
        Assert.Throws<ArgumentException>(() => AppSettingsValidator.Validate(new AppSettings { MaxHistoryItems = items, RetentionDays = days }));

    [Fact]
    public void CacheMustFitOneImage() =>
        Assert.Throws<ArgumentException>(() => AppSettingsValidator.Validate(new AppSettings { MaxPersistedImageBytes = 20, MaxImageCacheBytes = 19 }));

    [Fact]
    public void HttpCloudEndpoint_IsRejected() =>
        Assert.Throws<ArgumentException>(() => AppSettingsValidator.Validate(new AppSettings { CloudApiBaseUrl = "http://example.test" }));

    [Fact]
    public void DefaultTheme_IsStandard() => Assert.Equal("standard", new AppSettings().Theme);

    [Theory]
    [InlineData("standard")]
    [InlineData("jiajia")]
    [InlineData("kami")]
    public void KnownThemes_AreAccepted(string theme) => AppSettingsValidator.Validate(new AppSettings { Theme = theme });

    [Theory]
    [InlineData("")]
    [InlineData("Jiajia")]
    [InlineData("Kami")]
    [InlineData("neon")]
    [InlineData(null)]
    public void UnknownTheme_IsRejected(string? theme) =>
        Assert.Throws<ArgumentException>(() => AppSettingsValidator.Validate(new AppSettings { Theme = theme! }));

    [Fact]
    public void HttpsCloudEndpoint_IsAccepted() =>
        AppSettingsValidator.Validate(new AppSettings { CloudApiBaseUrl = "https://cloud.example.test" });
}
