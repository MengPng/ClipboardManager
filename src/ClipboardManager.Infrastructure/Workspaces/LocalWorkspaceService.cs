using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Workspaces;

/// <summary>
/// Exposes the two workspaces this build knows about, both stored only on this device.
/// The team workspace has no account, server or sync behind it; it is a second local
/// container so 团队配置 content is isolated from 个人配置 by the same repository rules
/// that will later separate a real team space. See
/// <c>docs/adr/0002-local-team-config-workspace.md</c>.
/// </summary>
public sealed class LocalWorkspaceService(IDeviceIdentityStore deviceIdentityStore) : IWorkspaceService
{
    public async Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var device = await deviceIdentityStore.GetOrCreateAsync(cancellationToken);
        return [CreateLocal(device), CreateTeam(device)];
    }

    /// <summary>
    /// The capture anchor. Clipboard capture, history and quick-collect all resolve their
    /// workspace from here, so this must keep returning the local workspace: AGENTS.md
    /// requires newly captured content to stay on this machine and never drift into a
    /// team space just because the user is looking at a different config view.
    /// </summary>
    public async Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        CreateLocal(await deviceIdentityStore.GetOrCreateAsync(cancellationToken));

    /// <summary>
    /// Validates that the id names a known workspace, but does not move the capture
    /// anchor. There is no ambient "selected workspace" in this build: config operations
    /// carry their target workspace explicitly, which is what keeps a UI space switch
    /// from redirecting the clipboard listener.
    /// </summary>
    public async Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var available = await GetAvailableAsync(cancellationToken);
        if (available.All(workspace => workspace.Id != workspaceId))
        {
            throw new InvalidOperationException("目标 Workspace 不存在。");
        }
    }

    private static Workspace CreateLocal(DeviceIdentity device) =>
        new(CreateWorkspaceId(device.DeviceId, 0x4C, 0x57), WorkspaceType.Local, "本机剪切板", null,
            device.CreatedAt, device.CreatedAt);

    private static Workspace CreateTeam(DeviceIdentity device) =>
        new(CreateWorkspaceId(device.DeviceId, 0x54, 0x4D), WorkspaceType.Team, "团队配置（仅本机）", null,
            device.CreatedAt, device.CreatedAt);

    // Workspace ids are derived from the stable device id rather than stored, so they
    // survive a settings reset and stay reproducible across restarts. Each workspace
    // needs its own salt or the two spaces would collide on one id.
    private static Guid CreateWorkspaceId(Guid deviceId, byte firstSalt, byte secondSalt)
    {
        var bytes = deviceId.ToByteArray();
        bytes[0] ^= firstSalt;
        bytes[1] ^= secondSalt;
        return new Guid(bytes);
    }
}
