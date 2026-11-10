using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

/// <summary>
/// 检查更新的传输层（ADR-0005）：只读取公开的版本清单和安装包，请求中不带任何剪切板内容、
/// 设备或账号信息。
/// </summary>
public interface IAppUpdateClient
{
    /// <summary>读取 latest.json 原文。</summary>
    Task<string> FetchManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>下载安装包并核对大小与 SHA-256，返回本地路径；不一致时删除并抛出异常。</summary>
    Task<string> DownloadAsync(AppUpdateFile file, Version version, IProgress<double>? progress, CancellationToken cancellationToken = default);

    /// <summary>删除不高于 <paramref name="installed"/> 的已下载安装包。</summary>
    void CleanupDownloads(Version installed);
}
