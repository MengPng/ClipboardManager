using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_CreatesDefaultsAndPersistsChanges()
    {
        var store = new JsonAppSettingsStore(new AppPaths(_root));

        var defaults = await store.LoadAsync();
        Assert.Equal(1000, defaults.MaxHistoryItems);
        Assert.Equal("Alt+V", defaults.GlobalHotkey);

        var changed = defaults with { MaxHistoryItems = 250, EnableAutomaticPaste = true };
        await store.SaveAsync(changed);

        var reloaded = await new JsonAppSettingsStore(new AppPaths(_root)).LoadAsync();
        Assert.Equal(250, reloaded.MaxHistoryItems);
        Assert.True(reloaded.EnableAutomaticPaste);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
