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
