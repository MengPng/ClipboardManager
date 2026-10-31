using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Infrastructure.ConfigSync;

public sealed record ConfigSyncServerOptions(int Port = ConfigSyncPolicy.DefaultPort);

/// <summary>
/// 发布方的 HTTPS 监听：自签证书 + 双向 TLS。除 hello 与配对外，所有端点只接受已配对订阅方的证书指纹。
/// 只实现本协议需要的最小 HTTP/1.1 子集（一连接一请求）。
/// </summary>
public sealed class ConfigSyncServer(
    ConfigSyncIdentity identity,
    IConfigSyncEndpoint endpoint,
    ILogger<ConfigSyncServer> logger,
    ConfigSyncServerOptions? options = null) : IConfigSyncServer, IAsyncDisposable
{
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxBodyBytes = 4 * 1024;
    private const int MaxConcurrent = 8;

    private readonly ConfigSyncServerOptions serverOptions = options ?? new ConfigSyncServerOptions();
    private readonly SemaphoreSlim gate = new(MaxConcurrent, MaxConcurrent);
    private readonly object sync = new();
    private TcpListener? listener;
    private CancellationTokenSource? stopping;
    private Task? acceptLoop;
    private SyncServerStatus status = new(false, 0, string.Empty, null);

    public SyncServerStatus Status
    {
        get { lock (sync) return status; }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (listener is not null) return Task.CompletedTask;
            var fingerprint = identity.Fingerprint;
            TcpListener? created = null;
            try
            {
                created = CreateListener(serverOptions.Port);
                created.Start(16);
            }
            catch (SocketException ex)
            {
                created?.Stop();
                var message = ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                    ? $"端口 {serverOptions.Port} 已被其他程序占用或不允许监听，无法开启共享发布。请关闭占用该端口的程序后重试。"
                    : $"无法在端口 {serverOptions.Port} 上开始监听（{ex.SocketErrorCode}）。";
                status = new SyncServerStatus(false, serverOptions.Port, fingerprint, message);
                throw new ConfigSyncException(ConfigSyncError.Local, message, ex);
            }
            listener = created;
            var port = ((IPEndPoint)created.LocalEndpoint).Port;
            stopping = new CancellationTokenSource();
            status = new SyncServerStatus(true, port, fingerprint, null);
            var token = stopping.Token;
            acceptLoop = Task.Run(() => AcceptLoopAsync(created, token));
            logger.LogInformation("配置共享监听已启动，端口 {Port}", port);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (sync)
        {
            if (listener is null) return;
            stopping!.Cancel();
            listener.Stop();
            loop = acceptLoop;
            listener = null;
            acceptLoop = null;
            status = status with { IsRunning = false };
        }
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        logger.LogInformation("配置共享监听已停止");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static TcpListener CreateListener(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            var dual = new TcpListener(IPAddress.IPv6Any, port);
            dual.Server.DualMode = true;
            return dual;
        }
        return new TcpListener(IPAddress.Any, port);
    }

    private async Task AcceptLoopAsync(TcpListener active, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await active.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested) return;
                logger.LogWarning(ex, "接受共享连接失败：{Code}", ex.SocketErrorCode);
                continue;
            }
            _ = Task.Run(() => HandleAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        if (!await gate.WaitAsync(0, CancellationToken.None))
        {
            client.Dispose();
            return;
        }
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(ConfigSyncPolicy.RequestTimeout);
                await using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity.Certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    // 接受任何客户端证书以完成握手；是否信任由各端点按已保存的指纹 pin 决定。
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null
                }, deadline.Token);
                var clientFingerprint = ConfigSyncPolicy.Fingerprint(ssl.RemoteCertificate!.GetRawCertData());
                var request = await ReadRequestAsync(ssl, deadline.Token);
                await DispatchAsync(ssl, request, clientFingerprint, deadline, stop);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or AuthenticationException or InvalidDataException or SocketException or ObjectDisposedException)
        {
            // 握手失败、对端中断或请求格式错误：只记录类型，不影响监听。
            logger.LogDebug("共享连接已结束：{Type}", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理共享请求时出现未预期的错误");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DispatchAsync(SslStream stream, HttpRequest request, string clientFingerprint,
        CancellationTokenSource deadline, CancellationToken stop)
    {
        var token = deadline.Token;
        try
        {
            switch (request.Method, request.Path)
            {
                case ("GET", "/v1/hello"):
                    await WriteJsonAsync(stream, 200, new HelloInfo(ConfigSyncPolicy.ProtocolVersion, endpoint.DeviceName, identity.Fingerprint), token);
                    return;

                case ("POST", "/v1/pair/begin"):
                {
                    var body = ParseBody<PairBeginRequest>(request);
                    var nonce = DecodeBytes(body.Nonce, PairingProtocol.NonceBytes);
                    var (serverNonce, name) = await endpoint.PairBeginAsync(clientFingerprint, body.Name ?? string.Empty, nonce, token);
                    await WriteJsonAsync(stream, 200, new PairBeginResponse(Convert.ToBase64String(serverNonce), name), token);
                    return;
                }

                case ("POST", "/v1/pair/finish"):
                {
                    var body = ParseBody<PairFinishRequest>(request);
                    var proof = DecodeBytes(body.Proof, 32);
                    var (serverProof, name) = await endpoint.PairFinishAsync(clientFingerprint, identity.Fingerprint, proof, token);
                    await WriteJsonAsync(stream, 200, new PairFinishResponse(Convert.ToBase64String(serverProof), name), token);
                    return;
                }
            }

            if (request.Method != "GET" || request.Path is not ("/v1/manifest" or "/v1/package"))
            {
                await WriteErrorAsync(stream, 404, "NotFound", "不支持的请求。", null, token);
                return;
            }

            if (!await endpoint.IsPinnedAsync(clientFingerprint, token))
            {
                await WriteErrorAsync(stream, 401, nameof(ConfigSyncError.NotPaired), "此设备尚未与发布者配对，或配对已被发布者移除。", null, token);
                return;
            }

            if (request.Path == "/v1/manifest")
            {
                string json;
                if (request.Query.TryGetValue("id", out var idText))
                {
                    if (!Guid.TryParse(idText, out var id))
                    {
                        await WriteErrorAsync(stream, 400, nameof(ConfigSyncError.Protocol), "发布编号格式不正确。", null, token);
                        return;
                    }
                    var manifest = await endpoint.GetManifestAsync(id, token);
                    if (manifest is null)
                    {
                        await WriteErrorAsync(stream, 404, nameof(ConfigSyncError.NotFound), "发布者已不再发布这份配置。", null, token);
                        return;
                    }
                    json = ConfigSyncPolicy.SerializeManifest(manifest);
                }
                else
                {
                    json = JsonSerializer.Serialize(await endpoint.ListPublicationsAsync(token), ConfigSyncPolicy.Json);
                }
                var bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.Length > ConfigSyncPolicy.MaxManifestBytes)
                {
                    await WriteErrorAsync(stream, 500, nameof(ConfigSyncError.TooLarge), "清单超过 4 MB 上限，无法提供。", null, token);
                    return;
                }
                await WriteBytesAsync(stream, 200, "application/json; charset=utf-8", bytes, token);
                return;
            }

            if (!request.Query.TryGetValue("id", out var packageId) || !Guid.TryParse(packageId, out var publishedId) ||
                !request.Query.TryGetValue("version", out var versionText) || !int.TryParse(versionText, out var version) || version < 1)
            {
                await WriteErrorAsync(stream, 400, nameof(ConfigSyncError.Protocol), "缺少或错误的发布编号/版本号。", null, token);
                return;
            }
            var path = await endpoint.GetPackagePathAsync(publishedId, version, token);
            if (path is null || !File.Exists(path))
            {
                await WriteErrorAsync(stream, 404, nameof(ConfigSyncError.NotFound), "发布者没有这个版本的配置包。", null, token);
                return;
            }
            await SendPackageAsync(stream, path, deadline, stop);
        }
        catch (PairingException ex)
        {
            var code = ex.Error switch
            {
                ConfigSyncError.PairingCodeExpired => 410,
                ConfigSyncError.PairingRevoked => 429,
                _ => 403
            };
            await WriteErrorAsync(stream, code, ex.Error.ToString(), ex.Message, ex.RemainingAttempts, token);
        }
        catch (InvalidDataException ex)
        {
            await WriteErrorAsync(stream, 400, nameof(ConfigSyncError.Protocol), ex.Message, null, token);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(stream, 400, nameof(ConfigSyncError.Protocol), "请求内容格式不正确。", null, token);
        }
    }

    private static async Task SendPackageAsync(SslStream stream, string path, CancellationTokenSource deadline, CancellationToken stop)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {file.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, deadline.Token);
        var buffer = new byte[81920];
        int read;
        while (true)
        {
            // 每次读写重新计时：只要对方持续接收就不会因总时长被切断，对方停止接收超过 60 秒则放弃。
            deadline.CancelAfter(ConfigSyncPolicy.RequestTimeout);
            read = await file.ReadAsync(buffer, deadline.Token);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
        }
        await stream.FlushAsync(deadline.Token);
    }

    private static T ParseBody<T>(HttpRequest request)
    {
        if (request.Body.Length == 0) throw new InvalidDataException("请求缺少内容。");
        return JsonSerializer.Deserialize<T>(request.Body, ConfigSyncPolicy.Json) ?? throw new InvalidDataException("请求内容为空。");
    }

    private static byte[] DecodeBytes(string? value, int expectedLength)
    {
        try
        {
            var bytes = Convert.FromBase64String(value ?? string.Empty);
            if (bytes.Length != expectedLength) throw new InvalidDataException("字段长度不正确。");
            return bytes;
        }
        catch (FormatException)
        {
            throw new InvalidDataException("字段编码不正确。");
        }
    }

    private static async Task<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes + MaxBodyBytes];
        var length = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length >= MaxHeaderBytes) throw new InvalidDataException("请求头过长。");
            var read = await stream.ReadAsync(buffer.AsMemory(length, MaxHeaderBytes - length), token);
            if (read == 0) throw new InvalidDataException("连接在请求完成前关闭。");
            length += read;
            headerEnd = IndexOfHeaderEnd(buffer, length);
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new InvalidDataException("请求行格式不正确。");
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                !int.TryParse(line[(colon + 1)..].Trim(), out contentLength))
                throw new InvalidDataException("Content-Length 不正确。");
        }
        if (contentLength is < 0 or > MaxBodyBytes) throw new InvalidDataException("请求内容过大。");

        var bodyStart = headerEnd + 4;
        var have = length - bodyStart;
        var body = new byte[contentLength];
        Array.Copy(buffer, bodyStart, body, 0, Math.Min(have, contentLength));
        var filled = Math.Min(have, contentLength);
        while (filled < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled), token);
            if (read == 0) throw new InvalidDataException("请求内容不完整。");
            filled += read;
        }

        var target = requestLine[1];
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (question >= 0)
        {
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                query[key] = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return new HttpRequest(requestLine[0].ToUpperInvariant(), path, query, body);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n') return i - 3;
        return -1;
    }

    private static Task WriteJsonAsync<T>(Stream stream, int status, T value, CancellationToken token) =>
        WriteBytesAsync(stream, status, "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(value, ConfigSyncPolicy.Json), token);

    private static Task WriteErrorAsync(Stream stream, int status, string error, string message, int? remaining, CancellationToken token) =>
        WriteJsonAsync(stream, status, new ErrorResponse(error, message, remaining), token);

    private static async Task WriteBytesAsync(Stream stream, int status, string contentType, byte[] body, CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
            410 => "Gone", 429 => "Too Many Requests", _ => "Error"
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Query, byte[] Body);
}

internal sealed record PairBeginRequest(string? Name, string? Nonce);
internal sealed record PairBeginResponse(string Nonce, string Name);
internal sealed record PairFinishRequest(string? Proof);
internal sealed record PairFinishResponse(string Proof, string Name);
internal sealed record ErrorResponse(string Error, string Message, int? Remaining);
