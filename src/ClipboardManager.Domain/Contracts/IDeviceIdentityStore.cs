using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IDeviceIdentityStore
{
    Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default);
}
