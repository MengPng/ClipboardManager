using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services.ConfigSync;

/// <summary>发布方对订阅方请求的业务回调：配对、指纹 pin 校验与只读的发布内容查询。</summary>
public sealed class ConfigSyncEndpoint(
    IConfigSyncRepository repository,
    IConfigPackageService packages,
    IConfigSyncIdentity identity,
    PairingSessions pairing,
    TimeProvider time) : IConfigSyncEndpoint
{
    public event EventHandler? PairingCompleted;

    public string DeviceName => identity.DeviceName;

    public Task<(byte[] ServerNonce, string DeviceName)> PairBeginAsync(string clientFingerprint, string clientName,
        byte[] clientNonce, CancellationToken cancellationToken = default) =>
        Task.FromResult((pairing.Begin(clientFingerprint, CleanName(clientName), clientNonce), identity.DeviceName));

    public async Task<(byte[] ServerProof, string DeviceName)> PairFinishAsync(string clientFingerprint, string serverFingerprint,
        byte[] clientProof, CancellationToken cancellationToken = default)
    {
        var (serverProof, clientName) = pairing.Finish(clientFingerprint, serverFingerprint, clientProof);
        await repository.SavePeerAsync(new PairedPeer(Guid.NewGuid(), PeerRole.Subscriber, clientName,
            clientFingerprint.ToUpperInvariant(), null, time.GetUtcNow(), PeerStatus.Active), cancellationToken);
        PairingCompleted?.Invoke(this, EventArgs.Empty);
        return (serverProof, identity.DeviceName);
    }

    public async Task<bool> IsPinnedAsync(string clientFingerprint, CancellationToken cancellationToken = default) =>
        await repository.FindActivePeerAsync(PeerRole.Subscriber, clientFingerprint, cancellationToken) is not null;

    public async Task<IReadOnlyList<PublicationInfo>> ListPublicationsAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetPublishedAsync(cancellationToken))
        .Select(config => new PublicationInfo(config.Id, config.Name, config.CurrentVersion, config.ContentHash))
        .ToList();

    public async Task<ConfigManifest?> GetManifestAsync(Guid publishedId, CancellationToken cancellationToken = default)
    {
        var config = await repository.GetPublishedAsync(publishedId, cancellationToken);
        return config is null ? null : ConfigSyncPolicy.ParseManifest(config.ManifestJson);
    }

    public async Task<string?> GetPackagePathAsync(Guid publishedId, int version, CancellationToken cancellationToken = default)
    {
        var config = await repository.GetPublishedAsync(publishedId, cancellationToken);
        // 只提供当前发布版本；旧版本不再对外提供，避免订阅方被引导回退。
        return config is null || config.CurrentVersion != version ? null : packages.ResolvePath(config.PackagePath);
    }

    private static string CleanName(string name)
    {
        var text = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length == 0) text = "未知设备";
        return text.Length > 80 ? text[..80] : text;
    }
}
