using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClipboardCalculator_DefaultsOff_AndPersistsAcrossRestart()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));
        var original = await store.LoadAsync();
        Assert.False(original.EnableClipboardCalculator);
        await store.SaveAsync(original with { EnableClipboardCalculator = true });
        Assert.True((await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync()).EnableClipboardCalculator);
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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
