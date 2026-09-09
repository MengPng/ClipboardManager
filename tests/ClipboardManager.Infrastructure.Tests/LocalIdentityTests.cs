using ClipboardManager.Domain.Models;
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

    /// <summary>
    /// The two config spaces are derived from the same device id with different salts, so
    /// they must be distinct, stable across restarts, and the current workspace must stay
    /// the local one — capture is anchored to it and must not follow a UI space switch.
    /// </summary>
    [Fact]
    public async Task LocalAndTeamWorkspaces_AreDistinctStableAndDoNotMoveTheCaptureAnchor()
    {
        var paths = new AppPaths(_root);
        var service = new LocalWorkspaceService(new JsonDeviceIdentityStore(paths));
        var available = await service.GetAvailableAsync();
        var local = Assert.Single(available, x => x.Type == WorkspaceType.Local);
        var team = Assert.Single(available, x => x.Type == WorkspaceType.Team);
        Assert.NotEqual(local.Id, team.Id);
        Assert.Equal(local.Id, (await service.GetCurrentAsync()).Id);

        var reopened = await new LocalWorkspaceService(new JsonDeviceIdentityStore(new AppPaths(_root))).GetAvailableAsync();
        Assert.Equal(available.Select(x => x.Id), reopened.Select(x => x.Id));

        // Selecting the team space is legal, but the capture anchor does not move.
        await service.SwitchAsync(team.Id);
        Assert.Equal(local.Id, (await service.GetCurrentAsync()).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync(Guid.NewGuid()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
