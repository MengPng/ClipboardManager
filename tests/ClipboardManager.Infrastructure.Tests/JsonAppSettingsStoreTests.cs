using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClipboardCalculatorMode_DefaultsToHint_AndPersistsAcrossRestart()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        var original = await store.LoadAsync();
        Assert.Equal(ClipboardCalculatorMode.Hint, original.ClipboardCalculatorMode);
        Assert.False(original.CalculatorKeypadOpen);
        foreach (var mode in new[] { ClipboardCalculatorMode.Rewrite, ClipboardCalculatorMode.Off, ClipboardCalculatorMode.Hint })
        {
            await store.SaveAsync(original with { ClipboardCalculatorMode = mode, CalculatorKeypadOpen = mode == ClipboardCalculatorMode.Off });
            var reloaded = await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync();
            Assert.Equal(mode, reloaded.ClipboardCalculatorMode);
            Assert.Equal(mode == ClipboardCalculatorMode.Off, reloaded.CalculatorKeypadOpen);
        }
        Assert.Contains("\"Hint\"", await File.ReadAllTextAsync(new AppPaths(_root).SettingsPath));
    }

    [Theory]
    [InlineData("true", ClipboardCalculatorMode.Rewrite)]   // users who switched it on keep the rewrite behaviour
    [InlineData("false", ClipboardCalculatorMode.Off)]      // users who explicitly had it off stay off
    public async Task LegacyEnableClipboardCalculator_MigratesToMode_AndIsDroppedOnNextSave(string legacy, ClipboardCalculatorMode expected)
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, $$"""{ "retentionDays": 12, "enableClipboardCalculator": {{legacy}} }""");
        var store = new JsonAppSettingsStore(paths);

        var loaded = await store.LoadAsync();

        Assert.Equal(expected, loaded.ClipboardCalculatorMode);
        Assert.Equal(12, loaded.RetentionDays);
        await store.SaveAsync(loaded);
        var rewritten = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("enableClipboardCalculator", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, (await new JsonAppSettingsStore(paths).LoadAsync()).ClipboardCalculatorMode);
    }

    [Fact]
    public async Task LegacyFlag_NeverOverridesAnExplicitMode()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """{ "enableClipboardCalculator": true, "clipboardCalculatorMode": "Hint" }""");
        Assert.Equal(ClipboardCalculatorMode.Hint, (await new JsonAppSettingsStore(paths).LoadAsync()).ClipboardCalculatorMode);
    }

    [Fact]
    public async Task SettingsWithoutCalculatorFields_LoadWithHint_AndInvalidModeIsRejected()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """{ "retentionDays": 30 }""");
        var store = new JsonAppSettingsStore(paths);
        Assert.Equal(ClipboardCalculatorMode.Hint, (await store.LoadAsync()).ClipboardCalculatorMode);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppSettings { ClipboardCalculatorMode = (ClipboardCalculatorMode)99 }));
    }

    [Fact]
    public async Task InvalidSave_PreservesPreviousSettings()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        await store.SaveAsync(new AppSettings { RetentionDays = 12 });
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppSettings { RetentionDays = 0 }));
        Assert.Equal(12, (await store.LoadAsync()).RetentionDays);
    }

    [Fact]
    public async Task ConcurrentSaveAndLoad_AlwaysReturnsWholeDocument()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        await store.LoadAsync();
        await Task.WhenAll(Enumerable.Range(1, 20).Select(async n =>
        {
            await store.SaveAsync(new AppSettings { RetentionDays = n });
            Assert.InRange((await store.LoadAsync()).RetentionDays, 1, 20);
        }));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        var reopened = new JsonAppSettingsStore(new AppPaths(_root));
        Assert.InRange((await reopened.LoadAsync()).RetentionDays, 1, 20);
    }

    [Fact]
    public async Task TimedPause_RoundTripsAcrossRestart()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(30);
        await new JsonAppSettingsStore(new AppPaths(_root)).SaveAsync(new AppSettings { CapturePausedUntil = until });
        Assert.Equal(until, (await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync()).CapturePausedUntil);
    }

    [Fact]
    public async Task Theme_DefaultsToStandard_AndRoundTripsAcrossRestart()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        var original = await store.LoadAsync();
        Assert.Equal("standard", original.Theme);
        await store.SaveAsync(original with { Theme = "jiajia" });
        Assert.Equal("jiajia", (await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync()).Theme);
        await store.SaveAsync(original with { Theme = "kami" });
        Assert.Equal("kami", (await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync()).Theme);
    }

    [Fact]
    public async Task InvalidTheme_IsRejected_AndPreservesPreviousTheme()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        await store.SaveAsync(new AppSettings { Theme = "jiajia" });
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppSettings { Theme = "neon" }));
        Assert.Equal("jiajia", (await store.LoadAsync()).Theme);
    }

    [Fact]
    public async Task LegacySettingsWithSelectionAi_LoadWithoutError_IgnoreTheField_AndDropItOnNextSave()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        // Shape written by 0.7.9–0.8.7, where the removed 划词 feature kept its configuration under "selectionAi".
        await File.WriteAllTextAsync(paths.SettingsPath, """
            {
              "retentionDays": 12,
              "theme": "kami",
              "globalHotkey": "Alt+B",
              "excludedProcesses": ["KeePass", "Secret"],
              "selectionAi": {
                "enabled": true,
                "hotkeyEnabled": true,
                "hotkey": "Alt+Q",
                "excludedProcesses": ["Other"],
                "templates": [{ "id": "11111111-1111-1111-1111-111111111111", "name": "翻译", "body": "{text}" }],
                "futureField": { "nested": [1, 2, 3] }
              }
            }
            """);
        var store = new JsonAppSettingsStore(paths);

        var loaded = await store.LoadAsync();

        Assert.Equal(12, loaded.RetentionDays);
        Assert.Equal("kami", loaded.Theme);
        Assert.Equal("Alt+B", loaded.GlobalHotkey);
        Assert.Equal(["KeePass", "Secret"], loaded.ExcludedProcesses);

        await store.SaveAsync(loaded);
        var rewritten = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("selectionAi", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, (await new JsonAppSettingsStore(paths).LoadAsync()).RetentionDays);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
