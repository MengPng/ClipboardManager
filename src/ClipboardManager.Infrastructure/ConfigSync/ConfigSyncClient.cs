using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.ConfigSync;

/// <summary>
/// 订阅方的 HTTPS 客户端：每个请求新建连接并按配对时保存的指纹 pin 校验发布方证书；
/// 所有失败都转换为带中文原因的 <see cref="ConfigSyncException"/> / <see cref="PairingException"/>。
/// </summary>
public sealed class ConfigSyncClient(ConfigSyncIdentity identity, ILogger<ConfigSyncClient> logger) : IConfigSyncClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<HelloInfo> HelloAsync(string address, CancellationToken cancellationToken = default)
    {
        var pin = new PinState(allowFirstUse: true);
        return await RunAsync(address, pin, cancellationToken, async (http, root, token) =>
        {
            using var response = await http.GetAsync(new Uri(root, "/v1/hello"), HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureSuccessAsync(response, pairing: false, token);
            var info = await ReadJsonAsync<HelloInfo>(response, ConfigSyncPolicy.MaxManifestBytes, token);
            if (info.ProtocolVersion != ConfigSyncPolicy.ProtocolVersion)
                throw new ConfigSyncException(ConfigSyncError.Protocol,
                    $"对方使用共享协议版本 {info.ProtocolVersion}，本机为 {ConfigSyncPolicy.ProtocolVersion}。请两边升级到同一版本。");
            return new HelloInfo(info.ProtocolVersion, info.DeviceName, pin.Fingerprint ?? string.Empty);
        });
    }

    public async Task<PairResult> PairAsync(string address, string code, CancellationToken cancellationToken = default)
    {
        if (!PairingProtocol.IsWellFormedCode(code))
            throw new PairingException(ConfigSyncError.PairingCodeInvalid, "配对码应为 6 位数字。");
        var pin = new PinState(allowFirstUse: true);
        return await RunAsync(address, pin, cancellationToken, async (http, root, token) =>
        {
            var clientNonce = PairingProtocol.NewNonce();
            var begin = await PostAsync<PairBeginRequest, PairBeginResponse>(http, new Uri(root, "/v1/pair/begin"),
                new PairBeginRequest(identity.DeviceName, Convert.ToBase64String(clientNonce)), token);
            var serverNonce = DecodeBytes(begin.Nonce, PairingProtocol.NonceBytes);
            var serverFingerprint = pin.Fingerprint ?? throw new ConfigSyncException(ConfigSyncError.Protocol, "未能取得对方证书指纹。");

            var clientProof = PairingProtocol.ComputeProof(code, 'C', identity.Fingerprint, serverFingerprint, clientNonce, serverNonce);
            var finish = await PostAsync<PairFinishRequest, PairFinishResponse>(http, new Uri(root, "/v1/pair/finish"),
                new PairFinishRequest(Convert.ToBase64String(clientProof)), token);

            // 发布方也必须证明自己知道配对码，防止假冒的发布方骗取订阅。
            var expected = PairingProtocol.ComputeProof(code, 'S', identity.Fingerprint, serverFingerprint, clientNonce, serverNonce);
            if (!PairingProtocol.VerifyProof(expected, DecodeBytes(finish.Proof, 32)))
                throw new PairingException(ConfigSyncError.PairingCodeInvalid, "对方未能通过配对码验证，已放弃配对。");
            return new PairResult(serverFingerprint, begin.Name);
        });
    }

    public async Task<IReadOnlyList<PublicationInfo>> ListPublicationsAsync(string address, string pinnedFingerprint,
        CancellationToken cancellationToken = default)
    {
        var pin = new PinState(pinnedFingerprint);
        return await RunAsync(address, pin, cancellationToken, async (http, root, token) =>
        {
            using var response = await http.GetAsync(new Uri(root, "/v1/manifest"), HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureSuccessAsync(response, pairing: false, token);
            return await ReadJsonAsync<List<PublicationInfo>>(response, ConfigSyncPolicy.MaxManifestBytes, token);
        });
    }

    public async Task<ConfigManifest> GetManifestAsync(string address, string pinnedFingerprint, Guid publishedId,
        CancellationToken cancellationToken = default)
    {
        var pin = new PinState(pinnedFingerprint);
        return await RunAsync(address, pin, cancellationToken, async (http, root, token) =>
        {
            using var response = await http.GetAsync(new Uri(root, $"/v1/manifest?id={publishedId:D}"),
                HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureSuccessAsync(response, pairing: false, token);
            var manifest = await ReadJsonAsync<ConfigManifest>(response, ConfigSyncPolicy.MaxManifestBytes, token);
            if (manifest.ProtocolVersion != ConfigSyncPolicy.ProtocolVersion || manifest.PublishedId != publishedId ||
                manifest.Version < 1 || manifest.Items is null)
                throw new ConfigSyncException(ConfigSyncError.Protocol, "对方返回的清单与请求不一致，已忽略。");
            return manifest;
        });
    }

    public async Task DownloadPackageAsync(string address, string pinnedFingerprint, Guid publishedId, int version,
        string destinationPath, CancellationToken cancellationToken = default)
    {
        var pin = new PinState(pinnedFingerprint);
        try
        {
            await RunAsync<bool>(address, pin, cancellationToken, async (http, root, token) =>
            {
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headerTimeout.CancelAfter(ConfigSyncPolicy.RequestTimeout);
                using var response = await http.GetAsync(new Uri(root, $"/v1/package?id={publishedId:D}&version={version}"),
                    HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                await EnsureSuccessAsync(response, pairing: false, headerTimeout.Token);

                var declared = response.Content.Headers.ContentLength;
                if (declared > ConfigSyncPolicy.MaxPackageBytes)
                    throw new ConfigSyncException(ConfigSyncError.TooLarge,
                        $"配置包 {declared / 1024 / 1024} MB，超过 200 MB 上限，已拒绝接收。");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
                await using var source = await response.Content.ReadAsStreamAsync(headerTimeout.Token);
                await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    // 空闲超时：每次读取重新计时，网络停顿超过 60 秒才判定超时。
                    headerTimeout.CancelAfter(ConfigSyncPolicy.RequestTimeout);
                    var read = await source.ReadAsync(buffer, headerTimeout.Token);
                    if (read == 0) break;
                    total += read;
                    if (total > ConfigSyncPolicy.MaxPackageBytes)
                        throw new ConfigSyncException(ConfigSyncError.TooLarge, "配置包超过 200 MB 上限，已中止接收。");
                    await target.WriteAsync(buffer.AsMemory(0, read), headerTimeout.Token);
                }
                if (declared is not null && total != declared)
                    throw new ConfigSyncException(ConfigSyncError.Protocol, "下载被中断，收到的内容不完整。");
                return true;
            }, totalTimeout: false);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    private async Task<T> RunAsync<T>(string address, PinState pin, CancellationToken cancellationToken,
        Func<HttpClient, Uri, CancellationToken, Task<T>> action, bool totalTimeout = true)
    {
        if (!ConfigSyncPolicy.TryParseAddress(address, out var host, out var port))
            throw new ConfigSyncException(ConfigSyncError.Local, "地址格式不正确，请输入 IP:端口，例如 192.168.1.20:53318。");
        var uriHost = host.Contains(':') ? $"[{host}]" : host;
        var root = new Uri($"https://{uriHost}:{port}/");
        using var http = CreateClient(pin);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 下载自己管理空闲超时；其余请求整体受 60 秒限制。
        if (totalTimeout) timeout.CancelAfter(ConfigSyncPolicy.RequestTimeout);
        try
        {
            return await action(http, root, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigSyncException(ConfigSyncError.Timeout,
                $"连接 {host}:{port} 超过 60 秒没有响应。对方可能已离线、地址不对，或被防火墙拦截。{ConfigSyncPolicy.FirewallHint}");
        }
        catch (HttpRequestException ex)
        {
            throw Map(ex, pin, host, port);
        }
        catch (IOException ex)
        {
            throw Map(ex, pin, host, port);
        }
        catch (AuthenticationException ex)
        {
            throw Map(ex, pin, host, port);
        }
    }

    private HttpClient CreateClient(PinState pin)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && pin.Accept(ConfigSyncPolicy.Fingerprint(certificate.GetRawCertData()))
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private ConfigSyncException Map(Exception ex, PinState pin, string host, int port)
    {
        if (pin.Mismatch)
            return new ConfigSyncException(ConfigSyncError.PinMismatch,
                "对方的证书指纹与配对时保存的不一致，已拒绝连接。可能是对方重装或重置了共享证书，也可能有设备在冒充；请确认后重新配对。", ex);
        var root = ex is HttpRequestException { InnerException: { } inner } ? inner : ex;
        for (var current = root; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                var message = socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused =>
                        $"{host}:{port} 拒绝了连接：对方没有开启共享发布，或端口不对。",
                    SocketError.TimedOut =>
                        $"连接 {host}:{port} 超时。对方可能已离线或被防火墙拦截。{ConfigSyncPolicy.FirewallHint}",
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        $"找不到主机 {host}，请检查地址。",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                        $"无法到达 {host}:{port}（网络不可达），请确认两台电脑在同一局域网。{ConfigSyncPolicy.FirewallHint}",
                    SocketError.ConnectionReset or SocketError.ConnectionAborted =>
                        $"与 {host}:{port} 的连接被对方中断。",
                    _ => $"网络错误（{socket.SocketErrorCode}），无法连接 {host}:{port}。"
                };
                var error = socket.SocketErrorCode == SocketError.TimedOut ? ConfigSyncError.Timeout : ConfigSyncError.Unreachable;
                return new ConfigSyncException(error, message, ex);
            }
            if (current is AuthenticationException)
                return new ConfigSyncException(ConfigSyncError.Protocol,
                    $"与 {host}:{port} 的安全连接握手失败。对方可能不是本软件的共享服务，或版本不兼容。", ex);
        }
        logger.LogDebug(ex, "共享请求失败");
        return new ConfigSyncException(ConfigSyncError.Unreachable, $"与 {host}:{port} 通信失败（{ex.GetType().Name}）。", ex);
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient http, Uri uri, TRequest body, CancellationToken token)
    {
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, ConfigSyncPolicy.Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(uri, content, token);
        await EnsureSuccessAsync(response, pairing: true, token);
        return await ReadJsonAsync<TResponse>(response, ConfigSyncPolicy.MaxManifestBytes, token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, bool pairing, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        ErrorResponse? error = null;
        try
        {
            var bytes = await ReadLimitedAsync(response.Content, 8 * 1024, token);
            error = JsonSerializer.Deserialize<ErrorResponse>(bytes, ConfigSyncPolicy.Json);
        }
        catch (Exception ex) when (ex is JsonException or ConfigSyncException) { }

        var message = error?.Message;
        var status = (int)response.StatusCode;
        if (pairing && status is 400 or 403 or 410 or 429)
        {
            var kind = status switch
            {
                410 => ConfigSyncError.PairingCodeExpired,
                429 => ConfigSyncError.PairingRevoked,
                _ => ConfigSyncError.PairingCodeInvalid
            };
            throw new PairingException(kind, message ?? "配对被对方拒绝。", error?.Remaining ?? 0);
        }
        throw status switch
        {
            401 => new ConfigSyncException(ConfigSyncError.NotPaired, message ?? "此设备尚未与发布者配对。"),
            404 => new ConfigSyncException(ConfigSyncError.NotFound, message ?? "对方没有这份配置。"),
            _ => new ConfigSyncException(ConfigSyncError.Protocol, message ?? $"对方返回了异常响应（HTTP {status}）。")
        };
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, long limit, CancellationToken token)
    {
        var bytes = await ReadLimitedAsync(response.Content, limit, token);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, ConfigSyncPolicy.Json)
                ?? throw new ConfigSyncException(ConfigSyncError.Protocol, "对方返回了空内容。");
        }
        catch (JsonException ex)
        {
            throw new ConfigSyncException(ConfigSyncError.Protocol, "对方返回的数据格式不正确，已忽略。", ex);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit)
            throw new ConfigSyncException(ConfigSyncError.TooLarge, $"对方返回的数据超过 {limit / 1024 / 1024} MB 上限，已拒绝接收。");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (memory.Length + read > limit)
                throw new ConfigSyncException(ConfigSyncError.TooLarge, "对方返回的数据超过大小上限，已中止接收。");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static byte[] DecodeBytes(string? value, int expectedLength)
    {
        try
        {
            var bytes = Convert.FromBase64String(value ?? string.Empty);
            if (bytes.Length == expectedLength) return bytes;
        }
        catch (FormatException) { }
        throw new ConfigSyncException(ConfigSyncError.Protocol, "对方返回的配对数据格式不正确。");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>指纹 pin：首次使用（hello/配对）记录对方指纹，其后每个连接都必须一致；pin 模式下必须等于已保存指纹。</summary>
    private sealed class PinState
    {
        private readonly bool allowFirstUse;
        private readonly object sync = new();

        public PinState(string pinned)
        {
            Fingerprint = pinned.ToUpperInvariant();
        }

        public PinState(bool allowFirstUse)
        {
            this.allowFirstUse = allowFirstUse;
        }

        public string? Fingerprint { get; private set; }
        public bool Mismatch { get; private set; }

        public bool Accept(string fingerprint)
        {
            lock (sync)
            {
                if (Fingerprint is null)
                {
                    if (!allowFirstUse) return false;
                    Fingerprint = fingerprint;
                    return true;
                }
                if (string.Equals(Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return true;
                Mismatch = true;
                return false;
            }
        }
    }
}
