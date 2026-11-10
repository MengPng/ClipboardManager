using System.Windows;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Windows;

/// <summary>共享页“发布”段：一份已发布的配置。</summary>
public sealed class PublishedRowViewModel(PublishedConfig config, string scopeText, int itemCount)
{
    public PublishedConfig Config { get; } = config;
    public string Name => Config.Name;
    public string VersionText => $"v{Config.CurrentVersion}";
    public string Detail => $"{scopeText} · {itemCount} 项 · {Config.UpdatedAt.LocalDateTime:MM-dd HH:mm} 发布";
    public bool AutoPublish => Config.AutoPublish;
}

/// <summary>共享页“发布”段：已配对、可以订阅我发布内容的设备。</summary>
public sealed class SubscriberRowViewModel(PairedPeer peer)
{
    public PairedPeer Peer { get; } = peer;
    public string Name => Peer.Name;
    public string Detail => $"指纹 {ConfigSyncPolicy.FingerprintPrefix(Peer.Fingerprint)} · {Peer.PairedAt.LocalDateTime:MM-dd HH:mm} 配对";
}

/// <summary>共享页“订阅”段：组播发现到的发布者。</summary>
public sealed class PublisherRowViewModel(DiscoveredPublisher publisher, PairedPeer? paired)
{
    public DiscoveredPublisher Publisher { get; } = publisher;
    public PairedPeer? Paired { get; } = paired;
    public string DeviceName => Publisher.DeviceName;
    public string Detail => $"{Publisher.Address} · 指纹 {Publisher.FingerprintPrefix}";
    public string PublicationsText => Publisher.Publications.Count == 0
        ? "没有公开的发布"
        : string.Join("、", Publisher.Publications.Select(item => $"{item.Name} v{item.Version}"));
    public string ActionText => Paired is null ? "配对" : "浏览发布";
}

/// <summary>共享页“订阅”段：已配对的发布设备。</summary>
public sealed class PairedPublisherRowViewModel(PairedPeer peer)
{
    public PairedPeer Peer { get; } = peer;
    public string Name => Peer.Name;
    public string Detail => $"{Peer.LastAddress ?? "地址未知"} · 指纹 {ConfigSyncPolicy.FingerprintPrefix(Peer.Fingerprint)}";
}

/// <summary>共享页“订阅”段：一份订阅。状态完全由已持久化的订阅记录推出，重启后保持一致。</summary>
public sealed class SubscriptionRowViewModel(ConfigSubscription subscription, string peerName, bool canRollback)
{
    public ConfigSubscription Subscription { get; } = subscription;
    public string Name => Subscription.Name;
    public string VersionText => $"v{Subscription.LocalVersion}";
    public string Detail => $"来自 {peerName} · 上次检查 " +
        (Subscription.LastCheckedAt is { } at ? at.LocalDateTime.ToString("MM-dd HH:mm") : "尚未检查");

    public bool HasNewer => Subscription.LatestKnownVersion is { } latest && latest > Subscription.LocalVersion;
    public bool IsProblem => !string.IsNullOrEmpty(Subscription.LastError) || (HasNewer && !IsHeldBack);
    private bool IsHeldBack => Subscription.StayOnCurrent || Subscription.SkippedVersion == Subscription.LatestKnownVersion;

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrEmpty(Subscription.LastError)) return Subscription.LastError!;
            if (Subscription.LatestKnownVersion is not { } latest || latest <= Subscription.LocalVersion) return "已是最新";
            if (Subscription.StayOnCurrent) return $"已停留在 v{Subscription.LocalVersion}，发布者有 v{latest}";
            if (Subscription.SkippedVersion == latest) return $"已跳过 v{latest}（发布者最新）";
            return $"有新版本 v{latest}";
        }
    }

    public string OptionsText
    {
        get
        {
            var parts = new List<string>();
            if (Subscription.AutoUpdate) parts.Add("自动更新");
            if (Subscription.StayOnCurrent) parts.Add("停留当前版本");
            if (Subscription.SkippedVersion is { } skipped) parts.Add($"跳过 v{skipped}");
            return parts.Count == 0 ? "提示后更新" : string.Join(" · ", parts);
        }
    }

    public Visibility UpdateButtonVisibility => HasNewer ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRollback { get; } = canRollback;
}

/// <summary>
/// 共享页“传输”段：一台已匹配设备（同一指纹的发布者/订阅者记录合并）。
/// 行上显示在线状态与最近一次传输；拖入文件时 IsDropTarget 让行高亮。
/// </summary>
public sealed class MatchedDeviceRowViewModel(MatchedDevice device) : System.ComponentModel.INotifyPropertyChanged
{
    private string? _activity;
    private double _activePercent;
    private bool _hasProgress;
    private bool _isDropTarget;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public MatchedDevice Device { get; } = device;
    public string Name => Device.Name;
    public bool IsOnline => Device.IsOnline;
    public string Detail => $"{(Device.IsOnline ? "在线" : "未在线")} · {Device.Address ?? "地址未知"} · 指纹 {ConfigSyncPolicy.FingerprintPrefix(Device.Fingerprint)}";
    public string AccessibleName => $"{Name}，{StatusText}";
    public Visibility BrowseVisibility => Device.CanBrowse ? Visibility.Visible : Visibility.Collapsed;
    public string StatusText => _activity is null ? OnlineText : $"{OnlineText} · {_activity}";
    /// <summary>名字后的在线标签。</summary>
    public string OnlineText => Device.IsOnline ? "在线" : "离线";
    public double ActivePercent => _activePercent;
    public Visibility ActiveProgressVisibility => _hasProgress ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            Notify(nameof(IsDropTarget));
        }
    }

    /// <summary>用这台设备的传输快照（新的在前）更新行上的状态文字与进度。</summary>
    public void UpdateActivity(IReadOnlyList<FileTransferSnapshot> transfers)
    {
        var running = transfers.Where(item => !item.IsFinished).ToList();
        string? activity;
        var hasProgress = false;
        double percent = 0;
        if (running.Count == 1)
        {
            var item = running[0];
            activity = item.State switch
            {
                FileTransferState.Preparing => $"准备发送 {item.FileName}",
                FileTransferState.WaitingForAccept => item.Direction == FileTransferDirection.Send ? "等待对方确认" : $"准备接收 {item.FileName}",
                FileTransferState.Verifying => $"校验 {item.FileName}",
                _ => $"{(item.Direction == FileTransferDirection.Send ? "发送中" : "接收中")} {TransferRowViewModel.PercentOf(item):0}% · {item.FileName}"
            };
            hasProgress = item.State == FileTransferState.Transferring;
            percent = TransferRowViewModel.PercentOf(item);
        }
        else if (running.Count > 1)
        {
            var size = running.Sum(item => item.Size);
            percent = size <= 0 ? 0 : Math.Clamp(running.Sum(item => item.Transferred) * 100d / size, 0, 100);
            activity = $"{running.Count} 个文件传输中 · {percent:0}%";
            hasProgress = true;
        }
        else if (transfers.FirstOrDefault() is { } last)
        {
            var verb = last.State switch
            {
                FileTransferState.Completed => last.Direction == FileTransferDirection.Send ? "已发送" : "已收到",
                FileTransferState.Declined => "对方未接收",
                FileTransferState.Canceled => "已取消",
                _ => last.Direction == FileTransferDirection.Send ? "发送失败" : "接收失败"
            };
            activity = $"{verb} {last.FileName}";
        }
        else activity = null;

        if (activity == _activity && hasProgress == _hasProgress && Math.Abs(percent - _activePercent) < 0.05) return;
        _activity = activity;
        _hasProgress = hasProgress;
        _activePercent = percent;
        Notify(string.Empty);
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>共享页“传输”段：附近还没有匹配的设备。</summary>
public sealed class NearbyDeviceRowViewModel(DiscoveredPublisher device)
{
    public DiscoveredPublisher Device { get; } = device;
    public string DeviceName => Device.DeviceName;
    public string Detail => $"{Device.Address} · 指纹 {Device.FingerprintPrefix}";
}

/// <summary>共享页“传输”段：一条发送或接收任务；进度原地更新，避免列表重建打断点击。</summary>
public sealed class TransferRowViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private FileTransferSnapshot _snapshot;

    public TransferRowViewModel(FileTransferSnapshot snapshot) => _snapshot = snapshot;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => _snapshot.Id;
    public FileTransferSnapshot Snapshot => _snapshot;
    public string FileName => _snapshot.FileName;

    public string Detail
    {
        get
        {
            var direction = _snapshot.Direction == FileTransferDirection.Send ? $"发给 {_snapshot.PeerName}" : $"来自 {_snapshot.PeerName}";
            return Compose(direction);
        }
    }

    /// <summary>设备页里不再重复设备名，只写方向。</summary>
    public string DeviceDetail => Compose(_snapshot.Direction == FileTransferDirection.Send ? "发送" : "接收");

    private string Compose(string direction)
    {
        var state = _snapshot.State switch
        {
            FileTransferState.Preparing => "计算校验值",
            FileTransferState.WaitingForAccept => _snapshot.Direction == FileTransferDirection.Send ? "等待对方确认" : "等待对方开始",
            FileTransferState.Transferring => $"{Percent:0}%",
            FileTransferState.Verifying => "校验中",
            FileTransferState.Completed => "已完成",
            FileTransferState.Declined => "对方未接收",
            FileTransferState.Canceled => "已取消",
            _ => "失败"
        };
        var text = $"{direction} · {FormatSize(_snapshot.Size)} · {state}";
        return string.IsNullOrEmpty(_snapshot.Message) || _snapshot.State == FileTransferState.Completed ? text : $"{text} · {_snapshot.Message}";
    }

    public double Percent => PercentOf(_snapshot);

    internal static double PercentOf(FileTransferSnapshot snapshot) =>
        snapshot.Size <= 0 ? (snapshot.IsFinished ? 100 : 0) : Math.Clamp(snapshot.Transferred * 100d / snapshot.Size, 0, 100);
    public Visibility ProgressVisibility => _snapshot.IsFinished ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CancelVisibility => _snapshot.IsFinished ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OpenVisibility =>
        _snapshot.State == FileTransferState.Completed && _snapshot.SavedPath is not null ? Visibility.Visible : Visibility.Collapsed;
    public bool IsProblem => _snapshot.State is FileTransferState.Failed or FileTransferState.Declined;

    public void Update(FileTransferSnapshot snapshot)
    {
        _snapshot = snapshot;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} 字节"
    };
}
