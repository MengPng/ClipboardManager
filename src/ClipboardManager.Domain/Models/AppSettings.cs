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
    public bool EnableAutomaticPaste { get; init; }
    public bool SingleClickToPaste { get; init; }
    public bool CompactList { get; init; }
    public ClipboardCalculatorMode ClipboardCalculatorMode { get; init; } = ClipboardCalculatorMode.Hint;
    public bool CalculatorKeypadOpen { get; init; }
    public bool ShowPanelPet { get; init; } = true;
    public string Theme { get; init; } = "standard";
    public bool IsCapturePaused { get; init; }
    public DateTimeOffset? CapturePausedUntil { get; init; }
    public IReadOnlyList<string> ExcludedProcesses { get; init; } = ["KeePass", "1Password", "Bitwarden"];
    public string GlobalHotkey { get; init; } = "Alt+V";
    /// <summary>0.9.1: select text anywhere, press the hotkey, find the matching local file via Everything.</summary>
    public bool SelectionSearchEnabled { get; init; } = true;
    public string SelectionSearchHotkey { get; init; } = "Ctrl+Alt+F";
    /// <summary>Optional mouse-gesture toolbar; off by default and only active in <see cref="SelectionToolbarApps"/>.</summary>
    public bool SelectionToolbarEnabled { get; init; }
    public IReadOnlyList<string> SelectionToolbarApps { get; init; } = ["Weixin", "WeChat", "WXWork", "QQ", "DingTalk", "Feishu", "Lark"];
    public bool AutoBackupChoiceCompleted { get; init; }
    public int AutoBackupIntervalHours { get; init; }
    public DateTimeOffset? LastAutomaticBackupAt { get; init; }
    public string LanDeviceAlias { get; init; } = Environment.MachineName;
    public int LanPort { get; init; } = 53317;
    public int LanDiscoverySeconds { get; init; } = 2;
    public bool CloudModeEnabled { get; init; }
    public string CloudApiBaseUrl { get; init; } = "https://clipboard.netkaize.com";
}
