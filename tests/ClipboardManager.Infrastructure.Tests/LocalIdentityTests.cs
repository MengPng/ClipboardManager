using ClipboardManager.Infrastructure.Identity;
using ClipboardManager.Infrastructure.Paths;
using ClipboardManager.Infrastructure.Workspaces;

namespace ClipboardManager.Infrastructure.Tests;

public sealed class LocalIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClipboardManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeviceAndWorkspaceIdentifiersRemainStable()
    {
        var paths = new AppPaths(_root);
        var firstStore = new JsonDeviceIdentityStore(paths);
        var firstDevice = await firstStore.GetOrCreateAsync();
        var firstWorkspace = await new LocalWorkspaceService(firstStore).GetCurrentAsync();

        var secondStore = new JsonDeviceIdentityStore(new AppPaths(_root));
        var secondDevice = await secondStore.GetOrCreateAsync();
        var secondWorkspace = await new LocalWorkspaceService(secondStore).GetCurrentAsync();

        Assert.Equal(firstDevice.DeviceId, secondDevice.DeviceId);
        Assert.Equal(firstWorkspace.Id, secondWorkspace.Id);
        Assert.Equal(firstWorkspace.Name, secondWorkspace.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
