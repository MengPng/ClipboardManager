using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IIdentityProvider
{
    Task<IdentitySession?> GetSessionAsync(CancellationToken cancellationToken = default);

    Task<IdentitySession> SignInAsync(
        SignInRequest request,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
