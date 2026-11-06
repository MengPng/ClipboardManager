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
        byte[] clientNonce, int? clientPort = null, CancellationToken cancellationToken = default) =>
        Task.FromResult((pairing.Begin(clientFingerprint, CleanName(clientName), clientNonce, clientPort), identity.DeviceName));

    /// <summary>
    /// 匹配成功后双向记录对方（ADR-0008）：既是订阅者（可拉取本机发布），也是发布者（本机可浏览它、可互发文件）。
    /// 本机已经给它起过名字时保留本地名字。
    /// </summary>
    public async Task<(byte[] ServerProof, string DeviceName)> PairFinishAsync(string clientFingerprint, string serverFingerprint,
        byte[] clientProof, string? clientHost = null, CancellationToken cancellationToken = default)
    {
        var accepted = pairing.Finish(clientFingerprint, serverFingerprint, clientProof);
        var fingerprint = clientFingerprint.ToUpperInvariant();
        var name = await LocalNameAsync(repository, fingerprint, accepted.ClientName, cancellationToken);
        var address = clientHost is { Length: > 0 } && accepted.ClientPort is { } port ? FormatAddress(clientHost, port) : null;
        var now = time.GetUtcNow();
        await repository.SavePeerAsync(new PairedPeer(Guid.NewGuid(), PeerRole.Subscriber, name, fingerprint, address, now, PeerStatus.Active),
            cancellationToken);
        await repository.SavePeerAsync(new PairedPeer(Guid.NewGuid(), PeerRole.Publisher, name, fingerprint, address, now, PeerStatus.Active),
            cancellationToken);
        PairingCompleted?.Invoke(this, EventArgs.Empty);
        return (accepted.ServerProof, identity.DeviceName);
    }

    /// <summary>同一指纹已有本地名字（可能被用户改过）时沿用，否则用对方报的设备名。</summary>
    internal static async Task<string> LocalNameAsync(IConfigSyncRepository repository, string fingerprint, string reported,
        CancellationToken cancellationToken)
    {
        foreach (var role in new[] { PeerRole.Publisher, PeerRole.Subscriber })
            if (await repository.FindActivePeerAsync(role, fingerprint, cancellationToken) is { } existing)
                return existing.Name;
        return reported;
    }

    internal static string FormatAddress(string host, int port) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

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
