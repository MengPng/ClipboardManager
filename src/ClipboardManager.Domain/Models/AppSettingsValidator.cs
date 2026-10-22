namespace ClipboardManager.Domain.Models;

public static class AppSettingsValidator
{
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.SelectionAi);
        SelectionAiSettings.Validate(settings.SelectionAi);
        if (settings.MaxHistoryItems is < 1 or > 100000) throw new ArgumentException("历史条数必须为 1–100000。");
        if (settings.RetentionDays is < 1 or > 3650) throw new ArgumentException("保留天数必须为 1–3650。");
        if (settings.MaxTextBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentException("文本上限必须在 1 字节至 16 MB 之间。");
        if (settings.MaxPersistedImageBytes is < 1 or > 100 * 1024 * 1024) throw new ArgumentException("图片上限必须在 1 字节至 100 MB 之间。");
        if (settings.MaxImageCacheBytes < settings.MaxPersistedImageBytes) throw new ArgumentException("图片缓存上限不得小于单张图片上限。");
        if (settings.ExcludedProcesses is null || settings.ExcludedProcesses.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("应用排除列表不得包含空项。");
        if (string.IsNullOrWhiteSpace(settings.GlobalHotkey)) throw new ArgumentException("呼出快捷键不能为空。");
        if (settings.AutoBackupIntervalHours is not (0 or 24 or 48 or 72))
            throw new ArgumentException("自动备份周期只能是关闭、24、48 或 72 小时。");
        if (string.IsNullOrWhiteSpace(settings.LanDeviceAlias) || settings.LanDeviceAlias.Trim().Length > 40)
            throw new ArgumentException("局域网设备名称必须为 1–40 个字符。");
        if (settings.LanPort is < 1024 or > 65535)
            throw new ArgumentException("局域网端口必须为 1024–65535。");
        if (settings.LanDiscoverySeconds is < 1 or > 10)
            throw new ArgumentException("设备发现等待时间必须为 1–10 秒。");
        if (!string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl) &&
            (!Uri.TryCreate(settings.CloudApiBaseUrl, UriKind.Absolute, out var cloudApiUri) ||
             cloudApiUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("云端服务地址必须是有效的 HTTPS 地址。");
    }
}
