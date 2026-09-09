using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipboardManager.Infrastructure.Lan;

public sealed class LocalSendLanShareService(AppPaths paths, IAppSettingsStore settingsStore) : ILanShareService
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.167");
    private readonly string _fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async Task<IReadOnlyList<LanShareDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var port = settings.LanPort;
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        udp.JoinMulticastGroup(MulticastAddress);
        var announcement = JsonSerializer.SerializeToUtf8Bytes(new
        {
            alias = settings.LanDeviceAlias, version = "2.0", deviceModel = "Windows",
            deviceType = "desktop", fingerprint = _fingerprint, port,
            protocol = "https", download = false, announce = true
        });
        await udp.SendAsync(announcement, new IPEndPoint(MulticastAddress, port), cancellationToken);

        var devices = new Dictionary<string, LanShareDevice>(StringComparer.OrdinalIgnoreCase);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.LanDiscoverySeconds));
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                using var json = JsonDocument.Parse(packet.Buffer);
                var root = json.RootElement;
                var fingerprint = Text(root, "fingerprint");
                if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint == _fingerprint) continue;
                var alias = Text(root, "alias") ?? packet.RemoteEndPoint.Address.ToString();
                var protocol = Text(root, "protocol") ?? "https";
                var remotePort = root.TryGetProperty("port", out var portElement) && portElement.TryGetInt32(out var value) ? value : 53317;
                devices[fingerprint] = new(alias, packet.RemoteEndPoint.Address.ToString(), remotePort, protocol, fingerprint, Text(root, "deviceType"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return devices.Values.OrderBy(x => x.Alias, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<LanShareResult> SendAsync(LanShareDevice device, ClipboardItem item, string? pin = null,
        IProgress<LanShareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var sources = await MaterializeAsync(item, cancellationToken);
        try
        {
            using var client = CreateClient(device);
            var files = sources.ToDictionary(x => x.Id, x => new
            {
                id = x.Id, fileName = x.Name, size = x.Length, fileType = x.ContentType,
                sha256 = x.Hash, preview = (string?)null
            });
            var request = new
            {
                info = new { alias = settings.LanDeviceAlias, version = "2.0", deviceModel = "Windows", deviceType = "desktop", fingerprint = _fingerprint, port = settings.LanPort, protocol = "https", download = false },
                files
            };
            progress?.Report(new("等待对方确认", 0, sources.Sum(x => x.Length)));
            var preparePath = "/api/localsend/v2/prepare-upload" + (string.IsNullOrWhiteSpace(pin) ? "" : $"?pin={Uri.EscapeDataString(pin.Trim())}");
            using var prepare = await client.PostAsJsonAsync(preparePath, request, cancellationToken);
            prepare.EnsureSuccessStatusCode();
            var response = await prepare.Content.ReadFromJsonAsync<PrepareResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("接收方返回了空响应。");
            long sent = 0;
            foreach (var source in sources)
            {
                if (!response.Files.TryGetValue(source.Id, out var token)) continue;
                await using var stream = File.OpenRead(source.Path);
                using var content = new StreamContent(stream);
                content.Headers.ContentType = new(source.ContentType);
                using var uploaded = await client.PostAsync($"/api/localsend/v2/upload?sessionId={Uri.EscapeDataString(response.SessionId)}&fileId={Uri.EscapeDataString(source.Id)}&token={Uri.EscapeDataString(token)}", content, cancellationToken);
                uploaded.EnsureSuccessStatusCode();
                sent += source.Length;
                progress?.Report(new("正在发送", sent, sources.Sum(x => x.Length)));
            }
            return new(device.Alias, sources.Count, sent);
        }
        finally
        {
            foreach (var source in sources.Where(x => x.Temporary)) try { File.Delete(source.Path); } catch { }
        }
    }

    private static HttpClient CreateClient(LanShareDevice device)
    {
        var handler = new HttpClientHandler();
        if (device.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(device.Fingerprint, StringComparison.OrdinalIgnoreCase);
        return new(handler) { BaseAddress = new Uri(device.Endpoint), Timeout = TimeSpan.FromMinutes(10) };
    }

    private async Task<List<ShareSource>> MaterializeAsync(ClipboardItem item, CancellationToken cancellationToken)
    {
        if (item.ItemType == ClipboardItemType.Text)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"ClipboardManager-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(temp, item.TextContent ?? string.Empty, new UTF8Encoding(false), cancellationToken);
            return [await SourceAsync(temp, "剪切板文本.txt", "text/plain", true, cancellationToken)];
        }
        if (item.ItemType == ClipboardItemType.Image && !string.IsNullOrWhiteSpace(item.ImagePath))
        {
            var path = Path.IsPathRooted(item.ImagePath) ? item.ImagePath : Path.Combine(paths.Root, item.ImagePath);
            return [await SourceAsync(path, Path.GetFileName(path), "image/png", false, cancellationToken)];
        }
        var filePaths = JsonSerializer.Deserialize<string[]>(item.FileManifest ?? "[]") ?? [];
        var result = new List<ShareSource>();
        foreach (var file in filePaths)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("待发送文件不存在。", file);
            result.Add(await SourceAsync(file, Path.GetFileName(file), Mime(file), false, cancellationToken));
        }
        if (result.Count == 0) throw new InvalidOperationException("该记录没有可发送的内容。");
        return result;
    }

    private static async Task<ShareSource> SourceAsync(string path, string name, string contentType, bool temporary, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return new(Guid.NewGuid().ToString("N"), path, name, contentType, stream.Length, hash, temporary);
    }
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".pdf" => "application/pdf", ".txt" => "text/plain", _ => "application/octet-stream" };
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
    private sealed record ShareSource(string Id, string Path, string Name, string ContentType, long Length, string Hash, bool Temporary);
    private sealed record PrepareResponse(string SessionId, Dictionary<string, string> Files);
}
