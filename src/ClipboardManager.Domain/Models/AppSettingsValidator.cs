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
        if (settings.ExcludedProcesses is null || settings.ExcludedProcesses.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("应用排除列表不得包含空项。");
        if (string.IsNullOrWhiteSpace(settings.GlobalHotkey)) throw new ArgumentException("呼出快捷键不能为空。");
    }
}
