namespace ClipboardManager.Domain.Models;

public static class AppSettingsValidator
{
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxHistoryItems is < 1 or > 100000) throw new ArgumentException("历史条数必须为 1–100000。");
        if (settings.RetentionDays is < 1 or > 3650) throw new ArgumentException("保留天数必须为 1–3650。");
        if (settings.MaxTextBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentException("文本上限必须在 1 字节至 16 MB 之间。");
        if (settings.MaxPersistedImageBytes is < 1 or > 100 * 1024 * 1024) throw new ArgumentException("图片上限必须在 1 字节至 100 MB 之间。");
        if (settings.MaxImageCacheBytes < settings.MaxPersistedImageBytes) throw new ArgumentException("图片缓存上限不得小于单张图片上限。");
        if (!Enum.IsDefined(settings.ClipboardCalculatorMode)) throw new ArgumentException("剪切板算式模式只能是关闭、仅提示或直接改写。");
        if (settings.Theme is not ("standard" or "jiajia" or "kami" or "kubai")) throw new ArgumentException("界面主题只能是标准、夹夹、Kami 或 Kubai。");
        if (settings.ExcludedProcesses is null || settings.ExcludedProcesses.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("应用排除列表不得包含空项。");
        if (string.IsNullOrWhiteSpace(settings.GlobalHotkey)) throw new ArgumentException("呼出快捷键不能为空。");
        if (string.IsNullOrWhiteSpace(settings.SelectionSearchHotkey)) throw new ArgumentException("划词查找快捷键不能为空。");
        if (string.Equals(NormalizeHotkey(settings.GlobalHotkey), NormalizeHotkey(settings.SelectionSearchHotkey), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("划词查找快捷键不能与呼出快捷键相同。");
        if (settings.SelectionToolbarApps is null || settings.SelectionToolbarApps.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("划词工具条应用列表不得包含空项。");
        if (settings.SelectionToolbarApps.Count > 50)
            throw new ArgumentException("划词工具条应用最多 50 个。");
        if (settings.SelectionToolbarApps.Concat(settings.ExcludedProcesses).Any(name => name.IndexOfAny(['\\', '/', ':']) >= 0))
            throw new ArgumentException("应用名称只需填写进程名，例如 Weixin，不要包含路径。");
        if (settings.AutoBackupIntervalHours is not (0 or 24 or 48 or 72))
            throw new ArgumentException("自动备份周期只能是关闭、24、48 或 72 小时。");
        if (string.IsNullOrWhiteSpace(settings.LanDeviceAlias) || settings.LanDeviceAlias.Trim().Length > 40)
            throw new ArgumentException("局域网设备名称必须为 1–40 个字符。");
        if (settings.LanPort is < 1024 or > 65535)
            throw new ArgumentException("局域网端口必须为 1024–65535。");
        if (settings.LanDiscoverySeconds is < 1 or > 10)
            throw new ArgumentException("设备发现等待时间必须为 1–10 秒。");
        if (settings.LanReceiveFolder is null ||
            (settings.LanReceiveFolder.Length > 0 && (!Path.IsPathFullyQualified(settings.LanReceiveFolder) ||
                                                      settings.LanReceiveFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)))
            throw new ArgumentException("接收文件的保存位置必须是完整的文件夹路径。");
        if (!string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl) &&
            (!Uri.TryCreate(settings.CloudApiBaseUrl, UriKind.Absolute, out var cloudApiUri) ||
             cloudApiUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("云端服务地址必须是有效的 HTTPS 地址。");
        if (settings.SkippedUpdateVersion is { } skipped && !Version.TryParse(skipped, out _))
            throw new ArgumentException("跳过的更新版本号格式无效。");
    }

    /// <summary>Order-insensitive comparison key for "Ctrl+Alt+F" vs "Alt+Control+F".</summary>
    public static string NormalizeHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return string.Empty;
        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.ToUpperInvariant() switch { "CONTROL" => "CTRL", "WINDOWS" => "WIN", var other => other })
            .ToList();
        if (parts.Count == 0) return string.Empty;
        var key = parts[^1];
        return string.Join('+', parts[..^1].Distinct().Order(StringComparer.Ordinal).Append(key));
    }
}
