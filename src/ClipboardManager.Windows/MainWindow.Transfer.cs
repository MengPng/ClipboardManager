using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Application.Services.LanTransfer;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Windows;

/// <summary>
/// 共享页“传输”段（ADR-0008）：固定匹配码、已匹配设备（重命名/移除）、发送与接收文件。
/// 一级页只放匹配码与设备列表；设备、匹配设备、传输设置是二级页（‹ 或 Esc 返回）。
/// 只发送用户显式选择、拖入的文件或历史记录；收到的文件不进入历史、不自动打开。
/// </summary>
public partial class MainWindow
{
    private enum TransferPage { Home, Device, Match, Settings }

    private int _transferRefreshQueued;
    private TransferPage _transferPage = TransferPage.Home;
    private string? _transferDeviceFingerprint;

    private bool IsShareTransferActive => IsShareTabActive && _shareSegment == ShareSegment.Transfer;

    // ---- 页面切换 ----

    private void ShowTransferPage(TransferPage page, MatchedDeviceRowViewModel? device = null)
    {
        if (page == TransferPage.Device && device is null) page = TransferPage.Home;
        _transferPage = page;
        _transferDeviceFingerprint = page == TransferPage.Device ? device!.Device.Fingerprint : null;
        TransferDevicePanel.DataContext = page == TransferPage.Device ? device : null;
        TransferHomePanel.Visibility = VisibleIf(page == TransferPage.Home);
        TransferDevicePanel.Visibility = VisibleIf(page == TransferPage.Device);
        TransferMatchPanel.Visibility = VisibleIf(page == TransferPage.Match);
        TransferSettingsPanel.Visibility = VisibleIf(page == TransferPage.Settings);
        ShareTransferScroll.ScrollToTop();
        SyncTransferRows();
        UpdateShareStatus();
    }

    private void OpenMatchedDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<MatchedDeviceRowViewModel>(sender) is { } row) ShowTransferPage(TransferPage.Device, row);
    }

    private void ShareOpenMatchPage_Click(object sender, RoutedEventArgs e)
    {
        ShowTransferPage(TransferPage.Match);
        ShareTransferAddressBox.Focus();
    }

    /// <summary>第一次进入「传输设置」时，本机名称还没确认过就先要求起名（取消则下次再问），之后再显示设置页。</summary>
    private async void ShareOpenTransferSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunShareActionAsync("本机名称", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            if (!settings.LanDeviceNameConfirmed) await RenameThisDeviceAsync(firstTime: true);
        });
        ShowTransferPage(TransferPage.Settings);
        await RunShareActionAsync("防火墙", RefreshFirewallStateAsync);
    }

    private async void ShareRenameDeviceButton_Click(object sender, RoutedEventArgs e) =>
        await RunShareActionAsync("本机名称", () => RenameThisDeviceAsync(firstTime: false));

    /// <summary>
    /// 本机名称是局域网里其他电脑看到的名字（广播、匹配、LocalSend 共用 <c>LanDeviceAlias</c>）。
    /// 保存后重启监听，下一次广播起生效；对方已保存的名字是它自己起的，不会被覆盖。
    /// </summary>
    private async Task RenameThisDeviceAsync(bool firstTime)
    {
        var settings = await _settingsStore.LoadAsync();
        var title = firstTime ? "给本机起个名字" : "本机名称";
        var prompt = firstTime
            ? "局域网里的其他电脑会看到这个名字，方便认出是哪一台："
            : "局域网里的其他电脑看到的名字：";
        var dialog = new SingleInputDialog(this, title, prompt, settings.LanDeviceAlias);
        if (WithModal(() => dialog.ShowDialog()) != true) return;
        var name = LanMatchPolicy.CleanDeviceName(dialog.Value);
        if (name.Length == 0)
        {
            Info("名称不能为空。", title);
            return;
        }
        var latest = await _settingsStore.LoadAsync();
        if (name == latest.LanDeviceAlias && latest.LanDeviceNameConfirmed) return;
        await _settingsStore.SaveAsync(latest with { LanDeviceAlias = name, LanDeviceNameConfirmed = true });
        await _configSync.SetKeepOnlineAsync();
        await RefreshShareAsync();
    }

    private void TransferBack_Click(object sender, RoutedEventArgs e) => ShowTransferPage(TransferPage.Home);

    /// <summary>刷新会重建设备行；设备页换成同一指纹的新行，设备已被移除时回到一级页。</summary>
    private void ReattachTransferDevice()
    {
        if (_transferPage != TransferPage.Device) return;
        var row = MatchedDeviceRows.FirstOrDefault(item => item.Device.Fingerprint.Equals(_transferDeviceFingerprint, StringComparison.OrdinalIgnoreCase));
        if (row is null) ShowTransferPage(TransferPage.Home);
        else TransferDevicePanel.DataContext = row;
    }

    // ---- 已匹配设备：重命名、移除、浏览 ----

    private async void RenameSubscriber_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<SubscriberRowViewModel>(sender) is { } row) await RenameDeviceAsync(row.Peer.Fingerprint, row.Name);
    }

    private async void RenamePublisher_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<PairedPublisherRowViewModel>(sender) is { } row) await RenameDeviceAsync(row.Peer.Fingerprint, row.Name);
    }

    private async void RenameMatchedDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<MatchedDeviceRowViewModel>(sender) is { } row) await RenameDeviceAsync(row.Device.Fingerprint, row.Name);
    }

    /// <summary>重命名只改本机看到的名字；同一台设备的发布、订阅两条匹配记录一起改，重新匹配后保留。</summary>
    private Task RenameDeviceAsync(string fingerprint, string currentName) =>
        RunShareActionAsync("重命名设备", async () =>
        {
            var dialog = new SingleInputDialog(this, "重命名设备", "只改本机显示的名字，对方看不到：", currentName);
            if (WithModal(() => dialog.ShowDialog()) != true) return;
            var name = LanMatchPolicy.CleanDeviceName(dialog.Value);
            if (name.Length == 0)
            {
                Info("名称不能为空。", "重命名设备");
                return;
            }
            if (name == currentName) return;
            await _lanDevices.RenameAsync(fingerprint, name);
            await RefreshShareAsync();
        });

    private async void RemoveMatchedDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<MatchedDeviceRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("移除匹配", async () =>
        {
            var body = $"移除与“{row.Name}”的匹配？\n\n移除后双方不能再互相发文件、订阅配置，需要重新输入匹配码才能恢复。" +
                       "已经收到的文件和已安装的订阅内容不受影响。";
            if (!Confirm("移除匹配", body, "移除", null, out _)) return;
            await _lanDevices.RemoveAsync(row.Device.Fingerprint);
            ShowTransferPage(TransferPage.Home);
            await RefreshShareAsync();
        });
    }

    private async void BrowseMatchedDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<MatchedDeviceRowViewModel>(sender)?.Device.Peers.FirstOrDefault(peer => peer.Role == PeerRole.Publisher) is not { } peer) return;
        await RunShareActionAsync("浏览发布", () => BrowseAsync(peer, null));
    }

    // ---- 匹配新设备 ----

    private async void MatchNearbyDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<NearbyDeviceRowViewModel>(sender) is not { } row) return;
        await RunShareActionAsync("匹配设备", () => MatchDeviceAsync(row.Device.Address, row.Device.FingerprintPrefix));
    }

    private async void ShareTransferMatchButton_Click(object sender, RoutedEventArgs e) => await MatchManualAsync();

    private async void ShareTransferAddressBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        await MatchManualAsync();
    }

    private async Task MatchManualAsync()
    {
        var text = ShareTransferAddressBox.Text.Trim();
        if (!ConfigSyncPolicy.TryParseAddress(text, out var host, out var port))
        {
            Info($"地址格式不正确。请输入对方的 IP 或 IP:端口，例如 192.168.1.20:{ConfigSyncPolicy.DefaultPort}。", "匹配设备");
            return;
        }
        var address = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        await RunShareActionAsync("匹配设备", () => MatchDeviceAsync(address, null));
    }

    private async Task MatchDeviceAsync(string address, string? advertisedPrefix)
    {
        var hello = await _configSubscriptions.HelloAsync(address);
        var prefix = ConfigSyncPolicy.FingerprintPrefix(hello.Fingerprint);
        if (advertisedPrefix is not null && !prefix.Equals(advertisedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ConfigSyncException(ConfigSyncError.PinMismatch,
                "该设备出示的证书指纹与它广播的指纹不一致，已停止匹配。请确认地址是否正确，或稍后重试。");
        if (prefix.Equals(_shareFingerprintPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Info("这是本机的地址，不能和自己匹配。", "匹配设备");
            return;
        }
        var dialog = new SharePairDialog(this, hello.DeviceName, address, prefix);
        if (WithModal(() => dialog.ShowDialog()) != true) return;
        var peer = await _lanDevices.MatchAsync(address, dialog.Code);
        ShareTransferAddressBox.Clear();
        ShowTransferPage(TransferPage.Home);
        await RefreshShareAsync();
        Info($"已与“{peer.Name}”匹配。之后双方在同一局域网内会自动连接，可以互相发送文件、订阅配置。", "匹配设备");
    }

    // ---- 本机匹配码与接收设置 ----

    private async void ShareKeepOnlineCheck_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ShareKeepOnlineCheck.IsChecked == true;
        await RunShareActionAsync("保持可被发现", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            await _settingsStore.SaveAsync(settings with { LanKeepOnline = enabled });
            await _configSync.SetKeepOnlineAsync();
            await RefreshShareAsync();
        });
    }

    private async void ShareReceiveCheck_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ShareReceiveCheck.IsChecked == true;
        await RunShareActionAsync("接收文件", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            await _settingsStore.SaveAsync(settings with { LanReceiveEnabled = enabled });
            await RefreshShareAsync();
        });
    }

    private async void ShareAutoAcceptCheck_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ShareAutoAcceptCheck.IsChecked == true;
        await RunShareActionAsync("直接接收", async () =>
        {
            if (enabled && !Confirm("不询问，直接接收",
                    "打开后，已匹配设备发来的文件会直接保存到保存位置，不再逐个询问。\n\n收到的文件仍不会自动打开；可执行类文件请确认来源后再手动打开。",
                    "打开", null, out _))
            {
                ShareAutoAcceptCheck.IsChecked = false;
                return;
            }
            var settings = await _settingsStore.LoadAsync();
            await _settingsStore.SaveAsync(settings with { LanAutoAccept = enabled });
            await RefreshShareAsync();
        });
    }

    private async void ShareOpenReceiveFolderButton_Click(object sender, RoutedEventArgs e) =>
        await RunShareActionAsync("打开保存位置", async () =>
        {
            var folder = LanTransferPaths.ResolveReceiveFolder(await _settingsStore.LoadAsync());
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        });

    private async void ShareChangeReceiveFolderButton_Click(object sender, RoutedEventArgs e) =>
        await RunShareActionAsync("更改保存位置", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            var current = LanTransferPaths.ResolveReceiveFolder(settings);
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择收到的文件保存到哪里",
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (WithModal(() => dialog.ShowDialog(this)) != true) return;
            var chosen = dialog.FolderName;
            // 选回默认位置时存空值，之后默认位置变化（如换用户目录）也能跟上。
            var value = string.Equals(Path.GetFullPath(chosen).TrimEnd('\\'), Path.GetFullPath(LanTransferPaths.DefaultReceiveFolder).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase) ? string.Empty : chosen;
            await _settingsStore.SaveAsync(settings with { LanReceiveFolder = value });
            await RefreshShareAsync();
        });

    // ---- 发送 ----

    private async void SendFilesToDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<MatchedDeviceRowViewModel>(sender) is not { } row) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择要发给“{row.Name}”的文件",
            Multiselect = true,
            CheckFileExists = true
        };
        if (WithModal(() => dialog.ShowDialog(this)) != true || dialog.FileNames.Length == 0) return;
        await Task.Yield();
        StartSend(row.Device, dialog.FileNames, null);
    }

    /// <summary>文件夹会先打包成“文件夹名.zip”再发送，对方收到的是一个 zip。</summary>
    private async void SendFolderToDevice_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<MatchedDeviceRowViewModel>(sender) is not { } row) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"选择要发给“{row.Name}”的文件夹", Multiselect = true };
        if (WithModal(() => dialog.ShowDialog(this)) != true || dialog.FolderNames.Length == 0) return;
        await Task.Yield();
        StartSend(row.Device, dialog.FolderNames, null);
    }

    /// <summary>历史页右键“发送给已匹配设备…”：文件/图片发原文件，文本与链接存成临时 .txt 发送后删除。</summary>
    private async Task SendHistoryItemToDeviceAsync(ClipboardItem item)
    {
        var devices = await _lanDevices.GetDevicesAsync();
        if (devices.Count == 0)
        {
            Info("还没有已匹配的设备。请先在“共享 → 传输”输入对方的匹配码完成匹配。", "发送给已匹配设备");
            return;
        }
        var lines = devices.Select(device => $"{device.Name}  ·  {(device.IsOnline ? "在线" : "未在线")}").ToList();
        var chooser = new ShareListDialog(this, "发送给已匹配设备", "选择接收的设备：", lines, "发送");
        if (WithModal(() => chooser.ShowDialog()) != true || chooser.SelectedIndex < 0) return;
        var target = devices[chooser.SelectedIndex];

        string? temporaryFolder = null;
        IReadOnlyList<string> paths;
        switch (item.ItemType)
        {
            case ClipboardItemType.Files or ClipboardItemType.ImageFile:
                // 文件夹会先打包成 zip 再发送。
                paths = HistoryItemViewModel.ReadFiles(item).Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
                if (paths.Count == 0)
                {
                    Info("这些文件已不存在，无法发送。", "发送给已匹配设备");
                    return;
                }
                break;
            case ClipboardItemType.Image:
                if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
                {
                    Info("这张图片的缓存已不存在，无法发送。", "发送给已匹配设备");
                    return;
                }
                paths = [item.ImagePath];
                break;
            default:
                if (string.IsNullOrEmpty(item.TextContent))
                {
                    Info("这条记录没有可发送的内容。", "发送给已匹配设备");
                    return;
                }
                temporaryFolder = Path.Combine(Path.GetTempPath(), "ClipboardManager", "LanSend", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryFolder);
                var file = Path.Combine(temporaryFolder, $"剪切板文本 {DateTime.Now:yyyyMMdd-HHmmss}.txt");
                await File.WriteAllTextAsync(file, item.TextContent, new System.Text.UTF8Encoding(true));
                paths = [file];
                break;
        }

        var folderToDelete = temporaryFolder;
        StartSend(target, paths, folderToDelete is null ? null : () => TryDeleteFolder(folderToDelete));
        // 切到“共享 → 传输”这台设备的页面，让用户看到等待确认与进度。
        MainTabs.SelectedItem = ShareTab;
        await SwitchShareSegmentAsync(ShareSegment.Transfer);
        if (MatchedDeviceRows.FirstOrDefault(row => row.Device.Fingerprint.Equals(target.Fingerprint, StringComparison.OrdinalIgnoreCase)) is { } targetRow)
            ShowTransferPage(TransferPage.Device, targetRow);
    }

    // ---- 拖放发送 ----

    /// <summary>拖放目标：设备页就是这台设备；一级页是指针下的设备行，只有一台设备时也可以拖到页面任意位置。</summary>
    private MatchedDeviceRowViewModel? TransferDropTarget(System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return null;
        return _transferPage switch
        {
            TransferPage.Device => TransferDevicePanel.DataContext as MatchedDeviceRowViewModel,
            TransferPage.Home => IsWithin(e.OriginalSource as DependencyObject, MatchedDeviceList)
                ? FindItem<MatchedDeviceRowViewModel>(e.OriginalSource as DependencyObject)
                : MatchedDeviceRows.Count == 1 ? MatchedDeviceRows[0] : null,
            _ => null
        };
    }

    private void TransferDragOver(System.Windows.DragEventArgs e)
    {
        var target = TransferDropTarget(e);
        SetTransferDropTarget(target);
        e.Effects = target is null ? System.Windows.DragDropEffects.None : System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // 在子元素之间移动也会冒泡 DragLeave；指针仍在窗口里就保留高亮，由下一次 DragOver 决定。
        var point = e.GetPosition(this);
        if (point.X > 0 && point.Y > 0 && point.X < ActualWidth && point.Y < ActualHeight) return;
        SetTransferDropTarget(null);
    }

    private void SetTransferDropTarget(MatchedDeviceRowViewModel? target)
    {
        foreach (var row in MatchedDeviceRows) row.IsDropTarget = ReferenceEquals(row, target);
    }

    private async Task TransferDropAsync(System.Windows.DragEventArgs e)
    {
        var target = TransferDropTarget(e);
        SetTransferDropTarget(null);
        e.Handled = true;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        if (target is null)
        {
            if (_transferPage == TransferPage.Home && MatchedDeviceRows.Count > 1)
                Info("请把文件拖到要发送的那台设备上。", "发送文件");
            return;
        }
        var files = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (files.Count == 0) return;
        await Task.Yield();
        StartSend(target.Device, files, null);
        if (_transferPage == TransferPage.Home) ShowTransferPage(TransferPage.Device, target);
    }

    /// <summary>后台发送；进度显示在传输记录里，结束后只在有失败或跳过时提示。</summary>
    private void StartSend(MatchedDevice device, IReadOnlyList<string> paths, Action? cleanup)
    {
        _ = Task.Run(async () =>
        {
            FileSendSummary? summary = null;
            string? error = null;
            try { summary = await _lanTransfer.SendFilesAsync(device.Fingerprint, paths); }
            catch (Exception exception) when (exception is FileTransferException or ConfigSyncException or IOException)
            {
                error = exception.Message;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"发送文件失败: {exception}");
                error = "发送失败：" + exception.Message;
            }
            finally
            {
                cleanup?.Invoke();
            }
            if (error is null && summary is { Failed: 0, Skipped: 0 }) return;
            var message = error ?? SendSummaryText(device.Name, summary!);
            await Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(this, message, "发送文件", MessageBoxButton.OK, MessageBoxImage.Warning));
        });
    }

    private static string SendSummaryText(string deviceName, FileSendSummary summary)
    {
        var text = $"发给“{deviceName}”：成功 {summary.Sent} 个";
        if (summary.Failed > 0) text += $"，失败 {summary.Failed} 个";
        if (summary.Skipped > 0) text += $"，跳过 {summary.Skipped} 个（已不存在）";
        text += "。";
        if (summary.LastError is { } reason) text += $"\n\n{reason}";
        return text;
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"删除临时发送目录失败: {exception.GetType().Name}");
        }
    }

    // ---- 传输记录 ----

    private void OnTransfersChanged()
    {
        if (Interlocked.Exchange(ref _transferRefreshQueued, 1) == 1) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _transferRefreshQueued, 0);
            SyncTransferRows();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 原地更新：进度变化只改行的属性，不重建列表，避免打断正在点的“取消”。新任务插到最上面。
    /// 传输记录只列当前设备页的那台设备；每个设备行显示自己的最近状态。
    /// </summary>
    private void SyncTransferRows()
    {
        var all = _lanTransfer.Tracker.GetAll();
        foreach (var device in MatchedDeviceRows)
            device.UpdateActivity(all.Where(snapshot => snapshot.PeerFingerprint.Equals(device.Device.Fingerprint, StringComparison.OrdinalIgnoreCase)).ToList());
        var snapshots = _transferDeviceFingerprint is { } fingerprint
            ? all.Where(snapshot => snapshot.PeerFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        var byId = snapshots.ToDictionary(snapshot => snapshot.Id);
        for (var index = TransferRows.Count - 1; index >= 0; index--)
        {
            if (byId.TryGetValue(TransferRows[index].Id, out var snapshot)) TransferRows[index].Update(snapshot);
            else TransferRows.RemoveAt(index);
        }
        var known = TransferRows.Select(row => row.Id).ToHashSet();
        foreach (var snapshot in snapshots.Where(snapshot => !known.Contains(snapshot.Id)).Reverse())
            TransferRows.Insert(0, new TransferRowViewModel(snapshot));
        ShareTransferEmptyText.Visibility = VisibleIf(TransferRows.Count == 0);
        var anyFinished = TransferRows.Any(row => row.Snapshot.IsFinished);
        ShareClearTransfersDivider.Visibility = VisibleIf(anyFinished);
        ShareClearTransfersRow.Visibility = VisibleIf(anyFinished);
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<TransferRowViewModel>(sender) is { } row) _lanTransfer.Tracker.Cancel(row.Id);
    }

    private async void RevealTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf<TransferRowViewModel>(sender)?.Snapshot.SavedPath is not { } path) return;
        await RunShareActionAsync("打开位置", () =>
        {
            OpenFile(path, true);
            return Task.CompletedTask;
        });
    }

    private void ShareClearTransfersButton_Click(object sender, RoutedEventArgs e) => _lanTransfer.Tracker.ClearFinished(_transferDeviceFingerprint);
}
