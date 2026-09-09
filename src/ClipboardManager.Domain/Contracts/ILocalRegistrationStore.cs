using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ILocalRegistrationStore
{
    Task EnsureRegisteredAsync(
        IdentitySession identity,
        DeviceIdentity device,
        IReadOnlyList<Workspace> workspaces,
        CancellationToken cancellationToken = default);
}
