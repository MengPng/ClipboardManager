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
    /// <summary>本机名称：局域网广播、匹配和 LocalSend 里其他设备看到的名字（「共享 → 传输 → 传输设置」修改）。</summary>
    public string LanDeviceAlias { get; init; } = Environment.MachineName;
    /// <summary>0.9.4：用户是否确认过本机名称；第一次打开「传输设置」时未确认就先要求起名。</summary>
    public bool LanDeviceNameConfirmed { get; init; }
    /// <summary>0.9.5：是否已在第一次进入「共享 → 传输」时问过放行防火墙（无论放行与否只问一次）。</summary>
    public bool LanFirewallPrompted { get; init; }
    public int LanPort { get; init; } = 53317;
    public int LanDiscoverySeconds { get; init; } = 2;
    /// <summary>0.9.2（ADR-0008）：常驻监听 53318 并广播，已匹配设备在同一局域网内自动连上。</summary>
    public bool LanKeepOnline { get; init; } = true;
    /// <summary>是否接收已匹配设备发来的文件。</summary>
    public bool LanReceiveEnabled { get; init; } = true;
    /// <summary>已匹配设备发来的文件不再逐个确认，直接接收（默认关）。</summary>
    public bool LanAutoAccept { get; init; }
    /// <summary>接收文件的保存目录；空表示“下载\剪切板传输”。</summary>
    public string LanReceiveFolder { get; init; } = string.Empty;
    /// <summary>0.9.3（ADR-0005）：启动约 30 秒后和之后每 1 小时读取一次下载服务器的版本清单。</summary>
    public bool AutoCheckForUpdates { get; init; } = true;
    /// <summary>用户选择"跳过此版本"的版本号；自动检查不再提示该版本，手动检查仍会显示。</summary>
    public string? SkippedUpdateVersion { get; init; }
    public bool CloudModeEnabled { get; init; }
    public string CloudApiBaseUrl { get; init; } = "https://clipboard.netkaize.com";
}
