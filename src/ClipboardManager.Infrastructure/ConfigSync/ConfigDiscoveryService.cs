using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.ConfigSync;

public sealed record ConfigDiscoveryOptions(int UdpPort = ConfigSyncPolicy.DefaultPort, int AnnouncedTcpPort = ConfigSyncPolicy.DefaultPort);

/// <summary>组播广播消息的编码与解析。广播只含协议版本、设备名、发布名称与版本号、指纹前缀，不含任何内容。</summary>
public static class ConfigDiscoveryMessage
{
    public const int MaxDatagramBytes = 2048;
    private const int MaxPublications = 20;
    private const int MaxNameLength = 80;

    public static byte[] Encode(string deviceName, string fingerprint, int tcpPort, IReadOnlyList<PublicationAnnouncement> publications)
    {
        var message = new AnnounceMessage(
            ConfigSyncPolicy.ProtocolVersion, Clean(deviceName), ConfigSyncPolicy.FingerprintPrefix(fingerprint), tcpPort,
            publications.Take(MaxPublications).Select(item => new AnnounceItem(Clean(item.Name), item.Version)).ToList());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ConfigSyncPolicy.Json);
        // 超出单个数据报时只保留设备信息，宁可少广播也不分片。
        return bytes.Length <= MaxDatagramBytes
            ? bytes
            : JsonSerializer.SerializeToUtf8Bytes(message with { Pubs = [] }, ConfigSyncPolicy.Json);
    }

    public static bool TryDecode(ReadOnlySpan<byte> datagram, IPAddress sender, DateTimeOffset now, out DiscoveredPublisher publisher)
    {
        publisher = null!;
        if (datagram.Length == 0 || datagram.Length > MaxDatagramBytes) return false;
        AnnounceMessage? message;
        try { message = JsonSerializer.Deserialize<AnnounceMessage>(datagram, ConfigSyncPolicy.Json); }
        catch (JsonException) { return false; }
        if (message is null || message.Port is < 1 or > 65535) return false;
        var fingerprint = message.Fp ?? string.Empty;
        if (fingerprint.Length is < 6 or > 64 || !fingerprint.All(Uri.IsHexDigit)) return false;
        var address = sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender;
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var publications = (message.Pubs ?? [])
            .Where(item => item is not null && item.Version >= 1)
            .Take(MaxPublications)
            .Select(item => new PublicationAnnouncement(Clean(item.Name), item.Version))
            .ToList();
        publisher = new DiscoveredPublisher(
            Clean(message.Name), $"{host}:{message.Port}", fingerprint.ToUpperInvariant(), message.Proto, publications, now);
        return true;
    }

    private static string Clean(string? value)
    {
        var text = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
    }

    private sealed record AnnounceMessage(int Proto, string? Name, string? Fp, int Port, List<AnnounceItem>? Pubs);
    private sealed record AnnounceItem(string? Name, int Version);
}

/// <summary>
/// UDP 组播发现（239.255.53.18:53318）。仅在订阅页打开时监听、发布存在时广播；
/// 组播被网络屏蔽时不影响手动输入 IP:端口。
/// </summary>
public sealed class ConfigDiscoveryService(
    ConfigSyncIdentity identity,
    ILogger<ConfigDiscoveryService> logger,
    ConfigDiscoveryOptions? options = null) : IConfigDiscovery, IDisposable
{
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(20);

    private readonly ConfigDiscoveryOptions discoveryOptions = options ?? new ConfigDiscoveryOptions();
    private readonly object sync = new();
    private readonly Dictionary<string, DiscoveredPublisher> seen = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PublicationAnnouncement> announcement = [];
    private bool listening;
    private bool announcing;
    private UdpClient? socket;
    private CancellationTokenSource? cts;
    private bool disposed;

    public event EventHandler? Changed;

    public IReadOnlyList<DiscoveredPublisher> GetPublishers()
    {
        lock (sync)
        {
            Prune(DateTimeOffset.UtcNow);
            return seen.Values.OrderBy(item => item.DeviceName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public void StartListening()
    {
        lock (sync)
        {
            if (disposed || listening) return;
            listening = true;
            EnsureRunning();
        }
    }

    public void StopListening()
    {
        var changed = false;
        lock (sync)
        {
            if (!listening) return;
            listening = false;
            changed = seen.Count > 0;
            seen.Clear();
            ReleaseIfIdle();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAnnouncement(IReadOnlyList<PublicationAnnouncement> publications)
    {
        lock (sync)
        {
            if (disposed) return;
            announcement = publications.ToList();
            announcing = announcement.Count > 0;
            if (announcing) EnsureRunning();
            else ReleaseIfIdle();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            listening = false;
            announcing = false;
            ReleaseIfIdle();
        }
    }

    private void EnsureRunning()
    {
        if (socket is not null) return;
        try
        {
            var created = new UdpClient(AddressFamily.InterNetwork);
            created.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            created.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryOptions.UdpPort));
            created.MulticastLoopback = true;
            var group = IPAddress.Parse(ConfigSyncPolicy.MulticastGroup);
            foreach (var local in LocalAddresses())
            {
                try { created.JoinMulticastGroup(group, local); }
                catch (SocketException ex) { logger.LogDebug("加入组播组失败（{Address}）：{Code}", local, ex.SocketErrorCode); }
            }
            socket = created;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            _ = Task.Run(() => ReceiveLoopAsync(created, token));
            _ = Task.Run(() => AnnounceLoopAsync(created, group, token));
        }
        catch (SocketException ex)
        {
            // 端口被占用或系统禁用组播：发现不可用，但手动 IP 仍可使用。
            logger.LogWarning("组播发现无法启动：{Code}", ex.SocketErrorCode);
            socket = null;
        }
    }

    private void ReleaseIfIdle()
    {
        if (listening || announcing || socket is null) return;
        cts?.Cancel();
        socket.Dispose();
        cts?.Dispose();
        socket = null;
        cts = null;
    }

    private static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) result.Add(unicast.Address);
        }
        return result;
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken token)
    {
        var ownPrefix = ConfigSyncPolicy.FingerprintPrefix(identity.Fingerprint);
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await client.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested) return;
                logger.LogDebug("组播接收出错：{Code}", ex.SocketErrorCode);
                await Task.Delay(500, token).ContinueWith(_ => { }, CancellationToken.None);
                continue;
            }

            if (!ConfigDiscoveryMessage.TryDecode(result.Buffer, result.RemoteEndPoint.Address, DateTimeOffset.UtcNow, out var publisher)) continue;
            if (string.Equals(publisher.FingerprintPrefix, ownPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            bool changed;
            lock (sync)
            {
                if (!listening) continue;
                var key = $"{publisher.FingerprintPrefix}@{publisher.Address}";
                changed = !seen.TryGetValue(key, out var previous) || !SameContent(previous, publisher);
                seen[key] = publisher;
                changed |= Prune(DateTimeOffset.UtcNow);
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AnnounceLoopAsync(UdpClient client, IPAddress group, CancellationToken token)
    {
        var endpoint = new IPEndPoint(group, discoveryOptions.UdpPort);
        using var timer = new PeriodicTimer(AnnounceInterval);
        do
        {
            try
            {
                IReadOnlyList<PublicationAnnouncement> current;
                bool shouldAnnounce, pruned;
                lock (sync)
                {
                    current = announcement;
                    shouldAnnounce = announcing;
                    pruned = Prune(DateTimeOffset.UtcNow);
                }
                if (pruned) Changed?.Invoke(this, EventArgs.Empty);
                if (shouldAnnounce)
                {
                    var payload = ConfigDiscoveryMessage.Encode(identity.DeviceName, identity.Fingerprint, discoveryOptions.AnnouncedTcpPort, current);
                    foreach (var local in LocalAddresses())
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                        await client.SendAsync(payload, endpoint, token);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                logger.LogDebug("组播广播失败：{Code}", ex.SocketErrorCode);
            }
        }
        while (await WaitAsync(timer, token));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }

    private bool Prune(DateTimeOffset now)
    {
        var stale = seen.Where(pair => now - pair.Value.LastSeen > Expiry).Select(pair => pair.Key).ToList();
        foreach (var key in stale) seen.Remove(key);
        return stale.Count > 0;
    }

    private static bool SameContent(DiscoveredPublisher a, DiscoveredPublisher b) =>
        a.DeviceName == b.DeviceName && a.ProtocolVersion == b.ProtocolVersion &&
        a.Publications.SequenceEqual(b.Publications);
}
