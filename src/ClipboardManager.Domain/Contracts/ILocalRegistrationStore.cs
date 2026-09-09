using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ILocalRegistrationStore
{
    Task EnsureRegisteredAsync(
        IdentitySession identity,
        DeviceIdentity device,
        Workspace workspace,
        CancellationToken cancellationToken = default);
}
