using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IWorkspaceService
{
    Task<IReadOnlyList<Workspace>> GetAvailableAsync(CancellationToken cancellationToken = default);

    Task<Workspace> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task SwitchAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
