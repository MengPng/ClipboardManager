using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Infrastructure.Paths;

namespace ClipboardManager.Infrastructure.Updates;

/// <summary>
/// 从下载服务器读取 latest.json 与安装包（ADR-0005）。请求只是匿名 GET，不带剪切板内容、
/// 设备 ID 或令牌。安装包下载到 <c>%LOCALAPPDATA%\ClipboardManager\updates\&lt;版本&gt;\</c>，
/// 写完先核对大小与 SHA-256 才改成正式文件名，不一致即删除。
/// </summary>
public sealed class HttpAppUpdateClient(HttpClient http, AppPaths paths) : IAppUpdateClient
{
    public static readonly Uri DefaultManifestUrl = new("https://clipboard.netkaize.com/download/latest.json");
    public const int MaxManifestBytes = 256 * 1024;

    public Uri ManifestUrl { get; init; } = DefaultManifestUrl;
    public TimeSpan ManifestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public string UpdatesRoot => Path.Combine(paths.Root, "updates");

    public async Task<string> FetchManifestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ManifestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxManifestBytes) throw new InvalidDataException("版本清单过大，已忽略。");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes) throw new InvalidDataException("版本清单过大，已忽略。");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public async Task<string> DownloadAsync(AppUpdateFile file, Version version, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(file.Name) != file.Name) throw new InvalidDataException("安装包文件名无效。");
        var directory = Path.Combine(UpdatesRoot, version.ToString(3));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, file.Name);
        if (File.Exists(target) && await MatchesAsync(target, file, cancellationToken))
        {
            progress?.Report(1);
            return target;
        }

        var partial = target + ".partial";
        try
        {
            if (File.Exists(partial)) File.Delete(partial);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != file.Size)
                throw new InvalidDataException("安装包大小与版本清单不一致，已停止下载。");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                var lastReported = -1;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > file.Size) throw new InvalidDataException("安装包比版本清单声明的更大，已停止下载。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    var percent = (int)(total * 100 / file.Size);
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        progress?.Report(total / (double)file.Size);
                    }
                }
                await output.FlushAsync(timeout.Token);
            }

            if (total != file.Size) throw new InvalidDataException("安装包下载不完整，请重试。");
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != file.Sha256) throw new InvalidDataException("安装包 SHA-256 校验失败，已删除下载的文件。");

            File.Move(partial, target, overwrite: true);
            DeleteOtherVersions(version);
            return target;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    public void CleanupDownloads(Version installed)
    {
        if (!Directory.Exists(UpdatesRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(UpdatesRoot))
        {
            if (!Version.TryParse(Path.GetFileName(directory), out var version) || version <= installed)
            {
                TryDeleteDirectory(directory);
            }
        }
    }

    private void DeleteOtherVersions(Version keep)
    {
        foreach (var directory in Directory.EnumerateDirectories(UpdatesRoot))
        {
            if (Path.GetFileName(directory) != keep.ToString(3)) TryDeleteDirectory(directory);
        }
    }

    private static async Task<bool> MatchesAsync(string path, AppUpdateFile file, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return actual == file.Sha256;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
