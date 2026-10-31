using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Windows;

/// <summary>
/// 共享页：发布本机配置文件夹、配对订阅设备；订阅他人发布的配置（受管文件夹只读）。
/// 所有网络与数据库操作都在服务层的后台任务里完成，这里只负责展示与确认；
/// 任何失败只弹出中文原因，不影响剪切板监听与本地功能。
/// </summary>
public partial class MainWindow
{
    private const string PairingHintDefault = "对方在“订阅”页输入本机地址和配对码；码 5 分钟有效，输错 5 次作废。";

    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<PairedPeer> _sharePeers = [];
    private string _sharePublisherSignature = string.Empty;
    private string _shareFingerprintPrefix = string.Empty;
    private bool _shareSubscribeSegment, _shareListening;
    private long _shareVersion;
    private int _shareRefreshQueued;

    public ObservableCollection<PublishedRowViewModel> PublishedRows { get; } = [];
    public ObservableCollection<SubscriberRowViewModel> SubscriberRows { get; } = [];
    public ObservableCollection<PublisherRowViewModel> PublisherRows { get; } = [];
    public ObservableCollection<PairedPublisherRowViewModel> PairedPublisherRows { get; } = [];
    public ObservableCollection<SubscriptionRowViewModel> SubscriptionRows { get; } = [];

    private void InitializeShare()
    {
        _pairingTimer.Tick += (_, _) => UpdatePairingCodeText();
        _configSync.Changed += (_, _) => OnShareChanged();
        _configSync.AutoUpdated += (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            try { if (IsVisible && IsCollectionTabActive) await RefreshFoldersAsync(); }
            catch (Exception exception) { Debug.WriteLine($"订阅自动更新后刷新配置页失败: {exception}"); }
        });
        _configDiscovery.Changed += (_, _) => Dispatcher.InvokeAsync(RebuildPublisherRows);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || !IsShareTabActive) return;
            if (_shareSubscribeSegment) StartShareListening();
            OnShareChanged();
        };
    }

    // ---- 订阅页监听组播：只在“共享 → 订阅”可见时开启 ----

    private void StartShareListening()
    {
        if (_shareListening) return;
        _shareListening = true;
        try { _configDiscovery.StartListening(); }
        catch (Exception exception) { Debug.WriteLine($"开始监听组播失败: {exception}"); }
    }

    private void StopShareListening()
    {
        if (!_shareListening) return;
        _shareListening = false;
        try { _configDiscovery.StopListening(); }
        catch (Exception exception) { Debug.WriteLine($"停止监听组播失败: {exception}"); }
    }

    private void OnShareChanged()
    {
        // 配置同步的后台事件可能在任意线程、短时间内连发多次；合并成一次界面刷新。
        if (Interlocked.Exchange(ref _shareRefreshQueued, 1) == 1) return;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            Interlocked.Exchange(ref _shareRefreshQueued, 0);
            try { if (IsVisible && IsShareTabActive) await RefreshShareAsync(); }
            catch (Exception exception) { Debug.WriteLine($"刷新共享页失败: {exception}"); }
        });
    }

    // ---- 刷新 ----

    private async Task RefreshShareAsync()
    {
        var version = Interlocked.Increment(ref _shareVersion);
        var published = await _configPublish.GetPublishedAsync();
        var subscribers = await _configPublish.GetSubscribersAsync();
        var peers = await _configSubscriptions.GetPublisherPeersAsync();
        var subscriptions = await _configSubscriptions.GetSubscriptionsAsync();
        var folders = (await _collectionService.GetFoldersAsync(_personalWorkspaceId)).Folders;
        var publishedRows = new List<PublishedRowViewModel>();
        foreach (var config in published)
        {
            // 发布范围：全部个人（非受管）文件夹，或所选的若干文件夹；条目数只统计现在仍存在的文件夹。
            var scope = config.AllFolders
                ? folders.Where(candidate => candidate.ManagedSubscriptionId is null).ToList()
                : config.FolderIds.Select(id => folders.FirstOrDefault(candidate => candidate.Id == id)).OfType<CollectionFolder>().ToList();
            var count = 0;
            foreach (var folder in scope) count += (await _collectionService.GetItemsAsync(config.WorkspaceId, folder.Id)).Count;
            var missing = config.AllFolders ? 0 : config.FolderIds.Count - scope.Count;
            var scopeText = config.AllFolders ? $"全部个人配置（{scope.Count} 个文件夹）"
                : scope.Count == 1 && missing == 0 ? $"文件夹“{scope[0].Name}”"
                : $"{scope.Count} 个文件夹" + (missing > 0 ? $"（{missing} 个已删除）" : string.Empty);
            publishedRows.Add(new PublishedRowViewModel(config, scopeText, count));
        }
        var subscriptionRows = new List<SubscriptionRowViewModel>();
        foreach (var subscription in subscriptions)
        {
            var snapshots = await _configSubscriptions.GetSnapshotsAsync(subscription.Id);
            var peerName = peers.FirstOrDefault(peer => peer.Id == subscription.PeerId)?.Name ?? "未知设备";
            subscriptionRows.Add(new SubscriptionRowViewModel(subscription, peerName,
                snapshots.Any(snapshot => snapshot.Version < subscription.LocalVersion)));
        }
        var prefix = await Task.Run(() => ConfigSyncPolicy.FingerprintPrefix(_configIdentity.Fingerprint));
        if (version != Interlocked.Read(ref _shareVersion)) return;

        _shareFingerprintPrefix = prefix;
        _sharePeers = peers;
        Replace(PublishedRows, publishedRows);
        Replace(SubscriberRows, subscribers.Select(peer => new SubscriberRowViewModel(peer)));
        Replace(PairedPublisherRows, peers.Select(peer => new PairedPublisherRowViewModel(peer)));
        Replace(SubscriptionRows, subscriptionRows);
        _sharePublisherSignature = string.Empty;
        RebuildPublisherRows();

        SharePublishEmptyText.Visibility = VisibleIf(PublishedRows.Count == 0);
        ShareSubscriberEmptyText.Visibility = VisibleIf(SubscriberRows.Count == 0);
        ShareSubscriptionEmptyText.Visibility = VisibleIf(SubscriptionRows.Count == 0);
        SharePairedEmptyText.Visibility = VisibleIf(PairedPublisherRows.Count == 0);
        UpdateShareLocalAddress();
        UpdatePairingCodeText();
        UpdateShareStatus();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private static Visibility VisibleIf(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>组播每几秒广播一次；只有发现列表真的变化时才重建，避免列表闪动。</summary>
    private void RebuildPublisherRows()
    {
        var self = _shareFingerprintPrefix;
        var discovered = _configDiscovery.GetPublishers()
            .Where(publisher => !string.Equals(publisher.FingerprintPrefix, self, StringComparison.OrdinalIgnoreCase))
            .OrderBy(publisher => publisher.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var signature = string.Join("|", discovered.Select(publisher =>
            $"{publisher.Address}/{publisher.FingerprintPrefix}/{publisher.DeviceName}/{string.Join(",", publisher.Publications.Select(item => $"{item.Name}:{item.Version}"))}")) +
            "#" + string.Join(",", _sharePeers.Select(peer => peer.Id));
        if (signature == _sharePublisherSignature) { UpdateShareStatus(); return; }
        _sharePublisherSignature = signature;
        Replace(PublisherRows, discovered.Select(publisher => new PublisherRowViewModel(publisher,
            _sharePeers.FirstOrDefault(peer => peer.Status == PeerStatus.Active &&
                ConfigSyncPolicy.FingerprintPrefix(peer.Fingerprint).Equals(publisher.FingerprintPrefix, StringComparison.OrdinalIgnoreCase)))));
        SharePublisherEmptyText.Visibility = VisibleIf(PublisherRows.Count == 0);
        UpdateShareStatus();
    }

    private void UpdateShareStatus()
    {
        if (_shareSubscribeSegment)
        {
            ShareStatusText.Text = PublisherRows.Count == 0 ? "未发现设备" : $"发现 {PublisherRows.Count} 台设备";
            CountText.Text = $"{SubscriptionRows.Count} 个订阅";
            return;
        }
        var status = _configSync.ServerStatus;
        ShareStatusText.Text = status.IsRunning ? $"监听中 · 端口 {status.Port}"
            : status.LastError is not null ? "监听失败" : "未监听（发布后自动开启）";
        ShareStatusText.ToolTip = status.LastError;
        SharePairingHintText.Text = !status.IsRunning && status.LastError is not null
            ? $"无法启动本机监听：{status.LastError}。{ConfigSyncPolicy.FirewallHint}"
            : PairingHintDefault;
        CountText.Text = $"{PublishedRows.Count} 项发布";
    }

    private void UpdateShareLocalAddress()
    {
        var port = _configSync.ServerStatus.IsRunning ? _configSync.ServerStatus.Port : ConfigSyncPolicy.DefaultPort;
        var addresses = LocalIPv4Addresses();
        ShareLocalAddressText.Text = addresses.Count == 0
            ? "未检测到可用的局域网地址，请检查网络连接。"
            : $"本机地址（对方手动输入）：{string.Join(" 或 ", addresses.Select(address => $"{address}:{port}"))}\n证书指纹 {_shareFingerprintPrefix}（配对时请与对方屏幕上的指纹核对）";
    }

    private static IReadOnlyList<string> LocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"读取本机地址失败: {exception}");
            return [];
        }
    }

    // ---- 段切换 ----

    private async void SharePublishChip_Click(object sender, RoutedEventArgs e) => await SwitchShareSegmentAsync(false);
    private async void ShareSubscribeChip_Click(object sender, RoutedEventArgs e) => await SwitchShareSegmentAsync(true);

    private async Task SwitchShareSegmentAsync(bool subscribe)
    {
        _shareSubscribeSegment = subscribe;
        SharePublishChip.IsChecked = !subscribe;
        ShareSubscribeChip.IsChecked = subscribe;
        SharePublishScroll.Visibility = VisibleIf(!subscribe);
        ShareSubscribeScroll.Visibility = VisibleIf(subscribe);
        if (subscribe) StartShareListening(); else StopShareListening();
        await RunShareActionAsync("刷新共享页", RefreshShareAsync);
    }

    /// <summary>共享页统一的错误出口：中文原因；连不上或超时时补充防火墙放行提示。</summary>
    private async Task RunShareActionAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Debug.WriteLine($"{name}失败: {exception}");
            var message = exception.Message;
            if (exception is ConfigSyncException { Error: ConfigSyncError.Unreachable or ConfigSyncError.Timeout } &&
                !message.Contains("防火墙"))
                message += "\n\n" + ConfigSyncPolicy.FirewallHint;
            System.Windows.MessageBox.Show(this, message, $"{name}失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            try { await RefreshShareAsync(); } catch (Exception inner) { Debug.WriteLine($"失败后刷新共享页失败: {inner}"); }
        }
    }

    private void Info(string message, string title = "共享") =>
        System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private bool Confirm(string title, string body, string okText, string? riskLabel, out bool riskConfirmed)
    {
        var dialog = new ShareConfirmDialog(this, title, body, okText, riskLabel);
        var accepted = WithModal(() => dialog.ShowDialog()) == true;
        riskConfirmed = dialog.RiskConfirmed;
        return accepted;
    }

    private static T? RowOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    // ---- 发布段 ----

    private async void SharePublishNewButton_Click(object sender, RoutedEventArgs e) =>
        await RunShareActionAsync("发布配置", PublishNewFolderAsync);

    private async Task PublishNewFolderAsync()
    {
        // 订阅得到的受管文件夹不能再发布；空文件夹没有内容可发，不列出。
        var folders = (await _collectionService.GetFoldersAsync(_personalWorkspaceId)).Folders
            .Where(folder => folder.ManagedSubscriptionId is null).ToList();
        var choices = new List<(Guid Id, string Name, int Count)>();
        foreach (var folder in folders)
        {
            var count = (await _collectionService.GetItemsAsync(_personalWorkspaceId, folder.Id)).Count;
            if (count > 0) choices.Add((folder.Id, folder.Name, count));
        }
        if (choices.Count == 0)
        {
            Info("没有可发布的内容：个人配置里还没有任何条目（订阅得到的文件夹不能再发布）。");
            return;
        }
        var dialog = new SharePublishFoldersDialog(this, choices);
        if (WithModal(() => dialog.ShowDialog()) != true) return;
        // 全部勾选 = 发布全部个人配置（之后新增的文件夹也会自动并入）；否则只发布勾选的文件夹。
        IReadOnlyList<Guid>? selection = dialog.AllSelected ? null : dialog.SelectedIds;
        var total = choices.Where(choice => dialog.SelectedIds.Contains(choice.Id)).Sum(choice => choice.Count);
        var body = (dialog.AllSelected
                       ? $"将一次发布全部个人配置：{choices.Count} 个文件夹、共 {total} 项，作为同一个版本。\n\n之后新建的文件夹也会并入这份发布。\n\n"
                       : $"将一次发布所选的 {dialog.SelectedIds.Count} 个文件夹、共 {total} 项，作为同一个版本。\n\n") +
                   "已配对的订阅设备可以读取其中的文本、链接、图片和文件（单个文件不超过 50 MB，超过的会被跳过）。" +
                   "剪切板历史不会被发布，未选的文件夹也不会。若已发布过同样的范围，会发布为它的新版本。";
        if (!Confirm("发布配置", body, "发布", null, out _)) return;
        var result = await _configPublish.PublishAsync(_personalWorkspaceId, selection);
        await RefreshShareAsync();
        Info(result.Changed
            ? PublishSummary(result, "已发布")
            : $"内容没有变化，没有产生新版本（仍是 v{result.Config.CurrentVersion}）。");
    }

    private static string PublishSummary(PublishResult result, string verb)
    {
        var text = $"{verb}“{result.Config.Name}” v{result.Config.CurrentVersion}（{result.FileCount} 个文件）。";
        if (result.SkippedMissingFiles > 0) text += $"\n有 {result.SkippedMissingFiles} 个文件已不存在或超过大小上限，已跳过。";
        return text;
    }

    private async void PublishNewVersion_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PublishedRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("发布新版本", async () =>
        {
            var result = await _configPublish.PublishAsync(row.Config.WorkspaceId, row.Config.AllFolders ? null : row.Config.FolderIds);
            await RefreshShareAsync();
            Info(result.Changed
                ? PublishSummary(result, "已发布")
                : $"内容没有变化，没有产生新版本（仍是 v{result.Config.CurrentVersion}）。");
        });
    }

    private async void StopPublishing_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PublishedRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("停止发布", async () =>
        {
            var body = $"停止发布“{row.Name}”？\n\n订阅方已经安装的内容不受影响，但之后无法再检查或下载它的更新；已配对的设备不会被移除，你的文件夹也不会被删除。";
            if (!Confirm("停止发布", body, "停止发布", null, out _)) return;
            await _configPublish.StopPublishingAsync(row.Config.Id);
            await RefreshShareAsync();
        });
    }

    private async void AutoPublishCheck_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PublishedRowViewModel>(sender) is not { } row || sender is not System.Windows.Controls.CheckBox box) return;
        var enabled = box.IsChecked == true;
        await RunShareActionAsync("自动发布", async () =>
        {
            await _configPublish.SetAutoPublishAsync(row.Config.Id, enabled);
            await RefreshShareAsync();
        });
    }

    private async void ShareIssueCodeButton_Click(object sender, RoutedEventArgs e) =>
        await RunShareActionAsync("生成配对码", async () =>
        {
            await _configSync.IssuePairingCodeAsync();
            UpdateShareLocalAddress();
            UpdatePairingCodeText();
            UpdateShareStatus();
        });

    private void UpdatePairingCodeText()
    {
        var current = _configPublish.CurrentPairingCode;
        var remaining = current is null ? TimeSpan.Zero : current.ExpiresAt - DateTimeOffset.UtcNow;
        if (current is null || remaining <= TimeSpan.Zero)
        {
            _pairingTimer.Stop();
            SharePairingCodeText.Text = "——";
            ShareIssueCodeButton.Content = "生成配对码";
            return;
        }
        SharePairingCodeText.Text = $"{current.Code[..3]} {current.Code[3..]}  ·  {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        ShareIssueCodeButton.Content = "重新生成";
        if (!_pairingTimer.IsEnabled) _pairingTimer.Start();
    }

    private async void ShareCopyFirewallButton_Click(object sender, RoutedEventArgs e) =>
        await RunShareActionAsync("复制防火墙命令", async () =>
        {
            await _restoreService.CopyTextAsync(string.Join(Environment.NewLine, ConfigSyncPolicy.FirewallCommands));
            Info("已复制两条 netsh 命令。请在“以管理员身份运行”的 PowerShell 或命令提示符中粘贴运行，放行 TCP/UDP 53318。\n\n（安装包版自动添加防火墙规则尚未实现。）");
        });

    private async void RemoveSubscriber_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<SubscriberRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("移除订阅设备", async () =>
        {
            var body = $"移除“{row.Name}”？\n\n它将不能再检查或下载你发布的配置，需要重新配对才能订阅。它已经安装的内容保留在它自己的电脑上。";
            if (!Confirm("移除订阅设备", body, "移除", null, out _)) return;
            await _configPublish.RemoveSubscriberAsync(row.Peer.Id);
            await RefreshShareAsync();
        });
    }

    // ---- 订阅段：配对与浏览 ----

    private async void PublisherAction_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PublisherRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync(row.Paired is null ? "配对" : "浏览发布", () => row.Paired is { } paired
            ? BrowseAsync(paired, row.Publisher.Address)
            : PairAndBrowseAsync(row.Publisher.Address, row.Publisher.FingerprintPrefix));
    }

    private async void ShareManualAddressBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        await ConnectManualAsync();
    }

    private async void ShareManualConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectManualAsync();

    private async Task ConnectManualAsync()
    {
        var text = ShareManualAddressBox.Text.Trim();
        if (!ConfigSyncPolicy.TryParseAddress(text, out var host, out var port))
        {
            Info($"地址格式不正确。请输入对方的 IP 或 IP:端口，例如 192.168.1.20:{ConfigSyncPolicy.DefaultPort}。", "手动连接");
            return;
        }
        var address = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        await RunShareActionAsync("手动连接", () => PairAndBrowseAsync(address, null));
    }

    private async Task PairAndBrowseAsync(string address, string? advertisedPrefix)
    {
        var hello = await _configSubscriptions.HelloAsync(address);
        var prefix = ConfigSyncPolicy.FingerprintPrefix(hello.Fingerprint);
        if (advertisedPrefix is not null && !prefix.Equals(advertisedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ConfigSyncException(ConfigSyncError.PinMismatch,
                "该设备出示的证书指纹与它广播的指纹不一致，已停止配对。请确认地址是否正确，或稍后重试。");
        var peers = await _configSubscriptions.GetPublisherPeersAsync();
        var known = peers.FirstOrDefault(peer => peer.Status == PeerStatus.Active &&
            peer.Fingerprint.Equals(hello.Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            await BrowseAsync(known, address);
            return;
        }
        var dialog = new SharePairDialog(this, hello.DeviceName, address, prefix);
        if (WithModal(() => dialog.ShowDialog()) != true) return;
        var peer = await _configSubscriptions.PairAsync(address, dialog.Code);
        await RefreshShareAsync();
        await BrowseAsync(peer, address);
    }

    private async void BrowsePublisher_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PairedPublisherRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("浏览发布", () => BrowseAsync(row.Peer, null));
    }

    private async void RemovePublisher_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PairedPublisherRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("移除配对", async () =>
        {
            var body = $"移除与“{row.Name}”的配对？之后需要重新配对才能订阅它的配置。\n\n如果你仍订阅着它的配置，需要先取消那些订阅。";
            if (!Confirm("移除配对", body, "移除", null, out _)) return;
            await _configSubscriptions.RemovePublisherAsync(row.Peer.Id);
            await RefreshShareAsync();
        });
    }

    private async Task BrowseAsync(PairedPeer peer, string? addressOverride)
    {
        var publications = await _configSubscriptions.ListPublicationsAsync(peer.Id, addressOverride);
        await RefreshShareAsync();
        if (publications.Count == 0)
        {
            Info($"“{peer.Name}”目前没有可订阅的发布。", "浏览发布");
            return;
        }
        var subscribed = (await _configSubscriptions.GetSubscriptionsAsync())
            .Where(subscription => subscription.PeerId == peer.Id)
            .Select(subscription => subscription.PublishedId)
            .ToHashSet();
        var lines = publications.Select(item => $"{item.Name}  v{item.Version}" + (subscribed.Contains(item.PublishedId) ? "  · 已订阅" : "")).ToList();
        var list = new ShareListDialog(this, $"{peer.Name} 的发布", "选择要订阅的配置：", lines, "查看并订阅");
        if (WithModal(() => list.ShowDialog()) != true || list.SelectedIndex < 0) return;
        var chosen = publications[list.SelectedIndex];
        if (subscribed.Contains(chosen.PublishedId))
        {
            Info($"已经订阅了“{chosen.Name}”，可在上方“我的订阅”里检查更新。", "浏览发布");
            return;
        }

        var manifest = await _configSubscriptions.GetRemoteManifestAsync(peer.Id, chosen.PublishedId, addressOverride);
        var risky = manifest.Items.SelectMany(item => item.RiskyFiles).Distinct().ToList();
        var body = $"发布者：{peer.Name}（证书指纹 {ConfigSyncPolicy.FingerprintPrefix(peer.Fingerprint)}）\n" +
                   $"配置：{manifest.Name} v{manifest.Version}\n" +
                   $"内容：{DescribeTypes(manifest)}，约 {FormatBytes(manifest.PackageBytes)}\n\n" +
                   (manifest.FolderList.Count > 1
                       ? $"订阅后会在“配置”页创建 {manifest.FolderList.Count} 个只读的受管文件夹（{FolderNames(manifest)}），内容随发布者整体更新；"
                       : $"订阅后会在“配置”页创建只读的受管文件夹“{manifest.FolderList.FirstOrDefault()?.Name ?? manifest.Name}”，内容随发布者更新；") +
                   "不能直接修改，可用“复制到我的配置”另存一份后再改。";
        if (risky.Count > 0)
            body += "\n\n⚠ 包含可执行类文件（导入后也不会自动打开，请只订阅你信任的发布者）：\n" + string.Join("\n", risky.Take(10).Select(name => "· " + name)) +
                    (risky.Count > 10 ? $"\n… 另有 {risky.Count - 10} 个" : "");
        if (!Confirm("订阅配置", body, "订阅", risky.Count > 0 ? "我了解风险，仍要导入这些文件" : null, out var riskConfirmed)) return;
        await _configSubscriptions.SubscribeAsync(_personalWorkspaceId, peer.Id, chosen.PublishedId, riskConfirmed, addressOverride);
        await RefreshShareAsync();
        Info($"已订阅“{manifest.Name}” v{manifest.Version}。到“配置”页可以看到受管文件夹。");
    }

    private static string FolderNames(ConfigManifest manifest)
    {
        var names = manifest.FolderList.Select(folder => $"“{folder.Name}”").ToList();
        return string.Join("、", names.Take(6)) + (names.Count > 6 ? $" 等 {names.Count} 个" : string.Empty);
    }

    private static string DescribeTypes(ConfigManifest manifest)
    {
        var groups = manifest.Items.GroupBy(item => (CollectionItemType)item.Type).ToDictionary(group => group.Key, group => group.Count());
        var parts = new List<string> { $"共 {manifest.Items.Count} 项" };
        foreach (var (type, label) in new[] { (CollectionItemType.Text, "文本"), (CollectionItemType.Link, "链接"), (CollectionItemType.Image, "图片"), (CollectionItemType.File, "文件") })
            if (groups.TryGetValue(type, out var count)) parts.Add($"{label} {count}");
        return string.Join("、", parts);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / 1024d / 1024d:0.#} MB"
    };

    // ---- 订阅段：我的订阅 ----

    private async void CheckSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<SubscriptionRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("检查更新", async () =>
        {
            var check = await _configSubscriptions.CheckAsync(row.Subscription.Id);
            await RefreshShareAsync();
            switch (check.State)
            {
                case ConfigUpdateState.UpToDate:
                    Info(check.Message, "检查更新");
                    break;
                case ConfigUpdateState.UpdateAvailable:
                    await ConfirmAndApplyAsync(row.Subscription, check);
                    break;
                case ConfigUpdateState.Skipped or ConfigUpdateState.Stayed:
                    Info(check.Message + "。需要时可点“更新”手动更新。", "检查更新");
                    break;
                default:
                    var message = check.Message;
                    if (check.State == ConfigUpdateState.PublisherOffline && !message.Contains("防火墙"))
                        message += "\n\n" + ConfigSyncPolicy.FirewallHint;
                    System.Windows.MessageBox.Show(this, message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        });
    }

    private async void UpdateSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<SubscriptionRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("更新订阅", async () =>
        {
            // 重新询问发布者，确保摘要对应的是此刻的最新版本。
            var check = await _configSubscriptions.CheckAsync(row.Subscription.Id);
            await RefreshShareAsync();
            if (check.Remote is { } remote && remote.Version > row.Subscription.LocalVersion)
                await ConfirmAndApplyAsync(row.Subscription, check);
            else
                System.Windows.MessageBox.Show(this, check.Message, "更新订阅", MessageBoxButton.OK,
                    check.State == ConfigUpdateState.UpToDate ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async Task ConfirmAndApplyAsync(ConfigSubscription subscription, ConfigUpdateCheck check)
    {
        var remote = check.Remote!;
        var changes = check.Changes ?? ConfigSyncPolicy.Compare(ConfigSyncPolicy.ParseManifest(subscription.ManifestJson), remote);
        var risky = remote.Items.SelectMany(item => item.RiskyFiles).Distinct().ToList();
        var body = new System.Text.StringBuilder();
        body.AppendLine($"“{subscription.Name}” 将从 v{subscription.LocalVersion} 更新到 v{remote.Version}。");
        body.AppendLine($"新增 {changes.Added} · 修改 {changes.Modified} · 删除 {changes.Removed}");
        if (changes.FoldersAdded > 0 || changes.FoldersRemoved > 0)
            body.AppendLine($"文件夹：新增 {changes.FoldersAdded} · 删除 {changes.FoldersRemoved}");
        AppendTitles(body, "新增", changes.AddedTitles, changes.Added);
        AppendTitles(body, "修改", changes.ModifiedTitles, changes.Modified);
        AppendTitles(body, "删除", changes.RemovedTitles, changes.Removed);
        body.AppendLine();
        body.Append("更新前会保留当前版本的快照，之后可以“回退”；下载或校验失败时保留当前版本不变。");
        if (risky.Count > 0)
        {
            body.AppendLine().AppendLine();
            body.AppendLine("⚠ 新版本包含可执行类文件（导入后不会自动打开）：");
            foreach (var name in risky.Take(10)) body.AppendLine("· " + name);
            if (risky.Count > 10) body.AppendLine($"… 另有 {risky.Count - 10} 个");
        }
        if (!Confirm("更新订阅", body.ToString().TrimEnd(), "更新",
                risky.Count > 0 ? "我了解风险，仍要导入这些文件" : null, out var riskConfirmed)) return;
        var updated = await _configSubscriptions.ApplyUpdateAsync(subscription.Id, riskConfirmed);
        await RefreshShareAsync();
        Info($"“{updated.Name}” 已更新到 v{updated.LocalVersion}。", "更新订阅");
    }

    private static void AppendTitles(System.Text.StringBuilder body, string label, IReadOnlyList<string> titles, int total)
    {
        if (total == 0) return;
        body.AppendLine($"{label}：{string.Join("、", titles.Select(title => string.IsNullOrWhiteSpace(title) ? "（无标题）" : title))}" +
                        (total > titles.Count ? $" 等 {total} 项" : ""));
    }

    private async void RollbackSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<SubscriptionRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("回退版本", async () =>
        {
            var subscription = row.Subscription;
            var snapshots = await _configSubscriptions.GetSnapshotsAsync(subscription.Id);
            var previous = snapshots.Where(snapshot => snapshot.Version < subscription.LocalVersion)
                .OrderByDescending(snapshot => snapshot.Version).FirstOrDefault();
            if (previous is null)
            {
                Info("没有可回退的历史版本。", "回退版本");
                return;
            }
            var body = $"将“{subscription.Name}”从 v{subscription.LocalVersion} 回退到 v{previous.Version}。\n\n" +
                       $"回退后会自动跳过 v{subscription.LocalVersion}，不再提示更新到它；可在“订阅设置”里取消跳过。最多保留 3 个版本快照。";
            if (!Confirm("回退版本", body, "回退", null, out _)) return;
            var result = await _configSubscriptions.RollbackAsync(subscription.Id);
            await RefreshShareAsync();
            Info($"已回退到 v{result.LocalVersion}。", "回退版本");
        });
    }

    private async void SubscriptionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<SubscriptionRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("订阅设置", async () =>
        {
            var subscription = row.Subscription;
            var dialog = new SubscriptionSettingsDialog(this, subscription);
            if (WithModal(() => dialog.ShowDialog()) != true) return;
            if (dialog.UnsubscribeRequested)
            {
                await UnsubscribeAsync(subscription);
                return;
            }
            await _configSubscriptions.UpdateOptionsAsync(subscription.Id, dialog.AutoUpdate, dialog.StayOnCurrent, dialog.SkippedVersion);
            await RefreshShareAsync();
        });
    }

    private async Task UnsubscribeAsync(ConfigSubscription subscription)
    {
        var count = 0;
        var folderCount = 1;
        try
        {
            var manifest = ConfigSyncPolicy.ParseManifest(subscription.ManifestJson);
            count = manifest.Items.Count;
            folderCount = Math.Max(1, manifest.FolderList.Count);
        }
        catch (Exception exception) { Debug.WriteLine($"读取订阅清单失败: {exception}"); }
        var target = folderCount > 1 ? $"受管的 {folderCount} 个文件夹" : "受管文件夹";
        var body = $"取消订阅“{subscription.Name}”？\n\n这会删除{target}及其中的全部内容（{count} 项）和历史快照。" +
                   "你自己的配置、以及用“复制到我的配置”另存的副本不受影响。此操作不能撤销。";
        if (!Confirm("取消订阅", body, "取消订阅并删除", null, out _)) return;
        await _configSubscriptions.UnsubscribeAsync(subscription.Id);
        await RefreshShareAsync();
        if (IsCollectionTabActive) await RefreshFoldersAsync();
    }

    // ---- 配置页：受管（订阅）内容只读 ----

    private Task ManagedReadOnlyNoticeAsync()
    {
        Info("这是订阅得到的内容，只读，由订阅更新替换。可以右键选择“复制到我的配置”，另存一份后再修改。", "只读内容");
        return Task.CompletedTask;
    }

    private async Task CopyManagedFolderToMineAsync(FolderViewModel folder)
    {
        var copy = await _configSubscriptions.CopyFolderToMineAsync(folder.WorkspaceId, folder.Folder.Id);
        await RefreshFoldersAsync();
        Info($"已复制为你自己的文件夹“{copy.Name}”，可以自由编辑。", "复制到我的配置");
    }

    private async Task CopyManagedItemToMineAsync(CollectionItem item)
    {
        await _configSubscriptions.CopyItemToMineAsync(item.WorkspaceId, item);
        await RefreshFoldersAsync();
        Info("已复制到你的“未分类”文件夹，可以自由编辑。", "复制到我的配置");
    }
}
