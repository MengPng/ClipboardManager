using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Workspaces;

public sealed class LocalWorkspaceService(IDeviceIdentityStore deviceIdentityStore) : IWorkspaceService
{
    public async Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken = default) =>
        [await GetCurrentAsync(cancellationToken)];

    public async Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var device = await deviceIdentityStore.GetOrCreateAsync(cancellationToken);
        var createdAt = device.CreatedAt;
        return new Workspace(
            CreateWorkspaceId(device.DeviceId),
            WorkspaceType.Local,
            "本机剪切板",
            null,
            createdAt,
            createdAt);
    }

    public async Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (workspaceId != current.Id)
        {
            throw new InvalidOperationException("当前版本只支持本机 Workspace。");
        }
    }

    private static Guid CreateWorkspaceId(Guid deviceId)
    {
        var bytes = deviceId.ToByteArray();
        bytes[0] ^= 0x4C;
        bytes[1] ^= 0x57;
        return new Guid(bytes);
    }
}
