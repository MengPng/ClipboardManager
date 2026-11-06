using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Application.Services.LanTransfer;

/// <summary>
/// 本机固定匹配码与“已匹配的设备”（ADR-0008）。同一台设备在仓储里可能有发布者、订阅者两条记录，
/// 这里按证书指纹合并为一台设备；重命名只改本机显示；地址随组播广播自动更新（连接仍校验完整指纹）。
/// </summary>
public sealed class LanDeviceService(
    IConfigSyncRepository repository,
    IConfigDiscovery discovery,
    IConfigSyncIdentity identity,
    IConfigSyncServer server,
    PairingSessions pairing,
    ConfigSubscriptionService subscriptions,
    ILogger<LanDeviceService> logger)
{
    private readonly SemaphoreSlim codeLock = new(1, 1);

    /// <summary>匹配码或设备列表变化。</summary>
    public event EventHandler? Changed;

    public MatchCodeStatus MatchCode => pairing.Status;

    /// <summary>从凭据管理器载入本机匹配码（首次使用时生成）。</summary>
    public async Task<MatchCodeStatus> LoadMatchCodeAsync(CancellationToken cancellationToken = default)
    {
        await codeLock.WaitAsync(cancellationToken);
        try
        {
            var (code, disabled) = await identity.GetMatchCodeAsync(cancellationToken);
            if (pairing.Status.Code != code || pairing.Status.Disabled != disabled) pairing.SetCode(code, disabled);
        }
        finally
        {
            codeLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return pairing.Status;
    }

    /// <summary>更换匹配码：旧码立即失效，锁定与停用解除；已匹配的设备不受影响。</summary>
    public async Task<string> RegenerateMatchCodeAsync(CancellationToken cancellationToken = default)
    {
        string code;
        await codeLock.WaitAsync(cancellationToken);
        try
        {
            code = await identity.RegenerateMatchCodeAsync(cancellationToken);
            pairing.SetCode(code);
        }
        finally
        {
            codeLock.Release();
        }
        logger.LogInformation("本机匹配码已更换");
        Changed?.Invoke(this, EventArgs.Empty);
        return code;
    }

    /// <summary>累计输错过多：把停用状态写入凭据管理器，重启后仍然停用。</summary>
    public async Task PersistDisabledAsync(CancellationToken cancellationToken = default)
    {
        await codeLock.WaitAsync(cancellationToken);
        try
        {
            if (pairing.Status.Disabled) await identity.DisableMatchCodeAsync(cancellationToken);
        }
        finally
        {
            codeLock.Release();
        }
        logger.LogWarning("匹配码因累计输错过多已停用");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<MatchedDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var peers = (await repository.GetPeersAsync(PeerRole.Publisher, cancellationToken))
            .Concat(await repository.GetPeersAsync(PeerRole.Subscriber, cancellationToken))
            .Where(peer => peer.Status == PeerStatus.Active)
            .ToList();
        var online = OnlinePrefixes();
        return peers
            .GroupBy(peer => peer.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var publisher = group.FirstOrDefault(peer => peer.Role == PeerRole.Publisher);
                var primary = publisher ?? group.First();
                var address = publisher?.LastAddress ?? group.Select(peer => peer.LastAddress).FirstOrDefault(value => value is not null);
                return new MatchedDevice(
                    primary.Fingerprint,
                    primary.Name,
                    address,
                    group.Min(peer => peer.PairedAt),
                    publisher is not null,
                    online.Contains(ConfigSyncPolicy.FingerprintPrefix(primary.Fingerprint)),
                    group.ToList());
            })
            .OrderByDescending(device => device.IsOnline)
            .ThenBy(device => device.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>重命名一台已匹配设备（只影响本机显示；再次匹配时保留）。</summary>
    public async Task RenameAsync(string fingerprint, string name, CancellationToken cancellationToken = default)
    {
        var cleaned = LanMatchPolicy.CleanDeviceName(name);
        if (cleaned.Length == 0) throw new ArgumentException("名称不能为空。", nameof(name));
        await repository.RenamePeersAsync(fingerprint.ToUpperInvariant(), cleaned, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>移除匹配：两个方向的记录一起撤销；还有订阅时拒绝。</summary>
    public async Task RemoveAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        var normalized = fingerprint.ToUpperInvariant();
        var publisher = await repository.FindActivePeerAsync(PeerRole.Publisher, normalized, cancellationToken);
        if (publisher is not null &&
            (await repository.GetSubscriptionsAsync(cancellationToken)).Any(subscription => subscription.PeerId == publisher.Id))
            throw new ConfigSyncException(ConfigSyncError.Local, "这台设备下还有订阅的配置。请先在「订阅」取消这些订阅，再移除匹配。");
        if (publisher is not null) await repository.RevokePeerAsync(publisher.Id, cancellationToken);
        var subscriber = await repository.FindActivePeerAsync(PeerRole.Subscriber, normalized, cancellationToken);
        if (subscriber is not null) await repository.RevokePeerAsync(subscriber.Id, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>输入对方的匹配码完成双向匹配；本机监听端口一并告诉对方，便于对方反向发文件。</summary>
    public async Task<PairedPeer> MatchAsync(string address, string code, CancellationToken cancellationToken = default)
    {
        int? port = null;
        try
        {
            await server.StartAsync(cancellationToken);
            if (server.Status.IsRunning) port = server.Status.Port;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("匹配前启动本机监听失败：{Type}", ex.GetType().Name);
        }
        var peer = await subscriptions.PairAsync(address, code, port, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return peer;
    }

    /// <summary>根据组播广播更新已匹配设备的地址（DHCP 换 IP 后自动跟上）。返回更新的记录数。</summary>
    public async Task<int> SyncAddressesFromDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        var announced = discovery.GetPublishers()
            .GroupBy(item => item.FingerprintPrefix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastSeen).First().Address,
                StringComparer.OrdinalIgnoreCase);
        if (announced.Count == 0) return 0;
        var updated = 0;
        foreach (var role in new[] { PeerRole.Publisher, PeerRole.Subscriber })
        foreach (var peer in await repository.GetPeersAsync(role, cancellationToken))
        {
            if (peer.Status != PeerStatus.Active) continue;
            if (!announced.TryGetValue(ConfigSyncPolicy.FingerprintPrefix(peer.Fingerprint), out var address)) continue;
            if (string.Equals(address, peer.LastAddress, StringComparison.OrdinalIgnoreCase)) continue;
            await repository.UpdatePeerAddressAsync(peer.Id, address, cancellationToken);
            updated++;
        }
        if (updated > 0) Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>发现服务只保留最近 <see cref="LanMatchPolicy.OnlineWindow"/> 内收到广播的设备，列表中即视为在线。</summary>
    private HashSet<string> OnlinePrefixes() =>
        discovery.GetPublishers()
            .Select(item => item.FingerprintPrefix)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
