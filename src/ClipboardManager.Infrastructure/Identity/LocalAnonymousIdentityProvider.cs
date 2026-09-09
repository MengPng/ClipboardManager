using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Identity;

public sealed class LocalAnonymousIdentityProvider(IDeviceIdentityStore deviceIdentityStore) : IIdentityProvider
{
    public async Task<IdentitySession?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var device = await deviceIdentityStore.GetOrCreateAsync(cancellationToken);
        return CreateSession(device);
    }

    public Task<IdentitySession> SignInAsync(
        SignInRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前版本仅支持本地匿名身份，尚未配置远端登录 Provider。");

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    private static IdentitySession CreateSession(DeviceIdentity device)
    {
        var source = Encoding.UTF8.GetBytes($"local:{device.DeviceId:D}");
        var hash = SHA256.HashData(source);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        var identityId = new Guid(guidBytes);
        return new IdentitySession(identityId, "local", device.DeviceId.ToString("D"), "本机用户", true);
    }
}
