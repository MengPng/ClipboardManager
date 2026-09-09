namespace ClipboardManager.Domain.Models;

public sealed record AppSettings
{
    public int MaxHistoryItems { get; init; } = 1000;
    public int RetentionDays { get; init; } = 30;
    public long MaxTextBytes { get; init; } = 2 * 1024 * 1024;
    public long MaxPersistedImageBytes { get; init; } = 20 * 1024 * 1024;
    public long MaxImageCacheBytes { get; init; } = 512 * 1024 * 1024;
    public bool LaunchAtStartup { get; init; }
    public bool AutoHideWhenDeactivated { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    public bool EnableAutomaticPaste { get; init; } = true;
    public bool SingleClickToPaste { get; init; } = true;
    public bool IsCapturePaused { get; init; }
    public DateTimeOffset? CapturePausedUntil { get; init; }
    public IReadOnlyList<string> ExcludedProcesses { get; init; } = ["KeePass", "1Password", "Bitwarden"];
    public string GlobalHotkey { get; init; } = "Alt+V";
}
