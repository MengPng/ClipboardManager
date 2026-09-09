using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipboardManager.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ClipboardHistoryService _historyService;
    private readonly ClipboardRestoreService _restoreService;
    private readonly ClipboardMonitorService _monitorService;
    private readonly ClipboardCaptureService _captureService;
    private readonly ClipboardPayloadCaptureService _payloadCaptureService;
    private readonly CollectionService _collectionService;
    private readonly CollectionRestoreService _collectionRestoreService;
    private readonly AutomaticPasteService _automaticPasteService;
    private readonly ClipboardManager.Domain.Contracts.IAppSettingsStore _settingsStore;
    private readonly ClipboardManager.Domain.Contracts.IStorageMaintenanceService _storageMaintenance;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer;
    private bool _allowClose, _favoritesOnly, _pinnedOnly, _refreshingFolders, _isPinnedTopmost;
    private int _activeModalCount;
    private ClipboardItemType? _itemType;
    private HistoryItemViewModel? _selectedItem;
    private FolderViewModel? _selectedFolder;
    private CollectionItemViewModel? _selectedCollectionItem;
    private System.Windows.Point _dragStart;
    private object? _dragItem;
    private long _historyVersion, _collectionVersion, _foldersVersion;
    private CancellationTokenSource? _historySearch, _collectionSearch;

    public MainWindow(ClipboardHistoryService historyService, ClipboardRestoreService restoreService,
        ClipboardMonitorService monitorService, ClipboardCaptureService captureService,
        ClipboardPayloadCaptureService payloadCaptureService, CollectionService collectionService,
        CollectionRestoreService collectionRestoreService, AutomaticPasteService automaticPasteService,
        ClipboardManager.Domain.Contracts.IAppSettingsStore settingsStore,
        ClipboardManager.Domain.Contracts.IStorageMaintenanceService storageMaintenance)
    {
        _historyService = historyService; _restoreService = restoreService; _monitorService = monitorService;
        _captureService = captureService; _payloadCaptureService = payloadCaptureService;
        _collectionService = collectionService; _collectionRestoreService = collectionRestoreService;
        _automaticPasteService = automaticPasteService; _settingsStore = settingsStore; _storageMaintenance = storageMaintenance;
        DataContext = this;
        InitializeComponent();
        var pauseMenu = new ContextMenu();
        AddMenu(pauseMenu, "暂停 5 分钟", () => _monitorService.PauseForAsync(TimeSpan.FromMinutes(5)));
        AddMenu(pauseMenu, "暂停 30 分钟", () => _monitorService.PauseForAsync(TimeSpan.FromMinutes(30)));
        AddMenu(pauseMenu, "手动暂停（直到继续）", () => _monitorService.SetPausedAsync(true));
        AddMenu(pauseMenu, "继续监听", () => _monitorService.SetPausedAsync(false));
        PauseButton.ContextMenu = pauseMenu;
        PauseButton.ToolTip = "点击暂停/继续；右键选择定时暂停";
        _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RunActionAsync("刷新", () => MainTabs.SelectedIndex == 0 ? RefreshHistoryAsync() : RefreshCollectionItemsAsync());
        };
        _captureService.ItemCaptured += (_, _) => Dispatcher.InvokeAsync(async () => await RunActionAsync("刷新历史", () => RefreshHistoryAsync()));
        _payloadCaptureService.ItemCaptured += (_, _) => Dispatcher.InvokeAsync(async () => await RunActionAsync("刷新历史", () => RefreshHistoryAsync()));
        _monitorService.PauseChanged += (_, paused) => Dispatcher.Invoke(() => UpdatePauseState(paused));
        Deactivated += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                if (_isPinnedTopmost || _activeModalCount > 0) return;
                if ((await _settingsStore.LoadAsync()).AutoHideWhenDeactivated && IsVisible &&
                    !OwnedWindows.Cast<Window>().Any(window => window.IsActive)) Hide();
            }
            catch { /* A settings read failure must not crash the desktop shell. */ }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
                (e.Key is not (Key.Up or Key.Down)) || Keyboard.Modifiers != ModifierKeys.None) return;
            var list = MainTabs.SelectedIndex == 0 ? HistoryList : CollectionList;
            if (list.Items.Count == 0) return;
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
            list.ScrollIntoView(list.SelectedItem);
            e.Handled = true;
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragItem = (object?)FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject)
                ?? FindItem<FolderViewModel>(e.OriginalSource as DependencyObject);

            var historyHit = FindItem<HistoryItemViewModel>(e.OriginalSource as DependencyObject);
            if (historyHit is not null && MainTabs.SelectedIndex == 0)
            {
                SelectedItem = historyHit;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    var settings = await _settingsStore.LoadAsync();
                    if (settings.SingleClickToPaste)
                    {
                        await RunActionAsync("单击使用历史", () => RestoreSelectedAsync(true));
                    }
                });
                return;
            }

            var collectionHit = FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject);
            if (collectionHit is not null && MainTabs.SelectedIndex == 1)
            {
                SelectedCollectionItem = collectionHit;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    var settings = await _settingsStore.LoadAsync();
                    if (settings.SingleClickToPaste)
                    {
                        await RunActionAsync("单击使用收藏", () => UseCollectionSelectedAsync(true));
                    }
                });
                return;
            }
        };
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];
    public ObservableCollection<FolderViewModel> Folders { get; } = [];
    public ObservableCollection<CollectionItemViewModel> CollectionItems { get; } = [];
    public HistoryItemViewModel? SelectedItem { get => _selectedItem; set { _selectedItem = value; Changed(nameof(SelectedItem)); } }
    public FolderViewModel? SelectedFolder { get => _selectedFolder; set { _selectedFolder = value; Changed(nameof(SelectedFolder)); } }
    public CollectionItemViewModel? SelectedCollectionItem { get => _selectedCollectionItem; set { _selectedCollectionItem = value; Changed(nameof(SelectedCollectionItem)); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync(BootstrapContext context, CancellationToken cancellationToken = default)
    {
        WorkspaceText.Text = $"Workspace：{context.Workspace.Name} · {context.Device.DeviceName}";
        UpdatePauseState(CapturePrivacyPolicy.IsPaused(context.Settings, DateTimeOffset.UtcNow));
        UpdatePinVisualState(context.Settings.AlwaysOnTop);
        await RefreshHistoryAsync(cancellationToken);
        await RefreshFoldersAsync(cancellationToken);
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var version = ++_historyVersion;
        _historySearch?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _historySearch = request;
        var selectedId = SelectedItem?.Item.Id;
        var search = SearchBox.Text;
        try
        {
            var records = await _historyService.SearchAsync(search, _favoritesOnly, _pinnedOnly, _itemType, cancellationToken: request.Token);
            if (request.IsCancellationRequested || version != _historyVersion) return;
            Items.Clear(); foreach (var item in records) Items.Add(new HistoryItemViewModel(item));
            SelectedItem = Items.FirstOrDefault(x => x.Item.Id == selectedId) ?? Items.FirstOrDefault();
            if (MainTabs.SelectedIndex == 0) CountText.Text = $"{Items.Count} 条历史";
            EmptyStateText.Text = string.IsNullOrWhiteSpace(search) ? (_monitorService.IsPaused ? "监听已暂停" : "还没有历史，复制内容试试") : "没有匹配的记录";
            EmptyStateText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) when (version != _historyVersion) { /* A superseded query must not show stale errors. */ }
        finally { if (ReferenceEquals(_historySearch, request)) _historySearch = null; }
    }

    private async Task RefreshFoldersAsync(CancellationToken cancellationToken = default)
    {
        var version = ++_foldersVersion;
        var result = await _collectionService.GetFoldersAsync(cancellationToken);
        var folders = new List<FolderViewModel>();
        foreach (var folder in result.Folders)
            folders.Add(new FolderViewModel(folder, (await _collectionService.GetItemsAsync(folder.Id, cancellationToken: cancellationToken)).Count));
        if (version != _foldersVersion) return;
        var selectedId = SelectedFolder?.Folder.Id;
        _refreshingFolders = true;
        try
        {
            Folders.Clear(); foreach (var folder in folders) Folders.Add(folder);
            SelectedFolder = Folders.FirstOrDefault(x => x.Folder.Id == selectedId) ?? Folders.FirstOrDefault(x => x.Folder.Id == result.Uncategorized.Id) ?? Folders.FirstOrDefault();
        }
        finally { _refreshingFolders = false; }
        await RefreshCollectionItemsAsync(cancellationToken);
    }

    private async Task RefreshCollectionItemsAsync(CancellationToken cancellationToken = default)
    {
        var version = ++_collectionVersion;
        _collectionSearch?.Cancel();
        if (SelectedFolder is null) { CollectionItems.Clear(); SelectedCollectionItem = null; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _collectionSearch = request;
        var folderId = SelectedFolder.Folder.Id;
        var selectedId = SelectedCollectionItem?.Item.Id;
        try
        {
            var records = await _collectionService.GetItemsAsync(folderId, CollectionSearchBox.Text, request.Token);
            if (request.IsCancellationRequested || version != _collectionVersion || SelectedFolder?.Folder.Id != folderId) return;
            CollectionItems.Clear(); foreach (var item in records) CollectionItems.Add(new CollectionItemViewModel(item));
            SelectedCollectionItem = CollectionItems.FirstOrDefault(x => x.Item.Id == selectedId) ?? CollectionItems.FirstOrDefault();
            if (MainTabs.SelectedIndex == 1) CountText.Text = $"{CollectionItems.Count} 条收藏";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) when (version != _collectionVersion) { }
        finally { if (ReferenceEquals(_collectionSearch, request)) _collectionSearch = null; }
    }

    public void RememberPasteTarget() => _automaticPasteService.RememberCurrentTarget();

    protected override void OnClosing(CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; Hide(); } base.OnClosing(e); }
    protected override void OnClosed(EventArgs e) { _searchTimer.Stop(); _historySearch?.Cancel(); _collectionSearch?.Cancel(); base.OnClosed(e); }

    protected override async void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled) { base.OnKeyDown(e); return; }
        var editing = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        { (MainTabs.SelectedIndex == 0 ? SearchBox : CollectionSearchBox).Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        { var box = MainTabs.SelectedIndex == 0 ? SearchBox : CollectionSearchBox; if (!string.IsNullOrEmpty(box.Text)) box.Clear(); else Hide(); e.Handled = true; }
        else if (!editing)
        {
            Func<Task>? action = null;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D && MainTabs.SelectedIndex == 0) action = () => CollectSelectedAsync();
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P && MainTabs.SelectedIndex == 0) action = TogglePinnedAsync;
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter) action = MainTabs.SelectedIndex == 0 ? () => RestoreSelectedAsync(true) : () => UseCollectionSelectedAsync(true);
            else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Enter) action = MainTabs.SelectedIndex == 0 ? () => RestoreSelectedAsync(false) : () => UseCollectionSelectedAsync(false);
            else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete) action = MainTabs.SelectedIndex == 0 ? DeleteSelectedAsync : DeleteCollectionSelectedAsync;
            if (action is not null) { e.Handled = true; await RunActionAsync("记录操作", action); }
        }
        base.OnKeyDown(e);
    }

    public void AllowClose() => _allowClose = true;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    private async Task RunActionAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, $"{name}失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { ++_historyVersion; _historySearch?.Cancel(); RestartSearchTimer(); }
    private void CollectionSearchBox_TextChanged(object sender, TextChangedEventArgs e) { ++_collectionVersion; _collectionSearch?.Cancel(); RestartSearchTimer(); }
    private void RestartSearchTimer() { if (_searchTimer is null) return; _searchTimer.Stop(); _searchTimer.Start(); }

    private async void AllFilterButton_Click(object sender, RoutedEventArgs e) => await FilterAsync(null, false, false);
    private async void TextFilterButton_Click(object sender, RoutedEventArgs e) => await FilterAsync(ClipboardItemType.Text, false, false);
    private async void ImageFilterButton_Click(object sender, RoutedEventArgs e) => await FilterAsync(ClipboardItemType.Image, false, false);
    private async void FileFilterButton_Click(object sender, RoutedEventArgs e) => await FilterAsync(ClipboardItemType.Files, false, false);
    private async void PinnedFilterButton_Click(object sender, RoutedEventArgs e) => await FilterAsync(null, false, true);
    private async void FavoriteFilterButton_Click(object sender, RoutedEventArgs e) => await FilterAsync(null, true, false);
    private async Task FilterAsync(ClipboardItemType? type, bool favorite, bool pinned)
    {
        _itemType = type; _favoritesOnly = favorite; _pinnedOnly = pinned;
        foreach (var b in new[] { AllFilterButton, TextFilterButton, ImageFilterButton, FileFilterButton, FavoriteFilterButton, PinnedFilterButton }) b.FontWeight = FontWeights.Normal;
        (pinned ? PinnedFilterButton : favorite ? FavoriteFilterButton : type == ClipboardItemType.Text ? TextFilterButton : type == ClipboardItemType.Image ? ImageFilterButton : type == ClipboardItemType.Files ? FileFilterButton : AllFilterButton).FontWeight = FontWeights.Bold;
        await RunActionAsync("筛选", () => RefreshHistoryAsync());
    }
    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("切换监听", async () => { await _monitorService.SetPausedAsync(!_monitorService.IsPaused); await RefreshHistoryAsync(); });
    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedTopmost = !_isPinnedTopmost;
        UpdatePinVisualState(_isPinnedTopmost);
        try
        {
            var settings = await _settingsStore.LoadAsync();
            if (settings.AlwaysOnTop != _isPinnedTopmost)
            {
                await _settingsStore.SaveAsync(settings with { AlwaysOnTop = _isPinnedTopmost });
            }
        }
        catch { /* A settings write failure must not disrupt UI toggle. */ }
    }

    private void UpdatePinVisualState(bool pinned)
    {
        _isPinnedTopmost = pinned;
        Topmost = pinned;
        if (pinned)
        {
            PinButton.Content = "📌 已常驻";
            PinButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xF8, 0xE6));
            PinButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D));
            PinButton.ToolTip = "当前状态：已常驻前台（窗口始终置顶且打开其他应用时不隐藏）。点击取消常驻。";
        }
        else
        {
            PinButton.Content = "📌 钉住";
            PinButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            PinButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            PinButton.ToolTip = "常驻前台：窗口始终置顶且打开其他应用时不隐藏。点击开启常驻。";
        }
    }

    private T WithModal<T>(Func<T> action)
    {
        Interlocked.Increment(ref _activeModalCount);
        try { return action(); }
        finally { Interlocked.Decrement(ref _activeModalCount); }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settingsStore, _storageMaintenance) { Owner = this };
        if (WithModal(() => dialog.ShowDialog()) == true) await RunActionAsync("应用设置", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            UpdatePauseState(CapturePrivacyPolicy.IsPaused(settings, DateTimeOffset.UtcNow));
            UpdatePinVisualState(settings.AlwaysOnTop);
            await RefreshHistoryAsync();
        });
    }
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void CollectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (FindItem<HistoryItemViewModel>(e.OriginalSource as DependencyObject) is not null) await RunActionAsync("使用历史", () => RestoreSelectedAsync(true)); }
    private async Task RestoreSelectedAsync(bool automaticPaste)
    {
        if (SelectedItem is null) return;
        await _restoreService.RestoreAsync(SelectedItem.Item);
        automaticPaste |= (await _settingsStore.LoadAsync()).EnableAutomaticPaste;
        if (!_isPinnedTopmost) Hide();
        if (automaticPaste && !await _automaticPasteService.TryPasteAsync(this))
            System.Windows.MessageBox.Show("内容已复制到剪切板，但未能自动粘贴，请手动按 Ctrl+V。", "自动粘贴", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task DeleteSelectedAsync() { if (SelectedItem is null) return; await _historyService.DeleteAsync(SelectedItem.Item.Id); await RefreshHistoryAsync(); }
    private async Task TogglePinnedAsync() { if (SelectedItem is null) return; await _historyService.SetPinnedAsync(SelectedItem.Item.Id, !SelectedItem.Item.IsPinned); await RefreshHistoryAsync(); }
    private async void CollectSelectedButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("收藏", () => CollectSelectedAsync(true));
    private async Task CollectSelectedAsync(bool chooseFolder = false)
    {
        var item = SelectedItem?.Item;
        if (item is null) return;
        if (Folders.Count == 0) await RefreshFoldersAsync();
        var folder = chooseFolder ? ChooseFolder() : Folders.FirstOrDefault(x => x.Folder.IsSystem);
        if (folder is null) return;
        await _collectionService.AddSnapshotAsync(folder.Folder.Id, item);
        await RefreshFoldersAsync();
    }
    private FolderViewModel? ChooseFolder()
    {
        return WithModal(() =>
        {
            var dialog = new FolderSelectionDialog(this, Folders.ToList());
            return dialog.ShowDialog() == true ? dialog.SelectedFolder : null;
        });
    }
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!IsLoaded || !ReferenceEquals(e.OriginalSource, MainTabs)) return; await RunActionAsync("切换视图", () => MainTabs.SelectedIndex == 1 ? RefreshFoldersAsync() : RefreshHistoryAsync()); }
    private async void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (IsLoaded && !_refreshingFolders) await RunActionAsync("读取文件夹", () => RefreshCollectionItemsAsync()); }
    private async void NewFolderButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("新建文件夹", async () =>
    {
        string? name = WithModal(() =>
        {
            var dialog = new SingleInputDialog(this, "新建文件夹", "请输入文件夹名称：");
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });
        if (!string.IsNullOrWhiteSpace(name)) { await _collectionService.CreateFolderAsync(name); await RefreshFoldersAsync(); }
    });
    private async void RenameFolderButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("重命名", async () =>
    {
        var folder = SelectedFolder; if (folder is null || folder.Folder.IsSystem) return;
        string? name = WithModal(() =>
        {
            var dialog = new SingleInputDialog(this, "重命名文件夹", "请输入新名称：", folder.Name);
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });
        if (!string.IsNullOrWhiteSpace(name)) { await _collectionService.RenameFolderAsync(folder.Folder.Id, name); await RefreshFoldersAsync(); }
    });
    private async void DeleteFolderButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("删除文件夹", async () =>
    {
        var folder = SelectedFolder; if (folder is null || folder.Folder.IsSystem) return;
        var confirm = WithModal(() => System.Windows.MessageBox.Show(this, "删除文件夹后，其中内容将移动到“未分类”。继续吗？", "删除文件夹", MessageBoxButton.YesNo, MessageBoxImage.Question));
        if (confirm == MessageBoxResult.Yes) { await _collectionService.DeleteFolderAsync(folder.Folder.Id); await RefreshFoldersAsync(); }
    });

    private void AddCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is null) return;
        var folderId = SelectedFolder.Folder.Id;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        AddMenu(menu, "添加文本", () => EditContentAsync(folderId, CollectionItemType.Text));
        AddMenu(menu, "添加链接", () => EditContentAsync(folderId, CollectionItemType.Link));
        AddMenu(menu, "添加图片", async () => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" }; if (WithModal(() => dialog.ShowDialog(this)) == true) { await _collectionService.AddImageAsync(folderId, dialog.FileName); await RefreshFoldersAsync(); } });
        AddMenu(menu, "添加文件", async () => { var dialog = new Microsoft.Win32.OpenFileDialog(); if (WithModal(() => dialog.ShowDialog(this)) == true) { await _collectionService.AddFileAsync(folderId, dialog.FileName); await RefreshFoldersAsync(); } });
        menu.IsOpen = true;
    }
    private async Task EditContentAsync(Guid folderId, CollectionItemType type, CollectionItem? item = null)
    {
        var dialog = new CollectionContentDialog(this, type, item?.Title ?? "", item is null ? "" : type == CollectionItemType.Link ? item.Url ?? "" : item.TextContent ?? "",
            async (title, content) =>
            {
                if (item is not null) await _collectionService.UpdateContentAsync(item, title, content);
                else if (type == CollectionItemType.Link) await _collectionService.AddLinkAsync(folderId, title, content.Trim());
                else await _collectionService.AddTextAsync(folderId, title, content);
            }, item is not null);
        if (WithModal(() => dialog.ShowDialog()) == true) await RefreshFoldersAsync();
    }
    private void AddMenu(ItemsControl menu, string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title };
        item.Click += async (_, e) => { e.Handled = true; await RunActionAsync(title, action); };
        menu.Items.Add(item);
    }
    private void CollectionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject);
        if (hit is not null) SelectedCollectionItem = hit;
        var item = SelectedCollectionItem?.Item;
        if (item is null) { e.Handled = true; return; }
        var menu = new ContextMenu();
        AddMenu(menu, "复制到剪切板", async () => { await _collectionRestoreService.UseAsync(item); });
        if (item.ItemType is CollectionItemType.Text or CollectionItemType.Link)
            AddMenu(menu, "编辑…", () => EditContentAsync(item.FolderId, item.ItemType, item));
        if (item.ItemType == CollectionItemType.Link)
            AddMenu(menu, "在浏览器打开", () => { _collectionRestoreService.OpenLink(item); return Task.CompletedTask; });
        var move = new MenuItem { Header = "移动到文件夹" };
        foreach (var folder in Folders.Where(f => f.Folder.Id != item.FolderId))
            AddMenu(move, folder.Name, () => MoveToFolderAsync(item, folder.Folder.Id));
        move.IsEnabled = move.Items.Count > 0; menu.Items.Add(move);
        if (item.ItemType == CollectionItemType.Image) AddMenu(menu, "图片另存为…", () => SaveImageAsync(item.LocalPath));
        if (item.ItemType == CollectionItemType.File) AddFileMenus(menu, item.FileManifest, item.LocalPath);
        AddMenu(menu, "删除收藏", async () => { await _collectionService.DeleteItemAsync(item.Id); await RefreshFoldersAsync(); });
        CollectionList.ContextMenu = menu;
    }
    private void HistoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = FindItem<HistoryItemViewModel>(e.OriginalSource as DependencyObject);
        if (hit is not null) SelectedItem = hit;
        var item = SelectedItem?.Item;
        if (item is null) { e.Handled = true; return; }
        var menu = new ContextMenu();
        AddMenu(menu, "复制到剪切板", () => _restoreService.RestoreAsync(item));
        if (item.ItemType == ClipboardItemType.Image) AddMenu(menu, "图片另存为…", () => SaveImageAsync(item.ImagePath));
        if (item.ItemType == ClipboardItemType.Files) AddFileMenus(menu, item.FileManifest, null);
        HistoryList.ContextMenu = menu;
    }
    private void AddFileMenus(ItemsControl menu, string? manifest, string? localPath)
    {
        // Parse inside the guarded action, not in the routed context-menu event.
        AddMenu(menu, "打开文件…", () => ChooseFileActionAsync(manifest, localPath, false));
        AddMenu(menu, "打开文件位置…", () => ChooseFileActionAsync(manifest, localPath, true));
    }
    private Task ChooseFileActionAsync(string? manifest, string? localPath, bool location)
    {
        var paths = (manifest is null ? new[] { localPath ?? "" } : JsonSerializer.Deserialize<string[]>(manifest) ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("记录不包含可用文件路径。");
        if (paths.Length == 1) OpenFile(paths[0], location);
        else
        {
            var menu = new ContextMenu { PlacementTarget = MainTabs };
            foreach (var path in paths) AddMenu(menu, path, () => { OpenFile(path, location); return Task.CompletedTask; });
            menu.IsOpen = true;
        }
        return Task.CompletedTask;
    }
    private static void OpenFile(string path, bool location)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("文件或目录已不存在。", path);
        if (location)
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,\"{Path.GetFullPath(path)}\"" };
            Process.Start(start);
        }
        else Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
    }
    private async Task SaveImageAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("原始图片已不存在，无法另存为。");
        var extension = Path.GetExtension(path);
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "图片另存为", FileName = Path.GetFileName(path), DefaultExt = extension, Filter = $"原始图片格式 (*{extension})|*{extension}", AddExtension = true, OverwritePrompt = true };
        if (WithModal(() => dialog.ShowDialog(this)) != true) return;
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择不同于原始图片的位置。");
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var target = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(target);
    }
    private async Task MoveToFolderAsync(CollectionItem item, Guid folderId)
    {
        var all = await _collectionService.GetItemsAsync(folderId);
        await _collectionService.MoveItemAsync(item.Id, folderId, all.Count);
        await RefreshFoldersAsync();
    }
    private async void CollectionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject) is not null) await RunActionAsync("使用收藏", () => UseCollectionSelectedAsync(true)); }
    private async Task UseCollectionSelectedAsync(bool automaticPaste)
    {
        if (SelectedCollectionItem is null) return;
        await _collectionRestoreService.UseAsync(SelectedCollectionItem.Item);
        automaticPaste |= (await _settingsStore.LoadAsync()).EnableAutomaticPaste;
        if (!_isPinnedTopmost) Hide();
        if (automaticPaste && !await _automaticPasteService.TryPasteAsync(this))
            System.Windows.MessageBox.Show("内容已复制到剪切板，但未能自动粘贴，请手动按 Ctrl+V。", "自动粘贴", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task DeleteCollectionSelectedAsync() { if (SelectedCollectionItem is null) return; await _collectionService.DeleteItemAsync(SelectedCollectionItem.Item.Id); await RefreshFoldersAsync(); }

    private async void FolderList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed && Moved(e) && _dragItem is FolderViewModel item) { _dragItem = null; await RunActionAsync("拖动文件夹", () => { DragDrop.DoDragDrop(FolderList, item, System.Windows.DragDropEffects.Move); return Task.CompletedTask; }); } }
    private async void FolderList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var target = FindItem<FolderViewModel>(e.OriginalSource as DependencyObject);
        if (e.Data.GetData(typeof(CollectionItemViewModel)) is CollectionItemViewModel content && target is not null)
        { e.Handled = true; await RunActionAsync("移动收藏", () => MoveToFolderAsync(content.Item, target.Folder.Id)); }
        else if (e.Data.GetData(typeof(FolderViewModel)) is FolderViewModel source)
        { e.Handled = true; await RunActionAsync("文件夹排序", async () => { await _collectionService.ReorderFolderAsync(source.Folder.Id, target is null ? Folders.Count - 1 : Folders.IndexOf(target)); await RefreshFoldersAsync(); }); }
    }
    private async void CollectionList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed && Moved(e) && _dragItem is CollectionItemViewModel item) { _dragItem = null; await RunActionAsync("拖动收藏", () => { DragDrop.DoDragDrop(CollectionList, item, System.Windows.DragDropEffects.Move); return Task.CompletedTask; }); } }
    private async void CollectionList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (SelectedFolder is null || e.Data.GetData(typeof(CollectionItemViewModel)) is not CollectionItemViewModel source) return;
        e.Handled = true;
        var folderId = SelectedFolder.Folder.Id;
        var target = FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject);
        await RunActionAsync("收藏排序", async () =>
        {
            // Resolve against the full folder, never the filtered search-result index.
            var all = (await _collectionService.GetItemsAsync(folderId)).Where(i => i.Id != source.Item.Id).ToList();
            if (target?.Item.Id == source.Item.Id) return;
            var index = target is null ? all.Count : all.FindIndex(i => i.Id == target.Item.Id);
            await _collectionService.MoveItemAsync(source.Item.Id, folderId, index < 0 ? all.Count : index);
            await RefreshFoldersAsync();
        });
    }
    private bool Moved(System.Windows.Input.MouseEventArgs e) { var p = e.GetPosition(this); return Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance; }
    private static T? FindItem<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is T item) return item;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var overFolder = IsWithin(e.OriginalSource as DependencyObject, FolderList);
        var overItems = IsWithin(e.OriginalSource as DependencyObject, CollectionList);
        var internalMove = MainTabs.SelectedIndex == 1 &&
            ((e.Data.GetDataPresent(typeof(FolderViewModel)) && overFolder) ||
             (e.Data.GetDataPresent(typeof(CollectionItemViewModel)) && ((overFolder && FindItem<FolderViewModel>(e.OriginalSource as DependencyObject) is not null) || (overItems && SelectedFolder is not null))));
        e.Effects = internalMove ? System.Windows.DragDropEffects.Move :
            MainTabs.SelectedIndex == 1 && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && SelectedFolder is not null ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, target)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled || MainTabs.SelectedIndex != 1 || SelectedFolder is null || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        var folderId = FindItem<FolderViewModel>(e.OriginalSource as DependencyObject)?.Folder.Id ?? SelectedFolder.Folder.Id;
        foreach (var path in paths)
            await RunActionAsync("导入文件", async () =>
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(ext)) await _collectionService.AddImageAsync(folderId, path);
                else await _collectionService.AddFileAsync(folderId, path);
            });
        await RunActionAsync("刷新收藏", () => RefreshFoldersAsync());
    }
    private void UpdatePauseState(bool paused) { ListeningStatusText.Text = paused ? "● 已暂停" : "● 正在监听"; ListeningStatusText.Foreground = new SolidColorBrush(paused ? System.Windows.Media.Color.FromRgb(180,83,9) : System.Windows.Media.Color.FromRgb(21,128,61)); PauseButton.Content = paused ? "继续" : "暂停"; }
}

public sealed class HistoryItemViewModel(ClipboardItem item)
{
    private string[]? _files;
    private string? _metadata;
    private string? _typeText;
    private ImageSource? _preview;
    private bool _previewResolved;

    public ClipboardItem Item { get; } = item;
    public string PreviewText => Item.ItemType switch
    {
        ClipboardItemType.Image => TypeText,
        ClipboardItemType.Files => Files.Length switch
        {
            0 => Item.PreviewText ?? "文件",
            1 => Path.GetFileName(Files[0]),
            _ => string.Join("、", Files.Take(3).Select(Path.GetFileName))
        },
        _ => string.IsNullOrWhiteSpace(Item.PreviewText) ? "（空预览）" : Item.PreviewText
    };

    // Images show their own thumbnail, files borrow the shell icon, text needs no leading visual.
    public ImageSource? Preview
    {
        get
        {
            if (_previewResolved) return _preview;
            _previewResolved = true;
            _preview = Item.ItemType switch
            {
                ClipboardItemType.Image => ListPreviewProvider.LoadThumbnail(Item.ThumbnailPath ?? Item.ImagePath),
                ClipboardItemType.Files when Files.Length > 0 => ListPreviewProvider.LoadFileIcon(Files[0]),
                _ => null
            };
            return _preview;
        }
    }

    public Visibility PreviewVisibility => Item.ItemType == ClipboardItemType.Text ? Visibility.Collapsed : Visibility.Visible;
    public Stretch PreviewStretch => Item.ItemType == ClipboardItemType.Image ? Stretch.UniformToFill : Stretch.Uniform;
    public Thickness PreviewPadding => Item.ItemType == ClipboardItemType.Image ? default : new Thickness(9);
    public string FavoriteMark => Item.IsFavorite ? "♥" : "";
    public string PinnedMark => Item.IsPinned ? "置顶" : "";
    public string Metadata => _metadata ??= BuildMetadata();

    /// <summary>The Windows type description, so a row reads "PNG 图像" or "Microsoft Word 文档".</summary>
    public string TypeText => _typeText ??= BuildTypeText();

    private string[] Files => _files ??= ParseFiles();

    private string BuildTypeText()
    {
        if (Item.ItemType == ClipboardItemType.Image)
            return ListPreviewProvider.LoadTypeName(Item.ImagePath ?? Item.ThumbnailPath) ?? "图片";
        if (Item.ItemType != ClipboardItemType.Files) return "文本";
        if (Files.Length == 0) return "文件";
        var extensions = Files.Select(Path.GetExtension).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (extensions > 1) return "多种文件";
        return ListPreviewProvider.LoadTypeName(Files[0]) ?? "文件";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };

    private string[] ParseFiles()
    {
        if (Item.ItemType != ClipboardItemType.Files || string.IsNullOrWhiteSpace(Item.FileManifest)) return [];
        try { return JsonSerializer.Deserialize<string[]>(Item.FileManifest) ?? []; }
        catch (JsonException) { return []; }
    }

    private string BuildMetadata()
    {
        var parts = new List<string> { TypeText, Item.SourceApp ?? Item.SourceProcess ?? "未知来源" };
        if (Item.ItemType == ClipboardItemType.Image && Item.ContentSize > 0) parts.Add(FormatSize(Item.ContentSize));
        if (Item.ItemType == ClipboardItemType.Files && Files.Length > 0)
        {
            var folder = Path.GetDirectoryName(Files[0]);
            if (!string.IsNullOrEmpty(folder)) parts.Add(folder);
            if (Files.Length > 1) parts.Add($"共 {Files.Length} 项");
            var missing = Files.Count(path => !File.Exists(path) && !Directory.Exists(path));
            if (missing > 0) parts.Add(missing == Files.Length ? "已失效" : $"{missing} 项已失效");
        }

        parts.Add(Item.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm"));
        return string.Join(" · ", parts);
    }
}
public sealed class FolderViewModel(CollectionFolder folder, int count) { public CollectionFolder Folder { get; }=folder; public string Name=>Folder.Name; public string CountText=>count.ToString(); }
public sealed class CollectionItemViewModel(CollectionItem item)
{
    private ImageSource? _thumbnail;
    private bool _thumbnailResolved;
    private string? _typeText;

    public CollectionItem Item { get; }=item; public string Title=>Item.Title??TypeText;
    public string Preview=>Item.ItemType switch { CollectionItemType.Text=>Item.TextContent??"",CollectionItemType.Link=>Item.Url??"",CollectionItemType.Image=>Item.LocalPath??"",_=>Item.LocalPath??"" };
    public string TypeText=>_typeText??=Item.ItemType switch
    {
        CollectionItemType.Text=>"文本",
        CollectionItemType.Link=>"链接",
        CollectionItemType.Image=>ListPreviewProvider.LoadTypeName(Item.LocalPath??Item.ThumbnailPath)??"图片",
        _=>ListPreviewProvider.LoadTypeName(Item.LocalPath)??"文件"
    };

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnailResolved) return _thumbnail;
            _thumbnailResolved = true;
            _thumbnail = Item.ItemType switch
            {
                CollectionItemType.Image => ListPreviewProvider.LoadThumbnail(Item.ThumbnailPath ?? Item.LocalPath),
                CollectionItemType.File => ListPreviewProvider.LoadFileIcon(Item.LocalPath),
                _ => null
            };
            return _thumbnail;
        }
    }

    public Visibility ThumbnailVisibility => Item.ItemType is CollectionItemType.Image or CollectionItemType.File ? Visibility.Visible : Visibility.Collapsed;
    public Stretch ThumbnailStretch => Item.ItemType == CollectionItemType.Image ? Stretch.UniformToFill : Stretch.Uniform;
    public Thickness ThumbnailPadding => Item.ItemType == CollectionItemType.Image ? default : new Thickness(9);
}
