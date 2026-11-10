namespace ClipboardManager.Domain.Models;

/// <summary>latest.json 中的一个安装包（ADR-0005）。</summary>
public sealed record AppUpdateFile(string Kind, string Name, Uri Url, long Size, string Sha256);

/// <summary>校验通过的最新版本清单；<see cref="Executable"/> 为空表示该版本没有可就地替换的单文件 EXE。</summary>
public sealed record AppUpdateManifest(
    Version Version,
    string Title,
    IReadOnlyList<string> Notes,
    Uri PageUrl,
    DateTimeOffset? ReleasedAt,
    AppUpdateFile? Executable);

public enum AppUpdateStatus
{
    /// <summary>已是最新版本。</summary>
    UpToDate,
    /// <summary>有更新的版本。</summary>
    Available,
    /// <summary>有更新的版本，但用户选择了跳过（只在自动检查时出现）。</summary>
    Skipped,
    /// <summary>清单格式或 schema 不认识，按"无更新"处理。</summary>
    Unsupported,
    /// <summary>网络或服务器错误。</summary>
    Failed,
}

public sealed record AppUpdateCheck(AppUpdateStatus Status, AppUpdateManifest? Manifest = null, string? Error = null);
