using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
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
using System.Windows.Threading;

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
    private readonly WindowDockingService _windowDockingService;
    private readonly ClipboardManager.Domain.Contracts.IAppSettingsStore _settingsStore;
    private readonly ClipboardManager.Domain.Contracts.IStorageMaintenanceService _storageMaintenance;
    private readonly ClipboardManager.Domain.Contracts.IPersonalConfigTransferService _personalConfigTransfer;
    private readonly ILanShareService _lanShareService;
    private readonly ICloudModeService _cloudModeService;
    private readonly TodoService _todoService;
    private readonly ICredentialVault _credentialVault;
    private readonly SelectionAiController _selectionAiController;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer;
    private bool _allowClose, _favoritesOnly, _pinnedOnly, _refreshingFolders, _isPinnedTopmost;
    private bool _isCollapsed;
    private Services.CollapsedPetWindow? _collapsedPet;
    private double _expandedWidth = 340;
    private double _expandedHeight = 540;
    private double _expandedMinWidth = 340;
    private double _expandedMinHeight = 450;
    private int _activeModalCount;
    private ClipboardItemType? _itemType;
    private HistoryItemViewModel? _selectedItem;
    private FolderViewModel? _selectedFolder;
    private CollectionItemViewModel? _selectedCollectionItem;
    private System.Windows.Point _dragStart;
    private object? _dragItem;
    private long _historyVersion, _collectionVersion, _foldersVersion, _tabSelectionVersion;
    private CancellationTokenSource? _historySearch, _collectionSearch;
    private bool _compactList;
    private bool _lanSettingsLoaded;
    private string _lanSessionPin = string.Empty;
    private Guid _personalWorkspaceId;
    private Guid? _teamWorkspaceId;
    private Guid? _lastOpenedFolderMappingId;
    private long _lastOpenedFolderMappingTick;
    private readonly Services.PetCompanion _panelPet;
    // Positions of the selected folder's items in its unfiltered order, used to decide
    // whether 上移/下移 are at a boundary. Null while a search narrows the list.
    private List<Guid>? _folderItemOrder;

    private const string PersonalSpaceName = "个人配置";
    private const string TeamSpaceName = "团队配置";

    private static readonly Thickness RelaxedRowMargin = new(10, 9, 10, 9);
    private static readonly Thickness CompactRowMargin = new(9, 4, 9, 4);

    public MainWindow(ClipboardHistoryService historyService, ClipboardRestoreService restoreService,
        ClipboardMonitorService monitorService, ClipboardCaptureService captureService,
        ClipboardPayloadCaptureService payloadCaptureService, CollectionService collectionService,
        CollectionRestoreService collectionRestoreService, AutomaticPasteService automaticPasteService,
        WindowDockingService windowDockingService,
        ClipboardManager.Domain.Contracts.IAppSettingsStore settingsStore,
        ClipboardManager.Domain.Contracts.IStorageMaintenanceService storageMaintenance,
        ClipboardManager.Domain.Contracts.IPersonalConfigTransferService personalConfigTransfer,
        ILanShareService lanShareService, ICloudModeService cloudModeService, TodoService todoService,
        ICredentialVault credentialVault, SelectionAiController selectionAiController,
        ClipboardCalculationService calculationService)
    {
        _historyService = historyService; _restoreService = restoreService; _monitorService = monitorService;
        _captureService = captureService; _payloadCaptureService = payloadCaptureService;
        _collectionService = collectionService; _collectionRestoreService = collectionRestoreService;
        _automaticPasteService = automaticPasteService; _windowDockingService = windowDockingService;
        _todoService = todoService;
        _settingsStore = settingsStore; _storageMaintenance = storageMaintenance;
        _personalConfigTransfer = personalConfigTransfer;
        _lanShareService = lanShareService;
        _cloudModeService = cloudModeService;
        _credentialVault = credentialVault;
        _selectionAiController = selectionAiController;
        DataContext = this;
        InitializeComponent();
        // Created in code rather than XAML: LayoutCheck loads this window's XAML
        // loose, where a same-assembly xmlns either breaks the markup compiler
        // (with assembly=) or the loose parser (without it).
        _panelPet = new Services.PetCompanion
        {
            Width = 28, Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            ToolTip = "面板宠物",
        };
        Grid.SetColumn(_panelPet, 1);
        FooterContentGrid.Children.Add(_panelPet);
        Closed += (_, _) => _collapsedPet?.Close();
        var selectionSettings = new SelectionAiSettingsPanel(_settingsStore, _credentialVault);
        selectionSettings.Saved += async (_, _) =>
        {
            try { await _selectionAiController.ReloadAsync(); }
            catch { System.Windows.MessageBox.Show(this, "划词设置已保存，但暂时无法应用；请关闭后重试。", "划词与 AI", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        selectionSettings.Cancelled += async (_, _) => await selectionSettings.ReloadAsync();
        SelectionAiSettingsHost.Content = selectionSettings;
        calculationService.CalculationCompleted += (_, result) => Dispatcher.InvokeAsync(() => AddCalculation(result));
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
            await RunActionAsync("刷新", () => IsTodoTabActive ? RefreshTodosAsync() : IsCollectionTabActive ? RefreshCollectionItemsAsync() : RefreshHistoryAsync());
        };
        _captureService.ItemCaptured += (_, _) => Dispatcher.InvokeAsync(async () => { _panelPet.NotifyCopied(); _collapsedPet?.NotifyCopied(); await RunActionAsync("刷新历史", () => RefreshHistoryAsync()); });
        _payloadCaptureService.ItemCaptured += (_, _) => Dispatcher.InvokeAsync(async () => { _panelPet.NotifyCopied(); _collapsedPet?.NotifyCopied(); await RunActionAsync("刷新历史", () => RefreshHistoryAsync()); });
        _monitorService.PauseChanged += (_, paused) => Dispatcher.Invoke(() => UpdatePauseState(paused));
        SizeChanged += (_, _) =>
        {
            UpdateCompactTitleBar();
            UpdateTodoLayout();
        };
        Deactivated += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                if (_isPinnedTopmost || _activeModalCount > 0 || _windowDockingService.ShouldRemainVisibleAfterDeactivation()) return;
                if ((await _settingsStore.LoadAsync()).AutoHideWhenDeactivated && IsVisible &&
                    !OwnedWindows.Cast<Window>().Any(window => window.IsActive)) Hide();
            }
            catch { /* A settings read failure must not crash the desktop shell. */ }
        };
        IsVisibleChanged += (_, _) =>
        {
            // Collapse hides this window on purpose and hands the dock to the pet.
            // Undocking here is what left the pet frozen while the chat window moved.
            if (!IsVisible && !_isCollapsed && !_windowDockingService.IsTemporarilyHidden)
                _windowDockingService.Undock();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (IsCalculatorTabActive) return;
            if (Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
                (e.Key is not (Key.Up or Key.Down)) || Keyboard.Modifiers != ModifierKeys.None) return;
            var list = IsTodoTabActive ? TodoList : IsCollectionTabActive ? CollectionList : HistoryList;
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
            if (historyHit is not null && IsHistoryTabActive &&
                !IsWithin(e.OriginalSource as DependencyObject, MainTabs))
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
            if (collectionHit is not null && IsCollectionTabActive)
            {
                SelectedCollectionItem = collectionHit;
                if (IsFolderMapping(collectionHit.Item))
                {
                    _ = Dispatcher.InvokeAsync(async () =>
                        await RunActionAsync("打开映射文件夹", () => OpenFolderMappingAsync(collectionHit.Item)));
                    return;
                }
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    var settings = await _settingsStore.LoadAsync();
                    if (settings.SingleClickToPaste)
                    {
                        await RunActionAsync("单击使用配置", () => UseCollectionSelectedAsync(true));
                    }
                });
                return;
            }
        };
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];
    public ObservableCollection<FolderViewModel> Folders { get; } = [];
    public ObservableCollection<CollectionItemViewModel> CollectionItems { get; } = [];
    public ObservableCollection<LanDeviceViewModel> LanDevices { get; } = [];
    public HistoryItemViewModel? SelectedItem { get => _selectedItem; set { _selectedItem = value; Changed(nameof(SelectedItem)); } }
    public FolderViewModel? SelectedFolder { get => _selectedFolder; set { _selectedFolder = value; Changed(nameof(SelectedFolder)); } }
    public CollectionItemViewModel? SelectedCollectionItem { get => _selectedCollectionItem; set { _selectedCollectionItem = value; Changed(nameof(SelectedCollectionItem)); } }
    public bool IsPinnedTopmost => _isPinnedTopmost;
    public event PropertyChangedEventHandler? PropertyChanged;

    // Row metrics the item templates bind to. Keeping them here rather than swapping
    // item templates means a density change re-measures the existing rows instead of
    // rebuilding and re-decoding every visible thumbnail.
    /// <summary>
    /// Whether one of the config spaces is showing. Tested this way round on purpose: the
    /// first refresh runs from <see cref="InitializeAsync"/>, before the window is shown
    /// and therefore before the tab template is applied, when <c>SelectedIndex</c> is
    /// still -1. Asking for "is history" there silently routed history results nowhere.
    /// Select by named tab identity so adding calculator cannot redirect a config workspace.
    /// Todo and calculator have separate local panes.
    /// </summary>
    private bool IsTodoTabActive => ReferenceEquals(MainTabs.SelectedItem, TodoTab);
    private bool IsCalculatorTabActive => ReferenceEquals(MainTabs.SelectedItem, CalculatorTab);
    private bool IsSelectionAiTabActive => ReferenceEquals(MainTabs.SelectedItem, SelectionAiTab);
    private bool IsCollectionTabActive => ReferenceEquals(MainTabs.SelectedItem, PersonalSpaceTab) || ReferenceEquals(MainTabs.SelectedItem, TeamSpaceTab);
    private bool IsLanTabActive => ReferenceEquals(MainTabs.SelectedItem, LanShareTab);
    private bool IsHistoryTabActive => MainTabs.SelectedIndex == -1 || ReferenceEquals(MainTabs.SelectedItem, HistoryTab);

    /// <summary>
    /// The workspace the config pane reads and writes. Only the team segment resolves to
    /// the team space; every other index, including the -1 reported before the tab
    /// template is applied, resolves to 个人配置 — which is also the workspace clipboard
    /// capture stays pinned to.
    /// </summary>
    private Guid ActiveConfigWorkspaceId =>
        ReferenceEquals(MainTabs.SelectedItem, TeamSpaceTab) && _teamWorkspaceId is { } team ? team : _personalWorkspaceId;

    private string SpaceName(Guid workspaceId) =>
        _teamWorkspaceId == workspaceId ? TeamSpaceName : PersonalSpaceName;

    public Thickness RowMargin => _compactList ? CompactRowMargin : RelaxedRowMargin;
    public Thickness HistorySeparatorMargin => new(0, 8, 0, 0);
    public double ThumbnailSize => _compactList ? 28d : 34d;
    public double PreviewMaxHeight => _compactList ? 18d : 36d;

    public async Task InitializeAsync(BootstrapContext context, CancellationToken cancellationToken = default)
    {
        // 个人配置 is the local capture workspace, so the collection data that already
        // exists keeps its home and needs no migration. 团队配置 is the separate
        // local-only Team workspace; without one there is nothing honest to put behind
        // the third segment, so it is hidden rather than aliased onto 个人配置.
        _personalWorkspaceId = context.Workspace.Id;
        _teamWorkspaceId = null;
        TeamSpaceTab.Visibility = Visibility.Collapsed;
        // This desktop build is deliberately local-only; clear any previously saved cloud mode.
        await _cloudModeService.SetModeAsync(AppMode.Local, cancellationToken);
        ApplySpaceLayout();
        UpdatePauseState(CapturePrivacyPolicy.IsPaused(context.Settings, DateTimeOffset.UtcNow));
        _panelPet.Visibility = context.Settings.ShowPanelPet ? Visibility.Visible : Visibility.Collapsed;
        UpdatePinVisualState(context.Settings.AlwaysOnTop);
        ApplyDensity(context.Settings.CompactList);
        await RefreshHistoryAsync(cancellationToken);
        await RefreshFoldersAsync(cancellationToken);
        await RefreshTodosAsync(cancellationToken);
        if (!context.Settings.AutoBackupChoiceCompleted)
        {
            var dialog = new AutoBackupSetupDialog(this);
            var enabled = WithModal(() => dialog.ShowDialog()) == true;
            var latest = await _settingsStore.LoadAsync(cancellationToken);
            await _settingsStore.SaveAsync(latest with
            {
                AutoBackupChoiceCompleted = true,
                AutoBackupIntervalHours = enabled ? dialog.SelectedHours : 0
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Shows the panel the selected segment names. Both panels stay in the visual tree so
    /// a space switch re-queries rather than rebuilding the config UI and its thumbnails.
    /// </summary>
    private void ApplySpaceLayout()
    {
        var config = IsCollectionTabActive;
        var lan = IsLanTabActive;
        var todo = IsTodoTabActive;
        var calculator = IsCalculatorTabActive;
        var selectionAi = IsSelectionAiTabActive;
        HistoryPanel.Visibility = !config && !lan && !todo && !calculator && !selectionAi ? Visibility.Visible : Visibility.Collapsed;
        CalculatorPanel.Visibility = calculator ? Visibility.Visible : Visibility.Collapsed;
        ShortcutHintText.Text = calculator ? "Enter 计算" : selectionAi ? "保存后应用" : todo ? "本机待办" : "Enter 使用";
        ShortcutHintText.ToolTip = "Ctrl+F 搜索 · Delete 删除 · Ctrl+D 存入配置 · Ctrl+P 记录置顶 · Esc 隐藏";
        TodoPanel.Visibility = todo ? Visibility.Visible : Visibility.Collapsed;
        ConfigPanel.Visibility = config ? Visibility.Visible : Visibility.Collapsed;
        SelectionAiPanel.Visibility = selectionAi ? Visibility.Visible : Visibility.Collapsed;
        LanSharePanel.Visibility = lan ? Visibility.Visible : Visibility.Collapsed;
        ConfigSpaceNotice.Visibility = config && ReferenceEquals(MainTabs.SelectedItem, TeamSpaceTab) ? Visibility.Visible : Visibility.Collapsed;
        PersonalTransferPanel.Visibility = config && ReferenceEquals(MainTabs.SelectedItem, PersonalSpaceTab) ? Visibility.Visible : Visibility.Collapsed;
        if (todo) UpdateTodoLayout();
    }

    /// <summary>
    /// Rounds the window corners once the handle exists. Nothing else happens here: the
    /// Qingxu surface is opaque, so there is no backdrop to request and no fallback fill to
    /// swap in, and RootSurface keeps the brush XAML gave it.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBackdrop.TryApplyRoundedCorners(this);
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
            if (IsHistoryTabActive) CountText.Text = $"{Items.Count} 条历史";
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
        var workspaceId = ActiveConfigWorkspaceId;
        var result = await _collectionService.GetFoldersAsync(workspaceId, cancellationToken);
        var folders = new List<FolderViewModel>();
        foreach (var folder in result.Folders)
            folders.Add(new FolderViewModel(folder, (await _collectionService.GetItemsAsync(workspaceId, folder.Id, cancellationToken: cancellationToken)).Count, SpaceName(workspaceId)));
        // A space switch bumps the version and changes the active workspace, so folders
        // loaded for the space the user just left must never replace the current list.
        if (version != _foldersVersion || workspaceId != ActiveConfigWorkspaceId) return;
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
        if (SelectedFolder is null) { CollectionItems.Clear(); SelectedCollectionItem = null; _folderItemOrder = null; UpdateCollectionEmptyState(null); return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _collectionSearch = request;
        var workspaceId = SelectedFolder.WorkspaceId;
        var folderId = SelectedFolder.Folder.Id;
        var selectedId = SelectedCollectionItem?.Item.Id;
        var search = CollectionSearchBox.Text;
        try
        {
            var records = await _collectionService.GetItemsAsync(workspaceId, folderId, search, request.Token);
            // The selected folder identifies both the folder and its space, so a switch to
            // the other config space invalidates this result even at the same folder index.
            if (request.IsCancellationRequested || version != _collectionVersion ||
                SelectedFolder?.Folder.Id != folderId || SelectedFolder?.WorkspaceId != workspaceId) return;
            CollectionItems.Clear(); foreach (var item in records) CollectionItems.Add(new CollectionItemViewModel(item));
            // Without a search this query already is the folder's full order, so it can be
            // cached for the 上移/下移 boundary check. With one, the service resolves the
            // index against the unfiltered folder itself and an edge move is a no-op.
            _folderItemOrder = string.IsNullOrWhiteSpace(search) ? records.Select(x => x.Id).ToList() : null;
            SelectedCollectionItem = CollectionItems.FirstOrDefault(x => x.Item.Id == selectedId) ?? CollectionItems.FirstOrDefault();
            if (IsCollectionTabActive) CountText.Text = $"{CollectionItems.Count} 项配置";
            UpdateCollectionEmptyState(search);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) when (version != _collectionVersion) { }
        finally { if (ReferenceEquals(_collectionSearch, request)) _collectionSearch = null; }
    }

    private void UpdateCollectionEmptyState(string? search)
    {
        CollectionEmptyStateText.Text = string.IsNullOrWhiteSpace(search) ? "这个文件夹还没有内容" : "没有匹配的配置";
        CollectionEmptyStateText.Visibility = CollectionItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RememberPasteTarget() => _automaticPasteService.RememberCurrentTarget();

    protected override void OnClosing(CancelEventArgs e) { if (!_allowClose) { e.Cancel = true; Hide(); } base.OnClosing(e); }
    protected override void OnClosed(EventArgs e) { _searchTimer.Stop(); _historySearch?.Cancel(); _collectionSearch?.Cancel(); base.OnClosed(e); }

    protected override async void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled) { base.OnKeyDown(e); return; }
        // Alt combinations arrive as Key.System with the pressed key in SystemKey, so
        // Alt+Up/Down would never match a plain e.Key test.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var editing = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        if (IsCalculatorTabActive)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F)
            { CalculatorExpressionBox.Focus(); e.Handled = true; }
            else if (key == Key.Escape)
            { if (CalculatorExpressionBox.Text.Length > 0) CalculatorExpressionBox.Clear(); else Hide(); e.Handled = true; }
            else if (!editing && key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            { CalculateExpression(); e.Handled = true; }
            base.OnKeyDown(e);
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F)
        {
            (IsTodoTabActive ? TodoTitleBox : IsCollectionTabActive ? CollectionSearchBox : SearchBox).Focus();
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            var box = IsTodoTabActive ? TodoTitleBox : IsCollectionTabActive ? CollectionSearchBox : SearchBox;
            if (!string.IsNullOrEmpty(box.Text)) box.Clear(); else Hide();
            e.Handled = true;
        }
        else if (!editing && IsTodoTabActive)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.C)
            {
                e.Handled = true;
                if (SelectedTodoItem is { } item) await CopyTodoItemAsync(item);
            }
            else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Delete)
            {
                e.Handled = true;
                await RunActionAsync("删除待办", async () =>
                {
                    if (SelectedTodoItem is null) return;
                    await _todoService.DeleteAsync(SelectedTodoItem.Item.Id);
                    await RefreshTodosAsync();
                });
            }
        }
        else if (!editing)
        {
            Func<Task>? action = null;
            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.D && !IsCollectionTabActive) action = () => CollectSelectedAsync();
            else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.P && !IsCollectionTabActive) action = TogglePinnedAsync;
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down && IsCollectionTabActive)
            { var offset = key == Key.Up ? -1 : 1; action = () => MoveCollectionSelectedAsync(offset); }
            else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.Enter) action = !IsCollectionTabActive ? () => RestoreSelectedAsync(true) : () => UseCollectionSelectedAsync(true);
            else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Enter) action = !IsCollectionTabActive ? () => RestoreSelectedAsync(false) : () => UseCollectionSelectedAsync(false);
            else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Delete) action = !IsCollectionTabActive ? DeleteSelectedAsync : DeleteCollectionSelectedAsync;
            if (action is not null) { e.Handled = true; await RunActionAsync("记录操作", action); }
        }
        base.OnKeyDown(e);
    }

    public void AllowClose() => _allowClose = true;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    private async Task RunActionAsync(string name, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{name}失败: {ex}");
            System.Windows.MessageBox.Show(this, ex.Message, $"{name}失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        // The chips share a group, so checking one clears the rest. Assigning IsChecked
        // does not raise Click, so this cannot re-enter the filter.
        (pinned ? PinnedFilterButton : favorite ? FavoriteFilterButton : type == ClipboardItemType.Text ? TextFilterButton : type == ClipboardItemType.Image ? ImageFilterButton : type == ClipboardItemType.Files ? FileFilterButton : AllFilterButton).IsChecked = true;
        await RunActionAsync("筛选", () => RefreshHistoryAsync());
    }
    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("切换监听", async () =>
    {
        // The toggle already flipped on click, so the real listener state is reasserted
        // afterwards rather than leaving the icon claiming a pause that never happened.
        try { await _monitorService.SetPausedAsync(!_monitorService.IsPaused); await RefreshHistoryAsync(); }
        finally { UpdatePauseState(_monitorService.IsPaused); }
    });
    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        UpdatePinVisualState(PinButton.IsChecked == true);
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

    private void ExpandCollapseButton_Click(object sender, RoutedEventArgs e) => SetCollapsed(!_isCollapsed);

    /// <summary>Used by the global hotkey and the single-instance wake-up: showing the
    /// panel while it is collapsed must restore the expanded form, not the strip.</summary>
    public void ExpandFromCollapse()
    {
        if (_isCollapsed) SetCollapsed(false);
    }

    private void SetCollapsed(bool collapsed)
    {
        if (collapsed == _isCollapsed) return;
        if (collapsed)
        {
            _isCollapsed = true;
            _expandedWidth = Math.Max(ActualWidth, _expandedMinWidth);
            _expandedHeight = Math.Max(ActualHeight, _expandedMinHeight);
            _expandedMinWidth = MinWidth;
            _expandedMinHeight = MinHeight;
            if (_panelPet.Visibility == Visibility.Visible)
            {
                // Pet mode: hide the panel; the collapsed window hosts a DEDICATED
                // pet instance (footer pet stays put) so cross-window reparenting can
                // never break rendering or logical parenting.
                try
                {
                    _collapsedPet = new Services.CollapsedPetWindow
                    {
                        Left = Left,
                        Top = Top,
                    };
                    _collapsedPet.SetListening(!_monitorService.IsPaused);
                    _collapsedPet.ExpandRequested += () => Dispatcher.BeginInvoke(() => SetCollapsed(false));
                    _collapsedPet.HideRequested += () => Dispatcher.BeginInvoke(() =>
                    {
                        // Park the panel where the pet was left so the next Alt+V
                        // reopens at the dragged position, not the pre-collapse one.
                        if (_collapsedPet is { } pet) { Left = pet.Left; Top = pet.Top; }
                        _collapsedPet?.Close();
                        Hide();
                    });
                    _collapsedPet.Closed += OnCollapsedPetClosed;
                    // Hide() used to undock before the pet could inherit the target.
                    // Show the pet first, then hand the live dock across.
                    var dockedToChat = _windowDockingService.IsDocked;
                    Hide();
                    _collapsedPet.Show();
                    if (dockedToChat) _windowDockingService.SwapPanel(_collapsedPet);
                }
                catch (Exception error)
                {
                    Serilog.Log.Error(error, "Collapse to pet window failed; rolled back");
                    if (_collapsedPet is { } broken)
                    {
                        broken.Closed -= OnCollapsedPetClosed;
                        broken.Close();
                    }
                    _collapsedPet = null;
                    _isCollapsed = false;
                }
                return;
            }
            // Pet disabled: fall back to the compact title-bar strip.
            MainContentRow.Height = new GridLength(0);
            FooterRow.Height = new GridLength(0);
            StatusRow.Height = new GridLength(0);
            WindowTitleText.Visibility = Visibility.Collapsed;
            ListeningStatusText.Visibility = Visibility.Collapsed;
            PinButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Collapsed;
            MaxWidth = 84;
            MinWidth = 84;
            MinHeight = 46;
            Width = 84;
            Height = 46;
            ResizeMode = ResizeMode.NoResize;
            ExpandCollapseButton.Content = "\uE70D";
            ExpandCollapseButton.ToolTip = "展开剪切板";
            System.Windows.Automation.AutomationProperties.SetName(ExpandCollapseButton, "展开剪切板");
            _windowDockingService.Reposition();
            return;
        }

        // Expanding from pet mode: reopen at the pet's parking spot.
        var inheritDock = false;
        if (_collapsedPet is { } petWindow)
        {
            inheritDock = _windowDockingService.IsDocked;
            Left = petWindow.Left;
            Top = petWindow.Top;
            _collapsedPet = null;
            petWindow.Closed -= OnCollapsedPetClosed; // Expand owns the dock handoff.
            if (inheritDock) _windowDockingService.SwapPanel(this);
            petWindow.Close();
        }
        ResizeMode = ResizeMode.CanResize;
        MaxWidth = double.PositiveInfinity;
        MinWidth = _expandedMinWidth;
        MinHeight = _expandedMinHeight;
        WindowTitleText.Visibility = Visibility.Visible;
        ListeningStatusText.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
        PinButton.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
        ExpandCollapseButton.Content = "\uE70E";
        ExpandCollapseButton.ToolTip = "收起为标题栏";
        System.Windows.Automation.AutomationProperties.SetName(ExpandCollapseButton, "收起剪切板");
        MainContentRow.Height = new GridLength(1, GridUnitType.Star);
        FooterRow.Height = GridLength.Auto;
        StatusRow.Height = GridLength.Auto;
        Width = Math.Max(_expandedWidth, MinWidth);
        Height = Math.Max(_expandedHeight, MinHeight);
        Show();
        try { Activate(); }
        catch (InvalidOperationException) { /* The window is mid-lifecycle; showing alone is enough. */ }
        _isCollapsed = false;
        UpdateCompactTitleBar();
        // Re-measure after the native window has applied the restored size.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_isCollapsed) return;
            TitleBar.InvalidateMeasure();
            TitleBar.InvalidateArrange();
            TitleBar.UpdateLayout();
        }));
        _windowDockingService.Reposition();
    }

    private void OnCollapsedPetClosed(object? sender, EventArgs e)
    {
        _collapsedPet = null;
        // A closed window must never stay registered as the dock panel: the
        // target-restore path would resurrect it and crash.
        if (_windowDockingService.IsDocked) _windowDockingService.Undock();
    }

    private void UpdateCompactTitleBar()
    {
        if (_isCollapsed) return;

        // The local-only title row keeps capture state and controls visible at 340px.
        WindowTitleText.Visibility = Visibility.Visible;
        PinButton.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
        PauseButton.Visibility = Visibility.Visible;
        ExpandCollapseButton.Visibility = Visibility.Visible;
    }

    private void UpdatePinVisualState(bool pinned)
    {
        _isPinnedTopmost = pinned;
        Topmost = pinned;
        // Assigning IsChecked from here does not raise Click, so callers that already
        // handled the click and callers restoring saved settings share this path.
        PinButton.IsChecked = pinned;
        PinButton.Content = pinned ? "\uE77A" : "\uE718";
        PinButton.ToolTip = pinned
            ? "当前状态：已常驻前台（窗口始终置顶且打开其他应用时不隐藏）。点击取消常驻。"
            : "常驻前台：窗口始终置顶且打开其他应用时不隐藏。点击开启常驻。";
    }

    private void ApplyDensity(bool compact)
    {
        if (_compactList == compact) return;
        _compactList = compact;
        Changed(nameof(RowMargin));
        Changed(nameof(HistorySeparatorMargin));
        Changed(nameof(ThumbnailSize));
        Changed(nameof(PreviewMaxHeight));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The window has no system caption, so dragging is explicit. DragMove throws
        // when the button is released between the event and the call.
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private T WithModal<T>(Func<T> action)
    {
        Interlocked.Increment(ref _activeModalCount);
        try { return action(); }
        finally { Interlocked.Decrement(ref _activeModalCount); }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settingsStore, _storageMaintenance, backupPath => RestoreBackupRequested?.Invoke(this, backupPath)) { Owner = this };
        if (WithModal(() => dialog.ShowDialog()) == true) await RunActionAsync("应用设置", async () =>
        {
            var settings = await _settingsStore.LoadAsync();
            UpdatePauseState(CapturePrivacyPolicy.IsPaused(settings, DateTimeOffset.UtcNow));
            UpdatePinVisualState(settings.AlwaysOnTop);
            ApplyDensity(settings.CompactList);
            await RefreshHistoryAsync();
        });
    }
    public event EventHandler<string>? RestoreBackupRequested;
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void CollectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (FindItem<HistoryItemViewModel>(e.OriginalSource as DependencyObject) is not null) await RunActionAsync("使用历史", () => RestoreSelectedAsync(null)); }
    private async Task RestoreSelectedAsync(bool? automaticPaste)
    {
        if (SelectedItem is null) return;
        await _restoreService.RestoreAsync(SelectedItem.Item);
        var shouldPaste = automaticPaste ?? (await _settingsStore.LoadAsync()).EnableAutomaticPaste;
        if (!_isPinnedTopmost) Hide();
        if (shouldPaste && !await _automaticPasteService.TryPasteAsync(this))
            System.Windows.MessageBox.Show("内容已复制到剪切板，但未能自动粘贴，请手动按 Ctrl+V。", "自动粘贴", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task DeleteSelectedAsync() { if (SelectedItem is null) return; await _historyService.DeleteAsync(SelectedItem.Item.Id); await RefreshHistoryAsync(); }
    private async Task TogglePinnedAsync() { if (SelectedItem is null) return; await _historyService.SetPinnedAsync(SelectedItem.Item.Id, !SelectedItem.Item.IsPinned); await RefreshHistoryAsync(); }
    private async Task CollectSelectedAsync(bool chooseFolder = false)
    {
        var item = SelectedItem?.Item;
        if (item is null) return;
        if (chooseFolder)
        {
            var folder = await ChooseFolderAsync();
            if (folder is null) return;
            await _collectionService.AddSnapshotAsync(folder.WorkspaceId, folder.Folder.Id, item);
        }
        else
        {
            // Ctrl+D offers no target, so it must resolve to 个人配置 的 未分类. Letting a
            // keystroke default into 团队配置 would make sharing implicit.
            await _collectionService.QuickCollectAsync(item);
        }
        await RefreshFoldersAsync();
    }

    /// <summary>
    /// Offers personal folders only while the team configuration feature is hidden.
    /// The team workspace remains intact for a later release and is never deleted here.
    /// </summary>
    private async Task<FolderViewModel?> ChooseFolderAsync(CancellationToken cancellationToken = default)
    {
        var choices = new List<FolderViewModel>();
        foreach (var workspaceId in new[] { _personalWorkspaceId })
        {
            var result = await _collectionService.GetFoldersAsync(workspaceId, cancellationToken);
            foreach (var folder in result.Folders)
                choices.Add(new FolderViewModel(folder,
                    (await _collectionService.GetItemsAsync(workspaceId, folder.Id, cancellationToken: cancellationToken)).Count,
                    SpaceName(workspaceId)));
        }
        return WithModal(() =>
        {
            var dialog = new FolderSelectionDialog(this, choices);
            return dialog.ShowDialog() == true ? dialog.SelectedFolder : null;
        });
    }
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        var version = ++_tabSelectionVersion;
        if (!IsLoaded)
        {
            ApplySpaceLayout();
            return;
        }

        // TabItem selects on mouse-down. Defer the body swap until the current input
        // gesture has fully completed, otherwise its mouse-up can hit a newly shown
        // destructive button. Keeping this handler synchronous also prevents exceptions
        // after an async-void continuation from escaping the normal action boundary.
        _todoUiBusy = true;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (version != _tabSelectionVersion) return;
            try
            {
                ApplySpaceLayout();
                MainTabs.Focus();
                if (IsCollectionTabActive)
                {
                    _refreshingFolders = true;
                    try { SelectedFolder = null; SelectedCollectionItem = null; CollectionItems.Clear(); _folderItemOrder = null; }
                    finally { _refreshingFolders = false; }
                }

                if (IsLanTabActive) await RunActionAsync("发现局域网设备", async () =>
                {
                    if (!_lanSettingsLoaded) await LoadLanSettingsAsync();
                    await RefreshLanDevicesAsync();
                });
                else if (IsCalculatorTabActive) await RunActionAsync("加载计算器", LoadCalculatorSettingsAsync);
                else if (IsSelectionAiTabActive) await RunActionAsync("加载划词设置", () => ((SelectionAiSettingsPanel)SelectionAiSettingsHost.Content).ReloadAsync());
                else if (IsTodoTabActive) await RunActionAsync("刷新待办", () => RefreshTodosAsync());
                else await RunActionAsync("切换视图", () => IsCollectionTabActive ? RefreshFoldersAsync() : RefreshHistoryAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"切换视图失败: {ex}");
                System.Windows.MessageBox.Show(this, ex.Message, "切换视图失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (version == _tabSelectionVersion) _todoUiBusy = false;
            }
        }, DispatcherPriority.ContextIdle);
    }
    private async void RefreshLanDevicesButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("发现局域网设备", RefreshLanDevicesAsync);

    private async Task LoadLanSettingsAsync()
    {
        _ = await _settingsStore.LoadAsync();
        _lanSettingsLoaded = true;
    }

    private async void OpenLanSettingsButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("局域网共享设置", async () =>
        {
            var dialog = new LanShareSettingsDialog(this, _settingsStore, _lanSessionPin);
            if (WithModal(() => dialog.ShowDialog()) == true)
            {
                _lanSessionPin = dialog.SessionPin;
                LanStatusText.Text = "共享设置已保存，正在重新发现设备…";
                await RefreshLanDevicesAsync();
            }
        });

    private async Task RefreshLanDevicesAsync()
    {
        LanStatusText.Text = "正在发现附近设备…";
        var devices = await _lanShareService.DiscoverAsync();
        LanDevices.Clear();
        foreach (var device in devices) LanDevices.Add(new(device));
        LanEmptyText.Visibility = LanDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LanStatusText.Text = LanDevices.Count == 0
            ? "未发现设备；请确认双方在同一局域网、LocalSend 已打开且防火墙允许 UDP 53317"
            : $"发现 {LanDevices.Count} 台设备；在历史记录右键选择“发送到局域网”";
        CountText.Text = $"{LanDevices.Count} 台附近设备";
    }
    private async void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (IsLoaded && !_refreshingFolders) await RunActionAsync("读取文件夹", () => RefreshCollectionItemsAsync()); }

    private void FolderList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = FindItem<FolderViewModel>(e.OriginalSource as DependencyObject);
        if (hit is not null) SelectedFolder = hit;
        var menu = new ContextMenu();
        AddMenu(menu, "新建文件夹…", () => CreateFolderAsync());
        menu.Items.Add(new Separator());
        var rename = AddMenu(menu, "重命名…", () => RenameSelectedFolderAsync());
        var delete = AddMenu(menu, "删除文件夹", () => DeleteSelectedFolderAsync());
        var editable = SelectedFolder is { Folder.IsSystem: false };
        rename.IsEnabled = editable;
        delete.IsEnabled = editable;
        FolderList.ContextMenu = menu;
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("新建文件夹", CreateFolderAsync);
    private async Task CreateFolderAsync()
    {
        string? name = WithModal(() =>
        {
            var dialog = new SingleInputDialog(this, "新建文件夹", "请输入文件夹名称：");
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });
        if (!string.IsNullOrWhiteSpace(name)) { await _collectionService.CreateFolderAsync(ActiveConfigWorkspaceId, name); await RefreshFoldersAsync(); }
    }
    private async void RenameFolderButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("重命名", RenameSelectedFolderAsync);
    private async Task RenameSelectedFolderAsync()
    {
        var folder = SelectedFolder; if (folder is null || folder.Folder.IsSystem) return;
        string? name = WithModal(() =>
        {
            var dialog = new SingleInputDialog(this, "重命名文件夹", "请输入新名称：", folder.Name);
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });
        if (!string.IsNullOrWhiteSpace(name)) { await _collectionService.RenameFolderAsync(folder.WorkspaceId, folder.Folder.Id, name); await RefreshFoldersAsync(); }
    }
    private async void DeleteFolderButton_Click(object sender, RoutedEventArgs e) => await RunActionAsync("删除文件夹", DeleteSelectedFolderAsync);
    private async Task DeleteSelectedFolderAsync()
    {
        var folder = SelectedFolder; if (folder is null || folder.Folder.IsSystem) return;
        var confirm = WithModal(() => System.Windows.MessageBox.Show(this, "删除文件夹后，其中内容将移动到“未分类”。继续吗？", "删除文件夹", MessageBoxButton.YesNo, MessageBoxImage.Question));
        if (confirm == MessageBoxResult.Yes) { await _collectionService.DeleteFolderAsync(folder.WorkspaceId, folder.Folder.Id); await RefreshFoldersAsync(); }
    }

    private void AddCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is null) return;
        var workspaceId = SelectedFolder.WorkspaceId;
        var folderId = SelectedFolder.Folder.Id;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        AddMenu(menu, "添加文本", () => EditContentAsync(workspaceId, folderId, CollectionItemType.Text));
        AddMenu(menu, "添加链接", () => EditContentAsync(workspaceId, folderId, CollectionItemType.Link));
        AddMenu(menu, "添加图片", async () => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" }; if (WithModal(() => dialog.ShowDialog(this)) == true) { await _collectionService.AddImageAsync(workspaceId, folderId, dialog.FileName); await RefreshFoldersAsync(); } });
        AddMenu(menu, "添加文件", async () => { var dialog = new Microsoft.Win32.OpenFileDialog(); if (WithModal(() => dialog.ShowDialog(this)) == true) { await _collectionService.AddFileAsync(workspaceId, folderId, dialog.FileName); await RefreshFoldersAsync(); } });
        AddMenu(menu, "添加文件夹映射", async () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要映射的本地文件夹", Multiselect = false };
            if (WithModal(() => dialog.ShowDialog(this)) == true)
            {
                await _collectionService.AddFileAsync(workspaceId, folderId, dialog.FolderName);
                await RefreshFoldersAsync();
            }
        });
        menu.IsOpen = true;
    }

    private async void ExportPersonalConfigButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("导出个人配置", async () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出个人配置迁移包",
                Filter = "个人配置迁移包 (*.clipconfig)|*.clipconfig",
                FileName = $"ClipboardManager-个人配置-{DateTime.Now:yyyyMMdd-HHmm}.clipconfig",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (WithModal(() => dialog.ShowDialog(this)) != true) return;
            var result = await _personalConfigTransfer.ExportAsync(_personalWorkspaceId, dialog.FileName);
            WithModal(() => System.Windows.MessageBox.Show(this,
                $"导出完成。\n\n文件夹：{result.FolderCount}\n配置项：{result.ItemCount}\n已打包文件：{result.IncludedFileCount}\n大小：{FormatTransferSize(result.IncludedBytes)}" +
                (result.MissingFileCount > 0 ? $"\n未找到文件：{result.MissingFileCount}（未写入迁移包）" : ""),
                "个人配置导出", MessageBoxButton.OK,
                result.MissingFileCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning));
        });

    private async void ImportPersonalConfigButton_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("导入个人配置", async () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入个人配置迁移包",
                Filter = "个人配置迁移包 (*.clipconfig)|*.clipconfig"
            };
            if (WithModal(() => dialog.ShowDialog(this)) != true) return;
            var preview = await _personalConfigTransfer.InspectAsync(dialog.FileName);
            var confirm = WithModal(() => System.Windows.MessageBox.Show(this,
                $"将把迁移包合并到个人配置，不会覆盖团队配置或剪切板历史。\n\n文件夹：{preview.FolderCount}\n配置项：{preview.ItemCount}\n附件：{preview.IncludedFileCount}\n解压大小：{FormatTransferSize(preview.IncludedBytes)}" +
                (preview.MissingFileCount > 0 ? $"\n导出时缺失：{preview.MissingFileCount}" : "") +
                "\n\n导入前会自动备份当前数据。继续吗？",
                "确认导入个人配置", MessageBoxButton.YesNo, MessageBoxImage.Question));
            if (confirm != MessageBoxResult.Yes) return;
            var result = await _personalConfigTransfer.ImportAsync(_personalWorkspaceId, dialog.FileName);
            await RefreshFoldersAsync();
            WithModal(() => System.Windows.MessageBox.Show(this,
                $"导入完成。\n\n新建文件夹：{result.CreatedFolderCount}\n导入配置项：{result.ImportedItemCount}\n跳过项目：{result.SkippedItemCount}",
                "个人配置导入", MessageBoxButton.OK, MessageBoxImage.Information));
        });

    private static string FormatTransferSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.##} MB",
        >= 1024 => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
    private async Task EditContentAsync(Guid workspaceId, Guid folderId, CollectionItemType type, CollectionItem? item = null)
    {
        var dialog = new CollectionContentDialog(this, type, item?.Title ?? "", item is null ? "" : type == CollectionItemType.Link ? item.Url ?? "" : item.TextContent ?? "",
            async (title, content) =>
            {
                if (item is not null) await _collectionService.UpdateContentAsync(workspaceId, item, title, content);
                else if (type == CollectionItemType.Link) await _collectionService.AddLinkAsync(workspaceId, folderId, title, content.Trim());
                else await _collectionService.AddTextAsync(workspaceId, folderId, title, content);
            }, item is not null);
        if (WithModal(() => dialog.ShowDialog()) == true) await RefreshFoldersAsync();
    }
    private async Task RenameCollectionItemAsync(CollectionItem item)
    {
        string? title = WithModal(() =>
        {
            var dialog = new SingleInputDialog(this, "修改配置名称", "请输入配置名称：", item.Title ?? "");
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });
        if (string.IsNullOrWhiteSpace(title)) return;
        await _collectionService.UpdateTitleAsync(item.WorkspaceId, item, title);
        await RefreshCollectionItemsAsync();
    }
    private MenuItem AddMenu(ItemsControl menu, string title, Func<Task> action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        if (gesture is not null) item.InputGestureText = gesture;
        item.Click += async (_, e) => { e.Handled = true; await RunActionAsync(title, action); };
        menu.Items.Add(item);
        return item;
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
            AddMenu(menu, "编辑…", () => EditContentAsync(item.WorkspaceId, item.FolderId, item.ItemType, item));
        else
            AddMenu(menu, "修改名称…", () => RenameCollectionItemAsync(item));
        if (item.ItemType == CollectionItemType.Link)
            AddMenu(menu, "在浏览器打开", () => { _collectionRestoreService.OpenLink(item); return Task.CompletedTask; });
        var position = _folderItemOrder?.IndexOf(item.Id) ?? -1;
        AddMenu(menu, "上移", () => MoveCollectionSelectedAsync(-1), "Alt+↑", _folderItemOrder is null || position > 0);
        AddMenu(menu, "下移", () => MoveCollectionSelectedAsync(1), "Alt+↓", _folderItemOrder is null || (position >= 0 && position < _folderItemOrder.Count - 1));
        var move = new MenuItem { Header = "移动到文件夹" };
        foreach (var folder in Folders.Where(f => f.Folder.Id != item.FolderId))
            AddMenu(move, folder.Name, () => MoveToFolderAsync(item, folder.Folder.Id));
        move.IsEnabled = move.Items.Count > 0; menu.Items.Add(move);
        if (item.ItemType == CollectionItemType.Image)
        {
            AddMenu(menu, "打开图片所在位置…", () => ChooseFileActionAsync(null, item.LocalPath, true));
            AddMenu(menu, "图片另存为…", () => SaveImageAsync(item.LocalPath));
        }
        if (item.ItemType == CollectionItemType.File)
        {
            if (!string.IsNullOrWhiteSpace(item.LocalPath) && Directory.Exists(item.LocalPath))
                AddMenu(menu, "打开映射文件夹", () => { OpenFile(item.LocalPath, false); return Task.CompletedTask; });
            else
                AddFileMenus(menu, item.FileManifest, item.LocalPath);
        }
        AddMenu(menu, "删除配置项", async () => { await _collectionService.DeleteItemAsync(item.WorkspaceId, item.Id); await RefreshFoldersAsync(); });
        CollectionList.ContextMenu = menu;
    }

    /// <summary>
    /// Moves the selected item one place towards the start (-1) or end (+1) of its folder.
    /// The index is resolved by the service against the folder's full contents, never the
    /// search-filtered list, and an edge move is a no-op.
    /// </summary>
    private async Task MoveCollectionSelectedAsync(int offset)
    {
        var item = SelectedCollectionItem?.Item;
        var folder = SelectedFolder;
        if (item is null || folder is null) return;
        if (await _collectionService.MoveItemByOffsetAsync(folder.WorkspaceId, item.Id, folder.Folder.Id, offset))
            await RefreshCollectionItemsAsync();
    }
    private void HistoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = FindItem<HistoryItemViewModel>(e.OriginalSource as DependencyObject);
        if (hit is not null) SelectedItem = hit;
        var item = SelectedItem?.Item;
        if (item is null) { e.Handled = true; return; }
        var menu = new ContextMenu();
        AddMenu(menu, "复制到剪切板", () => _restoreService.RestoreAsync(item), "Enter");
        menu.Items.Add(new Separator());
        AddMenu(menu, "存入个人配置", async () =>
        {
            await _collectionService.CollectToUncategorizedAsync(_personalWorkspaceId, item);
            await RefreshFoldersAsync();
        }, "Ctrl+D");
        AddMenu(menu, "选择配置文件夹…", async () =>
        {
            var folder = await ChooseFolderAsync();
            if (folder is null) return;
            await _collectionService.AddSnapshotAsync(folder.WorkspaceId, folder.Folder.Id, item);
            await RefreshFoldersAsync();
        });
        AddMenu(menu, item.IsPinned ? "取消置顶" : "置顶", async () =>
        {
            await _historyService.SetPinnedAsync(item.Id, !item.IsPinned);
            await RefreshHistoryAsync();
        }, "Ctrl+P");
        menu.Items.Add(new Separator());
        if (item.ItemType == ClipboardItemType.Image) AddMenu(menu, "图片另存为…", () => SaveImageAsync(item.ImagePath));
        if (item.ItemType == ClipboardItemType.ImageFile)
        {
            var imageFile = HistoryItemViewModel.ReadFiles(item).FirstOrDefault();
            AddMenu(menu, "图片另存为…", () => SaveImageAsync(imageFile));
            AddFileMenus(menu, item.FileManifest, null);
        }
        if (item.ItemType == ClipboardItemType.Files) AddFileMenus(menu, item.FileManifest, null);
        AddMenu(menu, "删除记录", async () =>
        {
            await _historyService.DeleteAsync(item.Id);
            await RefreshHistoryAsync();
        }, "Delete");
        HistoryList.ContextMenu = menu;
    }

    private async Task SendToLanAsync(ClipboardItem item)
    {
        var devices = await _lanShareService.DiscoverAsync();
        if (devices.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "未发现 LocalSend 设备。请确认双方位于同一局域网，并允许 UDP/TCP 53317。",
                "局域网共享", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new LanDeviceSelectionDialog(this, devices);
        if (WithModal(() => dialog.ShowDialog()) != true || dialog.SelectedDevice is null) return;
        var progress = new Progress<LanShareProgress>(value =>
        {
            var percent = value.TotalBytes <= 0 ? 0 : value.BytesSent * 100 / value.TotalBytes;
            ListeningStatusText.Text = $"● {value.Stage} {percent}%";
        });
        try
        {
            var result = await _lanShareService.SendAsync(dialog.SelectedDevice, item, _lanSessionPin, progress);
            System.Windows.MessageBox.Show(this, $"已向 {result.DeviceAlias} 发送 {result.FileCount} 项。",
                "局域网共享", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { UpdatePauseState(_monitorService.IsPaused); }
    }
    private void AddFileMenus(ItemsControl menu, string? manifest, string? localPath)
    {
        // Parse inside the guarded action, not in the routed context-menu event.
        AddMenu(menu, "复制文件路径", async () =>
        {
            var paths = (manifest is null ? new[] { localPath ?? "" } : JsonSerializer.Deserialize<string[]>(manifest) ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("记录不包含可用文件路径。");
            await _restoreService.CopyTextAsync(string.Join(Environment.NewLine, paths));
        });
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
        var all = await _collectionService.GetItemsAsync(item.WorkspaceId, folderId);
        await _collectionService.MoveItemAsync(item.WorkspaceId, item.Id, folderId, all.Count);
        await RefreshFoldersAsync();
    }
    private async void CollectionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var hit = FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject);
        if (hit is null) return;
        if (IsFolderMapping(hit.Item)) return;
        await RunActionAsync("使用配置", () => UseCollectionSelectedAsync(null));
    }

    private static bool IsFolderMapping(CollectionItem item) =>
        item.ItemType == CollectionItemType.File &&
        !string.IsNullOrWhiteSpace(item.LocalPath) &&
        Directory.Exists(item.LocalPath);

    private Task OpenFolderMappingAsync(CollectionItem item)
    {
        if (!IsFolderMapping(item)) throw new DirectoryNotFoundException("映射的本地文件夹已不存在。");
        var now = Environment.TickCount64;
        if (_lastOpenedFolderMappingId == item.Id && now - _lastOpenedFolderMappingTick <= Interop.NativeMethods.GetDoubleClickTime())
            return Task.CompletedTask;
        _lastOpenedFolderMappingId = item.Id;
        _lastOpenedFolderMappingTick = now;
        OpenFile(item.LocalPath!, false);
        return Task.CompletedTask;
    }
    private async Task UseCollectionSelectedAsync(bool? automaticPaste)
    {
        if (SelectedCollectionItem is null) return;
        await _collectionRestoreService.UseAsync(SelectedCollectionItem.Item);
        var shouldPaste = automaticPaste ?? (await _settingsStore.LoadAsync()).EnableAutomaticPaste;
        if (!_isPinnedTopmost) Hide();
        if (shouldPaste && !await _automaticPasteService.TryPasteAsync(this))
            System.Windows.MessageBox.Show("内容已复制到剪切板，但未能自动粘贴，请手动按 Ctrl+V。", "自动粘贴", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task DeleteCollectionSelectedAsync() { if (SelectedCollectionItem is null) return; await _collectionService.DeleteItemAsync(SelectedCollectionItem.Item.WorkspaceId, SelectedCollectionItem.Item.Id); await RefreshFoldersAsync(); }

    private async void FolderList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed && Moved(e) && _dragItem is FolderViewModel item) { _dragItem = null; await RunActionAsync("拖动文件夹", () => { DragDrop.DoDragDrop(FolderList, item, System.Windows.DragDropEffects.Move); return Task.CompletedTask; }); } }
    private async void FolderList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var target = FindItem<FolderViewModel>(e.OriginalSource as DependencyObject);
        if (e.Data.GetData(typeof(CollectionItemViewModel)) is CollectionItemViewModel content && target is not null)
        { e.Handled = true; await RunActionAsync("移动配置项", () => MoveToFolderAsync(content.Item, target.Folder.Id)); }
        else if (e.Data.GetData(typeof(FolderViewModel)) is FolderViewModel source)
        { e.Handled = true; await RunActionAsync("文件夹排序", async () => { await _collectionService.ReorderFolderAsync(source.WorkspaceId, source.Folder.Id, target is null ? Folders.Count - 1 : Folders.IndexOf(target)); await RefreshFoldersAsync(); }); }
    }
    private async void CollectionList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed && Moved(e) && _dragItem is CollectionItemViewModel item) { _dragItem = null; await RunActionAsync("拖动配置项", () => { DragDrop.DoDragDrop(CollectionList, item, System.Windows.DragDropEffects.Move); return Task.CompletedTask; }); } }
    private async void CollectionList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (SelectedFolder is null || e.Data.GetData(typeof(CollectionItemViewModel)) is not CollectionItemViewModel source) return;
        e.Handled = true;
        var workspaceId = SelectedFolder.WorkspaceId;
        var folderId = SelectedFolder.Folder.Id;
        var target = FindItem<CollectionItemViewModel>(e.OriginalSource as DependencyObject);
        await RunActionAsync("配置排序", async () =>
        {
            // Resolve against the full folder, never the filtered search-result index.
            var all = (await _collectionService.GetItemsAsync(workspaceId, folderId)).Where(i => i.Id != source.Item.Id).ToList();
            if (target?.Item.Id == source.Item.Id) return;
            var index = target is null ? all.Count : all.FindIndex(i => i.Id == target.Item.Id);
            await _collectionService.MoveItemAsync(workspaceId, source.Item.Id, folderId, index < 0 ? all.Count : index);
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
        var internalMove = IsCollectionTabActive &&
            ((e.Data.GetDataPresent(typeof(FolderViewModel)) && overFolder) ||
             (e.Data.GetDataPresent(typeof(CollectionItemViewModel)) && ((overFolder && FindItem<FolderViewModel>(e.OriginalSource as DependencyObject) is not null) || (overItems && SelectedFolder is not null))));
        e.Effects = internalMove ? System.Windows.DragDropEffects.Move :
            IsCollectionTabActive && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && SelectedFolder is not null ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
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
        if (e.Handled || !IsCollectionTabActive || SelectedFolder is null || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        var target = FindItem<FolderViewModel>(e.OriginalSource as DependencyObject) ?? SelectedFolder;
        var workspaceId = target.WorkspaceId;
        var folderId = target.Folder.Id;
        foreach (var path in paths)
            await RunActionAsync("导入文件", async () =>
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(ext)) await _collectionService.AddImageAsync(workspaceId, folderId, path);
                else await _collectionService.AddFileAsync(workspaceId, folderId, path);
            });
        await RunActionAsync("刷新配置", () => RefreshFoldersAsync());
    }
    private void UpdatePauseState(bool paused)
    {
        ListeningStatusText.Text = paused ? "● 暂停" : "● 监听";
        ListeningStatusText.Foreground = (System.Windows.Media.Brush)FindResource(paused ? "WarningBrush" : "SuccessBrush");
        PauseButton.IsChecked = paused;
        PauseButton.Content = paused ? "\uE768" : "\uE769";
        _panelPet.SetListening(!paused);
        _collapsedPet?.SetListening(!paused);
    }
}

public sealed class HistoryItemViewModel(ClipboardItem item)
{
    private string[]? _files;
    private string? _metadata;
    private string? _typeText;
    private ImageSource? _preview;
    private bool _previewResolved;

    public ClipboardItem Item { get; } = item;
    public TextWrapping PreviewWrapping => Item.ItemType is ClipboardItemType.Files or ClipboardItemType.ImageFile
        ? TextWrapping.NoWrap : TextWrapping.Wrap;
    public string PreviewText => Item.ItemType switch
    {
        ClipboardItemType.Image => TypeText,
        ClipboardItemType.ImageFile => Files.FirstOrDefault() is { } imageFile ? Path.GetFileName(imageFile) : Item.PreviewText ?? "图片",
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
                ClipboardItemType.ImageFile when Files.Length > 0 => ListPreviewProvider.LoadThumbnail(Files[0]) ?? ListPreviewProvider.LoadFileIcon(Files[0]),
                ClipboardItemType.Files when Files.Length > 0 => ListPreviewProvider.LoadFileIcon(Files[0]),
                _ => null
            };
            return _preview;
        }
    }

    public Visibility PreviewVisibility => Item.ItemType == ClipboardItemType.Text ? Visibility.Collapsed : Visibility.Visible;
    public Stretch PreviewStretch => Item.ItemType is ClipboardItemType.Image or ClipboardItemType.ImageFile ? Stretch.UniformToFill : Stretch.Uniform;
    public Thickness PreviewPadding => default;
    // Rendered with the icon font, so these are glyph code points, not text.
    public string FavoriteMark => Item.IsFavorite ? "\uEB52" : "";
    public string PinnedMark => Item.IsPinned ? "\uE718" : "";
    public string Metadata => _metadata ??= BuildMetadata();

    /// <summary>The Windows type description, so a row reads "PNG 图像" or "Microsoft Word 文档".</summary>
    public string TypeText => _typeText ??= BuildTypeText();

    private string[] Files => _files ??= ParseFiles();

    private string BuildTypeText()
    {
        if (Item.ItemType == ClipboardItemType.Image)
            return ListPreviewProvider.LoadTypeName(Item.ImagePath ?? Item.ThumbnailPath) ?? "图片";
        if (Item.ItemType == ClipboardItemType.ImageFile)
            return Files.Length > 0 ? ListPreviewProvider.LoadTypeName(Files[0]) ?? "图片" : "图片";
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
        return ReadFiles(Item);
    }

    internal static string[] ReadFiles(ClipboardItem item)
    {
        if (item.ItemType is not (ClipboardItemType.Files or ClipboardItemType.ImageFile) || string.IsNullOrWhiteSpace(item.FileManifest)) return [];
        try { return JsonSerializer.Deserialize<string[]>(item.FileManifest) ?? []; }
        catch (JsonException) { return []; }
    }

    private string BuildMetadata()
    {
        var parts = new List<string> { TypeText, Item.SourceApp ?? Item.SourceProcess ?? "未知来源" };
        if (Item.ItemType == ClipboardItemType.Image && Item.ContentSize > 0) parts.Add(FormatSize(Item.ContentSize));
        if ((Item.ItemType is ClipboardItemType.Files or ClipboardItemType.ImageFile) && Files.Length > 0)
        {
            if (Files.Length > 1) parts.Add($"共 {Files.Length} 项");
            var missing = Files.Count(path => !File.Exists(path) && !Directory.Exists(path));
            if (missing > 0) parts.Add(missing == Files.Length ? "已失效" : $"{missing} 项已失效");
        }

        parts.Add(Item.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm"));
        return string.Join(" · ", parts);
    }
}
/// <summary>
/// A folder plus the config space it belongs to. Every folder-scoped command reads the
/// workspace from here rather than from whichever space happens to be selected, which is
/// what lets the cross-space folder picker hand back an unambiguous target.
/// </summary>
public sealed class FolderViewModel(CollectionFolder folder, int count, string spaceName)
{
    public CollectionFolder Folder { get; } = folder;
    public Guid WorkspaceId => Folder.WorkspaceId;
    public string Name => Folder.Name;
    public string SpaceName { get; } = spaceName;
    /// <summary>Space-qualified label for pickers that mix folders from both spaces.</summary>
    public string QualifiedName => $"{SpaceName} · {Folder.Name}";
    public string CountText => count.ToString();
}
public sealed class CollectionItemViewModel(CollectionItem item)
{
    private ImageSource? _thumbnail;
    private bool _thumbnailResolved;
    private string? _typeText;

    public CollectionItem Item { get; }=item;
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? TypeText : Item.Title;
    public string Preview => Item.ItemType switch
    {
        CollectionItemType.Text => TextContentService.CreatePreview(Item.TextContent ?? ""),
        CollectionItemType.Link => Item.Url ?? "",
        _ => ""
    };
    public string TypeText=>_typeText??=Item.ItemType switch
    {
        CollectionItemType.Text=>"文本",
        CollectionItemType.Link=>"链接",
        CollectionItemType.Image=>ListPreviewProvider.LoadTypeName(Item.LocalPath??Item.ThumbnailPath)??"图片",
        _=>!string.IsNullOrWhiteSpace(Item.LocalPath) && Directory.Exists(Item.LocalPath)
            ? "文件夹映射"
            : ListPreviewProvider.LoadTypeName(Item.LocalPath)??"文件"
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
    public Visibility PreviewVisibility => Item.ItemType is CollectionItemType.Text or CollectionItemType.Link
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Stretch ThumbnailStretch => Item.ItemType == CollectionItemType.Image ? Stretch.UniformToFill : Stretch.Uniform;
    public Thickness ThumbnailPadding => Item.ItemType == CollectionItemType.Image ? default : new Thickness(9);
}
