using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Services;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using ContextMenu = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

/// <summary>Everything the settings window needs from the running app; null members hide their rows.</summary>
public sealed record SettingsWindowContext(
    IAppSettingsStore Store,
    IStorageMaintenanceService? Storage = null,
    Action<string>? RestoreBackup = null,
    Action<AppSettings>? ApplyHotkeys = null,
    Func<AppSettings, Task>? Apply = null,
    ClipboardMonitorService? Monitor = null,
    string? DataFolder = null,
    string? LogFolder = null,
    Func<bool>? IsFileSearchAvailable = null,
    Func<CancellationToken, Task<AppUpdateCheck>>? CheckForUpdates = null,
    Action<AppUpdateManifest>? ShowUpdate = null);

/// <summary>
/// Settings that save as soon as a control changes. Every change re-reads the stored settings,
/// applies one edit, validates, registers hotkeys / the startup entry first, then saves atomically;
/// a failure restores the previous value and explains why on the status line.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const long MiB = 1024 * 1024;
    public static readonly IReadOnlyList<string> Pages = ["常规", "外观", "记录与存储", "隐私", "划词查找", "备份与恢复", "关于"];

    private readonly SettingsWindowContext _context;
    private readonly IAppSettingsStore _store;
    private readonly StartupRegistrationService _startup = new();
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private readonly Dictionary<string, RadioButton> _navItems = [];
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _pageTitle = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 14) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Action<AppSettings>> _binders = [];
    private AppSettings _current = new();
    private bool _loading = true;

    // Rows whose content depends on runtime state rather than AppSettings.
    private readonly TextBlock _pauseState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _usageText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _usageFill = new() { HorizontalAlignment = HorizontalAlignment.Left, Height = 6, Width = 0 };
    private readonly Border _usageTrack = new() { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
    private double _usageRatio;
    private readonly TextBlock _fileSearchState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _everythingAction = new() { Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _lastBackup = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _updateState = new() { TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(SettingsWindowContext context, string? initialPage = null)
    {
        _context = context;
        _store = context.Store;
        Title = "设置";
        Width = 780; Height = 600; MinWidth = 640; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var navBackground = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        navBackground.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
        navBackground.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var nav = new StackPanel { Margin = new Thickness(12, 18, 12, 12) };
        nav.Children.Add(new TextBlock { Text = "设置", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 12) });
        foreach (var page in Pages)
        {
            var item = new RadioButton { Content = page, GroupName = "SettingsNav" };
            item.SetResourceReference(StyleProperty, "SettingsNavItemStyle");
            item.Checked += (_, _) => ShowPage(page);
            AutomationName(item, page);
            _navItems[page] = item;
            nav.Children.Add(item);
        }
        navBackground.Child = nav;
        root.Children.Add(navBackground);

        var right = new DockPanel();
        Grid.SetColumn(right, 1);
        var footer = new Border { Padding = new Thickness(24, 9, 24, 11), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var footerRow = new DockPanel();
        var close = new Button { Content = "完成", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(12, 0, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footerRow.Children.Add(close);
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        ShowStatus("修改会立即保存并生效。");
        footerRow.Children.Add(_status);
        footer.Child = footerRow;
        DockPanel.SetDock(footer, Dock.Bottom);
        right.Children.Add(footer);
        _scroller.Padding = new Thickness(24, 20, 24, 12);
        right.Children.Add(_scroller);
        root.Children.Add(right);
        Content = root;

        _usageTrack.SizeChanged += (_, _) => ResizeUsage();
        BuildPages();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !(Keyboard.FocusedElement is HotkeyRecorder { IsRecording: true })) { Close(); e.Handled = true; }
        };
        Loaded += async (_, _) => await LoadAsync();
        if (_context.Monitor is { } monitor)
        {
            EventHandler<bool> pauseChanged = (_, _) => Dispatcher.InvokeAsync(UpdatePauseState);
            monitor.PauseChanged += pauseChanged;
            Closed += (_, _) => monitor.PauseChanged -= pauseChanged;
        }
        _navItems[initialPage is not null && _navItems.ContainsKey(initialPage) ? initialPage : Pages[0]].IsChecked = true;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private void ShowPage(string page)
    {
        var body = new StackPanel();
        _pageTitle.Text = page;
        if (_pageTitle.Parent is Panel titleParent) titleParent.Children.Remove(_pageTitle);
        if (_pages[page].Parent is Panel previous) previous.Children.Remove(_pages[page]);
        body.Children.Add(_pageTitle);
        body.Children.Add(_pages[page]);
        _scroller.Content = body;
        _scroller.ScrollToTop();
        if (page == "记录与存储") _ = RefreshUsageAsync();
        if (page == "划词查找") UpdateFileSearchState();
    }

    private async Task LoadAsync()
    {
        try
        {
            _current = await _store.LoadAsync();
            Bind(_current);
            UpdatePauseState();
            UpdateFileSearchState();
            await RefreshUsageAsync();
        }
        catch (Exception exception)
        {
            ShowStatus("读取设置失败：" + exception.Message, error: true);
        }
    }

    private void Bind(AppSettings settings)
    {
        _loading = true;
        try { foreach (var binder in _binders) binder(settings); }
        finally { _loading = false; }
    }

    // ---------- commit pipeline ----------

    private async Task<bool> CommitAsync(string label, Func<AppSettings, AppSettings> change)
    {
        if (_loading) return true;
        await _commitGate.WaitAsync();
        try
        {
            // Re-read: pause and the main window's calculator box write settings too.
            var latest = await _store.LoadAsync();
            var next = change(latest);
            if (next == latest) return true;
            AppSettingsValidator.Validate(next);
            var hotkeysChanged =
                AppSettingsValidator.NormalizeHotkey(next.GlobalHotkey) != AppSettingsValidator.NormalizeHotkey(latest.GlobalHotkey) ||
                AppSettingsValidator.NormalizeHotkey(next.SelectionSearchHotkey) != AppSettingsValidator.NormalizeHotkey(latest.SelectionSearchHotkey) ||
                next.SelectionSearchEnabled != latest.SelectionSearchEnabled;
            if (hotkeysChanged) _context.ApplyHotkeys?.Invoke(next);
            StartupRegistrationService.Snapshot? startupBefore = null;
            try
            {
                if (next.LaunchAtStartup != latest.LaunchAtStartup)
                {
                    // Registry first: a failure must not persist a setting that claims startup is enabled.
                    startupBefore = _startup.Read();
                    _startup.SetEnabled(next.LaunchAtStartup);
                }
                await _store.SaveAsync(next);
            }
            catch
            {
                if (startupBefore is not null) { try { _startup.Restore(startupBefore); } catch (Exception) { } }
                if (hotkeysChanged) { try { _context.ApplyHotkeys?.Invoke(latest); } catch (Exception) { } }
                throw;
            }
            _current = next;
            if (_context.Apply is not null) await _context.Apply(next);
            ShowStatus($"已保存：{label}");
            return true;
        }
        catch (Exception exception)
        {
            ShowStatus($"{label}未保存：{FriendlyError(exception)}", error: true);
            try { _current = await _store.LoadAsync(); } catch (Exception) { }
            Bind(_current);
            return false;
        }
        finally { _commitGate.Release(); }
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        OverflowException => "数字超出允许范围。",
        _ => exception.Message,
    };

    private void ShowStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "LabelSecondaryBrush");
    }

    // ---------- pages ----------

    private void BuildPages()
    {
        _pages["常规"] = BuildGeneralPage();
        _pages["外观"] = BuildAppearancePage();
        _pages["记录与存储"] = BuildStoragePage();
        _pages["隐私"] = BuildPrivacyPage();
        _pages["划词查找"] = BuildFileSearchPage();
        _pages["备份与恢复"] = BuildBackupPage();
        _pages["关于"] = BuildAboutPage();
    }

    private FrameworkElement BuildGeneralPage()
    {
        var page = new StackPanel();
        var hotkey = new HotkeyRecorder();
        hotkey.GestureRecorded += async (_, gesture) => await CommitAsync("呼出快捷键", s => s with { GlobalHotkey = gesture.ToSettingsString() });
        _binders.Add(s => hotkey.Show(s.GlobalHotkey));
        var launch = Switch("开机启动");
        launch.Click += async (_, _) => await CommitAsync("开机启动", s => s with { LaunchAtStartup = launch.IsChecked == true });
        _binders.Add(s => launch.IsChecked = s.LaunchAtStartup);
        page.Children.Add(Group("启动与呼出",
            Row("呼出快捷键", "点击后直接按下新的组合键，Esc 取消。被其他应用占用时会保留原快捷键。", hotkey),
            Row("登录 Windows 时启动", "只对当前用户生效。移动程序位置后请关掉再打开一次。", launch)));

        var windowMode = Segments(["自动隐藏", "保持打开", "固定在最前"], index => CommitAsync("窗口行为", s => index switch
        {
            0 => s with { AlwaysOnTop = false, AutoHideWhenDeactivated = true },
            1 => s with { AlwaysOnTop = false, AutoHideWhenDeactivated = false },
            _ => s with { AlwaysOnTop = true },
        }), out var setWindowMode);
        _binders.Add(s => setWindowMode(s.AlwaysOnTop ? 2 : s.AutoHideWhenDeactivated ? 0 : 1));
        var pasteMode = Segments(["只复制", "复制并粘贴"], index => CommitAsync("使用记录后", s => s with { EnableAutomaticPaste = index == 1 }), out var setPasteMode);
        _binders.Add(s => setPasteMode(s.EnableAutomaticPaste ? 1 : 0));
        var singleClick = Switch("单击即使用");
        singleClick.Click += async (_, _) => await CommitAsync("单击即使用", s => s with { SingleClickToPaste = singleClick.IsChecked == true });
        _binders.Add(s => singleClick.IsChecked = s.SingleClickToPaste);
        page.Children.Add(Group("窗口与使用",
            Row("切到其他应用时", "自动隐藏：点到别处就收起。保持打开：不自动收起。固定在最前：始终浮在其他窗口上方。", windowMode),
            Row("使用一条记录后", "复制并粘贴会尝试粘贴到呼出前的窗口；失败时内容仍在剪切板，可手动 Ctrl+V。", pasteMode),
            Row("单击记录直接使用", "关闭时单击只选中，双击或 Enter 才使用。", singleClick)));

        var calculator = Segments(["不处理", "提示结果", "直接替换"], index => CommitAsync("剪切板算式", s => s with
        {
            ClipboardCalculatorMode = index switch { 0 => ClipboardCalculatorMode.Off, 2 => ClipboardCalculatorMode.Rewrite, _ => ClipboardCalculatorMode.Hint }
        }), out var setCalculator);
        _binders.Add(s => setCalculator(s.ClipboardCalculatorMode switch { ClipboardCalculatorMode.Off => 0, ClipboardCalculatorMode.Rewrite => 2, _ => 1 }));
        page.Children.Add(Group("剪切板算式",
            Row("复制了 1+2×3 这样的算式时", "提示结果：只在历史里显示「= 结果」，不改剪切板。直接替换：把剪切板内容换成结果。", calculator)));
        return page;
    }

    private FrameworkElement BuildAppearancePage()
    {
        var page = new StackPanel();
        var cards = new UniformGrid { Columns = 4, Margin = new Thickness(12, 12, 2, 12) };
        var themes = new (string Id, string Name, string Note)[] { ("standard", "暖纸", "默认"), ("jiajia", "夹夹", "活泼"), ("kami", "Kami", "纸感"), ("kubai", "Kubai", "清透") };
        var themeButtons = new List<RadioButton>();
        foreach (var (id, name, note) in themes)
        {
            var card = new RadioButton { GroupName = "Theme", Margin = new Thickness(0, 0, 10, 0), Content = ThemeSwatch(id, name, note) };
            card.SetResourceReference(StyleProperty, "SettingsThemeCardStyle");
            AutomationName(card, $"主题：{name}");
            var themeId = id;
            card.Checked += async (_, _) => await CommitAsync("界面主题", s => s with { Theme = themeId });
            themeButtons.Add(card);
            cards.Children.Add(card);
        }
        _binders.Add(s => { for (var i = 0; i < themes.Length; i++) themeButtons[i].IsChecked = themes[i].Id == s.Theme; });
        var themeGroup = GroupBorder();
        themeGroup.Child = cards;
        page.Children.Add(GroupHeader("界面主题"));
        page.Children.Add(themeGroup);

        var compact = Switch("紧凑列表");
        compact.Click += async (_, _) => await CommitAsync("紧凑列表", s => s with { CompactList = compact.IsChecked == true });
        _binders.Add(s => compact.IsChecked = s.CompactList);
        var pet = Switch("小宠物");
        pet.Click += async (_, _) => await CommitAsync("小宠物", s => s with { ShowPanelPet = pet.IsChecked == true });
        _binders.Add(s => pet.IsChecked = s.ShowPanelPet);
        page.Children.Add(Group("列表与装饰",
            Row("紧凑列表", "降低行高和缩略图尺寸，一屏显示更多记录。", compact),
            Row("显示小宠物「夹夹」", "监听时眨眼，暂停时睡觉，复制到新内容时跳一下。", pet)));
        return page;
    }

    private static FrameworkElement ThemeSwatch(string theme, string name, string note)
    {
        var palette = ThemePalette(theme);
        Brush Pick(string key) => palette.TryGetValue(key, out var brush) ? brush : Brushes.Transparent;
        var preview = new Border { Height = 70, CornerRadius = new CornerRadius(6), Background = Pick("WindowSurfaceBrush"), BorderBrush = Pick("BorderSubtleBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(8) };
        var lines = new StackPanel();
        lines.Children.Add(new Border { Height = 8, Width = 54, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = Pick("LabelPrimaryBrush") });
        lines.Children.Add(new Border { Height = 14, Margin = new Thickness(0, 7, 0, 0), CornerRadius = new CornerRadius(3), Background = Pick("SelectionBrush") });
        var bottom = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Pick("AccentBrandBrush") };
        DockPanel.SetDock(dot, Dock.Right);
        bottom.Children.Add(dot);
        bottom.Children.Add(new Border { Height = 6, Width = 70, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = Pick("LabelTertiaryBrush") });
        lines.Children.Add(bottom);
        preview.Child = lines;
        var stack = new StackPanel();
        stack.Children.Add(preview);
        var caption = new TextBlock { Margin = new Thickness(2, 7, 0, 0) };
        caption.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
        var noteRun = new Run("  " + note);
        noteRun.SetResourceReference(TextElement.ForegroundProperty, "LabelTertiaryBrush");
        caption.Inlines.Add(noteRun);
        stack.Children.Add(caption);
        return stack;
    }

    /// <summary>
    /// A theme's own brushes for its preview card, read from its palette files (overlay keys win
    /// over the standard palette), so the card shows that theme even while another one is active.
    /// </summary>
    private static Dictionary<string, Brush> ThemePalette(string theme)
    {
        var result = new Dictionary<string, Brush>();
        void Merge(string uri)
        {
            try
            {
                var dictionary = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };
                foreach (var key in dictionary.Keys.OfType<string>())
                    if (dictionary[key] is Brush brush) result[key] = brush;
            }
            catch (Exception) { }
        }
        Merge("pack://application:,,,/ClipboardManager;component/Themes/Palette.xaml");
        if (theme == "jiajia") Merge("pack://application:,,,/ClipboardManager;component/Themes/Palette.Jiajia.xaml");
        if (theme == "kami") Merge("pack://application:,,,/ClipboardManager;component/Themes/Palette.Kami.xaml");
        if (theme == "kubai") Merge("pack://application:,,,/ClipboardManager;component/Themes/Palette.Kubai.xaml");
        return result;
    }

    private FrameworkElement BuildStoragePage()
    {
        var page = new StackPanel();
        var days = NumberField(["7", "30", "90", "180", "365", "3650"], "天", out var daysBox, out var daysPresets);
        CommitOnEdit(daysBox, "保留天数", (s, text) => s with { RetentionDays = checked((int)ParsePositive(text, "保留天数")) }, daysPresets);
        _binders.Add(s => daysBox.Text = s.RetentionDays.ToString(CultureInfo.InvariantCulture));
        var count = NumberField(["500", "1000", "5000", "20000", "100000"], "条", out var countBox, out var countPresets);
        CommitOnEdit(countBox, "最多保留条数", (s, text) => s with { MaxHistoryItems = checked((int)ParsePositive(text, "最多保留条数")) }, countPresets);
        _binders.Add(s => countBox.Text = s.MaxHistoryItems.ToString(CultureInfo.InvariantCulture));
        page.Children.Add(Group("历史记录",
            Row("保留天数", "超过天数的普通历史会被清理（1–3650）。收藏和置顶不受影响。", days),
            Row("最多保留条数", "超出时从最早的普通记录开始清理（1–100000）。", count)));

        var text = MegabyteBox(out var textBox);
        CommitOnEdit(textBox, "单条文字上限", (s, value) => s with { MaxTextBytes = ParseMegabytes(value, "单条文字上限") });
        _binders.Add(s => textBox.Text = FormatMegabytes(s.MaxTextBytes));
        var image = MegabyteBox(out var imageBox);
        CommitOnEdit(imageBox, "单张图片上限", (s, value) => s with { MaxPersistedImageBytes = ParseMegabytes(value, "单张图片上限") });
        _binders.Add(s => imageBox.Text = FormatMegabytes(s.MaxPersistedImageBytes));
        var cache = MegabyteBox(out var cacheBox);
        CommitOnEdit(cacheBox, "图片缓存总量", (s, value) => s with { MaxImageCacheBytes = ParseMegabytes(value, "图片缓存总量") });
        _binders.Add(s => cacheBox.Text = FormatMegabytes(s.MaxImageCacheBytes));
        page.Children.Add(Group("大小限制",
            Row("单条文字上限", "更长的文字不会进入历史（最多 16 MB）。", text),
            Row("单张图片上限", "更大的图片不会保存（最多 100 MB）。", image),
            Row("图片缓存总量", "不能小于单张图片上限。调低后不会立即删除已有图片。", cache)));

        if (_context.Storage is not null)
        {
            _usageTrack.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
            _usageTrack.CornerRadius = new CornerRadius(3);
            _usageFill.SetResourceReference(Border.BackgroundProperty, "AccentBrandBrush");
            _usageFill.CornerRadius = new CornerRadius(3);
            _usageTrack.Child = _usageFill;
            _usageText.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondaryBrush");
            _usageText.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
            _usageText.Margin = new Thickness(0, 6, 0, 0);
            _usageText.Text = "正在统计…";
            var usage = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            usage.Children.Add(new TextBlock { Text = "本地占用" });
            usage.Children.Add(_usageTrack);
            usage.Children.Add(_usageText);
            var cleanup = new Button { Content = "清理无用缓存", Padding = new Thickness(12, 5, 12, 5) };
            cleanup.Click += async (_, _) => await CleanupAssetsAsync();
            var group = GroupBorder();
            var stack = new StackPanel();
            stack.Children.Add(usage);
            stack.Children.Add(Divider());
            stack.Children.Add(Row("清理无用缓存", "只删除没有被历史或收藏引用的图片文件，先预览再确认。", cleanup));
            group.Child = stack;
            page.Children.Add(GroupHeader("存储空间"));
            page.Children.Add(group);
        }
        return page;
    }

    private FrameworkElement BuildPrivacyPage()
    {
        var page = new StackPanel();
        if (_context.Monitor is { } monitor)
        {
            var pause = new Button { Content = "暂停 ▾", Padding = new Thickness(12, 5, 12, 5) };
            var menu = new ContextMenu();
            void AddPause(string header, Func<Task> action)
            {
                var item = new MenuItem { Header = header };
                item.Click += async (_, _) => await RunAsync("暂停监听", action);
                menu.Items.Add(item);
            }
            AddPause("暂停 5 分钟", () => monitor.PauseForAsync(TimeSpan.FromMinutes(5)));
            AddPause("暂停 30 分钟", () => monitor.PauseForAsync(TimeSpan.FromMinutes(30)));
            AddPause("暂停 1 小时", () => monitor.PauseForAsync(TimeSpan.FromHours(1)));
            AddPause("一直暂停，直到我继续", () => monitor.SetPausedAsync(true));
            pause.ContextMenu = menu;
            pause.Click += (_, _) => { menu.PlacementTarget = pause; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            var resume = new Button { Content = "继续监听", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
            resume.Click += async (_, _) => await RunAsync("继续监听", () => monitor.SetPausedAsync(false));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(pause);
            buttons.Children.Add(resume);
            _pauseState.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
            page.Children.Add(Group("监听", Row("剪切板监听", null, buttons, _pauseState)));
        }

        var excluded = new ChipEditor("添加", "进程名，例如 Bitwarden");
        excluded.Changed += async (_, list) => await CommitAsync("排除的应用", s => s with { ExcludedProcesses = list });
        _binders.Add(s => excluded.SetItems(s.ExcludedProcesses));
        var excludedGroup = GroupBorder();
        var excludedStack = new StackPanel();
        excludedStack.Children.Add(RowText("不记录这些应用复制的内容", "适合密码管理器等。判断在读取剪切板内容之前进行，划词查找也不会读取这些应用。"));
        excluded.Margin = new Thickness(14, 0, 14, 12);
        excludedStack.Children.Add(excluded);
        excludedGroup.Child = excludedStack;
        page.Children.Add(GroupHeader("应用排除"));
        page.Children.Add(excludedGroup);
        return page;
    }

    private FrameworkElement BuildFileSearchPage()
    {
        var page = new StackPanel();
        var enabled = Switch("划词查找");
        enabled.Click += async (_, _) => await CommitAsync("划词查找", s => s with { SelectionSearchEnabled = enabled.IsChecked == true });
        _binders.Add(s => enabled.IsChecked = s.SelectionSearchEnabled);
        var hotkey = new HotkeyRecorder();
        hotkey.GestureRecorded += async (_, gesture) => await CommitAsync("划词查找快捷键", s => s with { SelectionSearchHotkey = gesture.ToSettingsString() });
        _binders.Add(s => { hotkey.Show(s.SelectionSearchHotkey); hotkey.IsEnabled = s.SelectionSearchEnabled; });
        page.Children.Add(Group("划词查找文件",
            Row("启用划词查找", "在任意窗口选中文件名或关键词，按快捷键，用 Everything 找到本机文件，可以直接复制成文件发给别人。", enabled),
            Row("快捷键", "选中文字后按下。默认 Ctrl+Alt+F。", hotkey)));

        var toolbar = Switch("浮动按钮");
        toolbar.Click += async (_, _) => await CommitAsync("浮动按钮", s => s with { SelectionToolbarEnabled = toolbar.IsChecked == true });
        var apps = new ChipEditor("添加", "进程名，例如 Weixin");
        apps.Changed += async (_, list) => await CommitAsync("浮动按钮应用", s => s with { SelectionToolbarApps = list });
        _binders.Add(s =>
        {
            toolbar.IsChecked = s.SelectionToolbarEnabled;
            toolbar.IsEnabled = s.SelectionSearchEnabled;
            apps.SetItems(s.SelectionToolbarApps);
            apps.IsEnabled = s.SelectionSearchEnabled && s.SelectionToolbarEnabled;
        });
        var toolbarGroup = GroupBorder();
        var toolbarStack = new StackPanel();
        toolbarStack.Children.Add(Row("选中文字后显示「找文件」按钮", "默认关闭。开启后只在下面这些应用里，用鼠标拖选或双击文字时，在光标旁出现一个小按钮。", toolbar));
        toolbarStack.Children.Add(Divider());
        toolbarStack.Children.Add(RowText("在这些应用中显示", null));
        apps.Margin = new Thickness(14, 0, 14, 12);
        toolbarStack.Children.Add(apps);
        toolbarGroup.Child = toolbarStack;
        page.Children.Add(GroupHeader("浮动按钮"));
        page.Children.Add(toolbarGroup);

        var recheck = new Button { Content = "重新检测", Padding = new Thickness(12, 5, 12, 5) };
        recheck.Click += (_, _) => UpdateFileSearchState();
        _fileSearchState.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        _everythingAction.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _everythingAction.Click += async (_, _) => await FixEverythingAsync();
        var everythingButtons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        everythingButtons.Children.Add(_everythingAction);
        everythingButtons.Children.Add(recheck);
        page.Children.Add(Group("搜索来源", Row("Everything", null, everythingButtons, _fileSearchState)));
        page.Children.Add(Note("只在你按快捷键或点按钮时读取选中的文字，用完即丢：不进入历史、不写日志、不联网。优先通过系统辅助功能接口读取；读不到时模拟一次 Ctrl+C 并立即恢复原剪切板。终端窗口里不会模拟 Ctrl+C。"));
        return page;
    }

    private FrameworkElement BuildBackupPage()
    {
        var page = new StackPanel();
        var interval = new ComboBox { MinWidth = 150, ItemsSource = new[] { "关闭", "每 24 小时", "每 48 小时", "每 72 小时" } };
        interval.SelectionChanged += async (_, _) =>
        {
            if (interval.SelectedIndex < 0) return;
            await CommitAsync("自动备份", s => s with
            {
                AutoBackupChoiceCompleted = true,
                AutoBackupIntervalHours = interval.SelectedIndex switch { 1 => 24, 2 => 48, 3 => 72, _ => 0 }
            });
        };
        _lastBackup.SetResourceReference(TextBlock.ForegroundProperty, "LabelTertiaryBrush");
        _lastBackup.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        _binders.Add(s =>
        {
            interval.SelectedIndex = s.AutoBackupIntervalHours switch { 24 => 1, 48 => 2, 72 => 3, _ => 0 };
            _lastBackup.Text = s.LastAutomaticBackupAt is { } last ? $"上次自动备份：{last.ToLocalTime():yyyy-MM-dd HH:mm}" : "还没有自动备份过。";
        });
        page.Children.Add(Group("自动备份", Row("自动备份周期", "备份保存在本机数据目录的 backups 文件夹，包含历史、收藏、待办、设置和图片。", interval, _lastBackup)));

        if (_context.Storage is not null)
        {
            var export = new Button { Content = "导出备份", Padding = new Thickness(12, 5, 12, 5) };
            export.Click += async (_, _) => await CreateBackupAsync();
            var import = new Button { Content = "从备份恢复", Padding = new Thickness(12, 5, 12, 5), IsEnabled = _context.RestoreBackup is not null };
            import.Click += async (_, _) => await RestoreBackupAsync();
            page.Children.Add(Group("手动备份",
                Row("导出完整备份", "生成一个 .clipbackup 文件，换电脑或重装后可以恢复。", export),
                Row("从备份恢复", "会替换当前全部本地数据。恢复前自动保留当前数据；应用会自动退出、恢复并重启。", import)));
        }
        return page;
    }

    private FrameworkElement BuildAboutPage()
    {
        var page = new StackPanel();
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "未知";
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0) version = version[..plusIndex];
        var schema = ClipboardManager.Infrastructure.Data.SqliteDatabaseInitializer.CurrentVersion;
        var rows = new List<UIElement>
        {
            Row("版本", null, Caption(version)),
            Row("数据库结构", null, Caption($"第 {schema} 版")),
        };
        var autoCheck = Switch("自动检查更新");
        autoCheck.Click += async (_, _) => await CommitAsync("自动检查更新", s => s with { AutoCheckForUpdates = autoCheck.IsChecked == true });
        _binders.Add(s => autoCheck.IsChecked = s.AutoCheckForUpdates);
        rows.Add(Row("自动检查更新", "启动后和每天读取一次下载服务器上的版本清单，有新版本时在托盘和页脚提示。只下载公开清单，不上传任何剪切板内容。", autoCheck));
        if (_context.CheckForUpdates is not null)
        {
            var check = new Button { Content = "检查更新", Padding = new Thickness(12, 5, 12, 5) };
            check.Click += async (_, _) => await CheckForUpdatesAsync(check);
            _updateState.SetResourceReference(TextBlock.ForegroundProperty, "LabelTertiaryBrush");
            _updateState.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
            _updateState.Text = "点击立即检查是否有新版本。";
            rows.Add(Row("检查更新", null, check, _updateState));
        }
        if (_context.DataFolder is { } data) rows.Add(Row("数据目录", data, OpenFolderButton(data)));
        if (_context.LogFolder is { } logs) rows.Add(Row("日志目录", "日志不包含剪切板正文，反馈问题时可以附上。", OpenFolderButton(logs)));
        page.Children.Add(Group("剪切板管理工具", rows.ToArray()));
        page.Children.Add(Note("数据默认只保存在本机。未登录或离线时，所有本地功能都可以正常使用。"));
        return page;
    }

    private async Task CheckForUpdatesAsync(Button button)
    {
        if (_context.CheckForUpdates is not { } check) return;
        button.IsEnabled = false;
        _updateState.Text = "正在检查…";
        try
        {
            var result = await check(CancellationToken.None);
            _updateState.Text = result.Status switch
            {
                AppUpdateStatus.UpToDate => "已是最新版本。",
                AppUpdateStatus.Available or AppUpdateStatus.Skipped when result.Manifest is { } found => $"发现新版本 {found.Version.ToString(3)}：{found.Title}",
                AppUpdateStatus.Unsupported => "下载服务器上的版本清单暂不支持，请稍后再试或打开下载页。",
                _ => result.Error ?? "检查更新失败，请稍后再试。",
            };
            if (result.Status is AppUpdateStatus.Available or AppUpdateStatus.Skipped && result.Manifest is { } manifest)
                _context.ShowUpdate?.Invoke(manifest);
        }
        catch (Exception exception) { _updateState.Text = "检查更新失败：" + FriendlyError(exception); }
        finally { button.IsEnabled = true; }
    }

    private Button OpenFolderButton(string path)
    {
        var button = new Button { Content = "打开", Padding = new Thickness(12, 5, 12, 5) };
        button.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
            }
            catch (Exception exception) { ShowStatus("无法打开文件夹：" + exception.Message, error: true); }
        };
        return button;
    }

    // ---------- runtime state ----------

    private void UpdatePauseState()
    {
        if (_context.Monitor is not { } monitor) return;
        if (!monitor.IsPaused)
        {
            _pauseState.Text = "正在监听。暂停期间复制的内容不会被读取或记录。";
            _pauseState.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondaryBrush");
        }
        else
        {
            _pauseState.Text = monitor.PausedUntil is { } until
                ? $"已暂停，{until.ToLocalTime():HH:mm} 自动继续。"
                : "已暂停，直到你点击继续监听。";
            _pauseState.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
    }

    private void UpdateFileSearchState()
    {
        var available = _context.IsFileSearchAvailable?.Invoke() ?? false;
        var state = available ? EverythingState.Running : EverythingSetup.GetState();
        _fileSearchState.Text = state switch
        {
            EverythingState.Running => "已连接。搜索在本机完成，结果按文件名匹配度和修改时间排序。",
            EverythingState.InstalledNotRunning => "Everything 已安装但没有运行。选中的是完整路径时不需要 Everything。",
            _ => "未安装 Everything。选中的是完整路径时不需要 Everything。"
        };
        _fileSearchState.SetResourceReference(TextBlock.ForegroundProperty, state == EverythingState.Running ? "SuccessBrush" : "WarningBrush");
        var canFix = state == EverythingState.InstalledNotRunning || (state == EverythingState.NotInstalled && EverythingSetup.IsBundled);
        _everythingAction.Content = state == EverythingState.InstalledNotRunning ? "启动 Everything" : "安装 Everything";
        _everythingAction.Tag = state;
        _everythingAction.Visibility = canFix ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>只在用户点击后安装（运行官方安装向导）或启动 Everything，完成后重新检测。</summary>
    private async Task FixEverythingAsync()
    {
        if (_everythingAction.Tag is not EverythingState state) return;
        _everythingAction.IsEnabled = false;
        try
        {
            var result = state == EverythingState.InstalledNotRunning
                ? await EverythingSetup.StartAsync()
                : await EverythingSetup.InstallAsync();
            if (state == EverythingState.NotInstalled && result == EverythingSetupResult.Done &&
                !await EverythingSetup.WaitUntilRunningAsync(TimeSpan.FromSeconds(3)))
                result = await EverythingSetup.StartAsync();
            UpdateFileSearchState();
            if (result == EverythingSetupResult.Failed)
            {
                _fileSearchState.Text = state == EverythingState.InstalledNotRunning ? "没能启动 Everything，请手动打开它。" : "没能完成安装。";
                _fileSearchState.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            }
        }
        finally { _everythingAction.IsEnabled = true; }
    }

    private async Task RefreshUsageAsync()
    {
        if (_context.Storage is null) return;
        try
        {
            var stats = await _context.Storage.GetUsageStatsAsync();
            var images = stats.BlobBytes + stats.ThumbnailBytes;
            var limit = Math.Max(1, _current.MaxImageCacheBytes);
            _usageRatio = Math.Clamp((double)images / limit, 0, 1);
            _usageText.Text = $"图片 {FormatBytes(images)} / 上限 {FormatBytes(_current.MaxImageCacheBytes)} · 数据库 {FormatBytes(stats.DatabaseBytes)} · 合计 {FormatBytes(stats.TotalBytes)}";
            ResizeUsage();
        }
        catch (Exception exception) { _usageText.Text = "无法统计占用：" + exception.Message; }
    }

    private void ResizeUsage() =>
        _usageFill.Width = _usageRatio > 0 ? Math.Max(_usageTrack.ActualWidth * _usageRatio, 4) : 0;

    private async Task RunAsync(string label, Func<Task> action)
    {
        try { await action(); UpdatePauseState(); ShowStatus("已更新：" + label); }
        catch (Exception exception) { ShowStatus($"{label}失败：{FriendlyError(exception)}", error: true); }
    }

    // ---------- storage actions ----------

    private async Task CreateBackupAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "剪切板备份 (*.clipbackup)|*.clipbackup", FileName = AutomaticBackupService.BackupFileName(Environment.MachineName, DateTimeOffset.Now), AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await _context.Storage!.CreateBackupAsync(dialog.FileName); ShowStatus("备份已导出：" + dialog.FileName); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "创建备份失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task RestoreBackupAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "剪切板备份 (*.clipbackup)|*.clipbackup", Title = "从完整备份恢复" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var validation = await _context.Storage!.ValidateBackupAsync(dialog.FileName);
            if (!validation.IsValid) throw new InvalidDataException(validation.Error ?? "备份校验失败。");
            var created = validation.CreatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
            if (System.Windows.MessageBox.Show(this,
                $"即将用此完整备份替换当前本地数据。\n\n备份时间：{created}\n数据库版本：{validation.SchemaVersion}\n\n应用将自动退出，恢复完成后自动重启。继续吗？",
                "确认从备份恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _context.RestoreBackup?.Invoke(dialog.FileName);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "恢复备份失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task CleanupAssetsAsync()
    {
        try
        {
            var preview = await _context.Storage!.PreviewOrphanedAssetsAsync();
            if (preview.RelativePaths.Count == 0) { ShowStatus("没有可清理的无用缓存。"); return; }
            if (System.Windows.MessageBox.Show(this, $"将删除 {preview.RelativePaths.Count} 个没有被引用的图片文件，释放约 {FormatBytes(preview.ReclaimableBytes)}。\n收藏和历史正在使用的文件不会删除。继续吗？", "确认清理缓存", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var result = await _context.Storage.CleanupOrphanedAssetsAsync(preview);
            ShowStatus($"已删除 {result.DeletedFileCount} 个文件，释放 {FormatBytes(result.ReclaimedBytes)}" + (result.FailedRelativePaths.Count > 0 ? $"，{result.FailedRelativePaths.Count} 个删除失败。" : "。"), result.FailedRelativePaths.Count > 0);
            await RefreshUsageAsync();
        }
        catch (Exception ex) { ShowStatus("清理缓存失败：" + ex.Message, error: true); }
    }

    // ---------- small builders ----------

    private static TextBlock GroupHeader(string text)
    {
        var header = new TextBlock { Text = text };
        header.SetResourceReference(StyleProperty, "SettingsGroupHeaderStyle");
        return header;
    }

    private static Border GroupBorder()
    {
        var border = new Border();
        border.SetResourceReference(StyleProperty, "SettingsGroupStyle");
        return border;
    }

    private static FrameworkElement Group(string header, params UIElement[] rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(GroupHeader(header));
        var border = GroupBorder();
        var inner = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) inner.Children.Add(Divider());
            inner.Children.Add(rows[i]);
        }
        border.Child = inner;
        stack.Children.Add(border);
        return stack;
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(14, 0, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        return line;
    }

    private static StackPanel RowText(string title, string? description, UIElement? extra = null)
    {
        var text = new StackPanel { Margin = new Thickness(14, 11, 14, 8) };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
        {
            var detail = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondaryBrush");
            detail.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
            text.Children.Add(detail);
        }
        if (extra is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(element);
        }
        return text;
    }

    private static FrameworkElement Row(string title, string? description, UIElement control, UIElement? extra = null)
    {
        var grid = new Grid { MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = RowText(title, description, extra);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(14, 10, 12, 10);
        grid.Children.Add(text);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(0, 8, 14, 8);
        }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        if (control is Control labelled && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(labelled)))
            AutomationName(labelled, title);
        return grid;
    }

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextAlignment = TextAlignment.Right };
        block.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondaryBrush");
        return block;
    }

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 12) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "LabelTertiaryBrush");
        note.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        return note;
    }

    private static CheckBox Switch(string name)
    {
        var box = new CheckBox();
        box.SetResourceReference(StyleProperty, "SettingsSwitchStyle");
        AutomationName(box, name);
        return box;
    }

    private FrameworkElement Segments(string[] labels, Func<int, Task> changed, out Action<int> select)
    {
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        border.SetResourceReference(Border.BackgroundProperty, "SegmentedTrackBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "Segment" + Guid.NewGuid().ToString("N");
        var buttons = new List<RadioButton>();
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new RadioButton { Content = labels[i], GroupName = group };
            button.SetResourceReference(StyleProperty, "SettingsSegmentStyle");
            button.Checked += async (_, _) => { if (!_loading) await changed(index); };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        border.Child = panel;
        select = index => { if (index >= 0 && index < buttons.Count) buttons[index].IsChecked = true; };
        return border;
    }

    /// <summary>A number box with a small menu of common values next to it.</summary>
    private static FrameworkElement NumberField(string[] presets, string unit, out TextBox box, out ContextMenu presetMenu)
    {
        box = new TextBox { Width = 84, Padding = new Thickness(6, 4, 6, 4), HorizontalContentAlignment = HorizontalAlignment.Right };
        presetMenu = new ContextMenu();
        foreach (var preset in presets) presetMenu.Items.Add(new MenuItem { Header = preset });
        var menu = presetMenu;
        var pick = new Button { Content = "▾", Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(4, 0, 0, 0), ToolTip = "常用数值", ContextMenu = menu };
        System.Windows.Automation.AutomationProperties.SetName(pick, "常用数值");
        pick.Click += (_, _) => { menu.PlacementTarget = pick; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(pick);
        panel.Children.Add(UnitLabel(unit));
        return panel;
    }

    private static FrameworkElement MegabyteBox(out TextBox box)
    {
        box = new TextBox { Width = 104, Padding = new Thickness(6, 4, 6, 4), HorizontalContentAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(UnitLabel("MB"));
        return panel;
    }

    private static TextBlock UnitLabel(string unit)
    {
        var label = new TextBlock { Text = unit, MinWidth = 22, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "LabelSecondaryBrush");
        return label;
    }

    /// <summary>Commits a typed value when the field loses focus, Enter is pressed, or a preset is picked.</summary>
    private void CommitOnEdit(TextBox field, string label, Func<AppSettings, string, AppSettings> change, ContextMenu? presets = null)
    {
        async Task Commit()
        {
            if (_loading) return;
            var value = field.Text.Trim();
            await CommitAsync(label, s => change(s, value));
        }
        field.LostKeyboardFocus += async (_, _) => await Commit();
        field.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Commit(); } };
        if (presets is null) return;
        foreach (var item in presets.Items.OfType<MenuItem>())
            item.Click += async (_, _) =>
            {
                field.Text = (string)item.Header;
                await Commit();
            };
    }

    private static long ParsePositive(string text, string name)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException($"{name}请填写正整数。");
        return number;
    }

    private static long ParseMegabytes(string text, string name)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var megabytes) || megabytes <= 0)
            throw new ArgumentException($"{name}请填写大于 0 的数字（单位 MB）。");
        return checked((long)Math.Round(megabytes * MiB));
    }

    internal static string FormatMegabytes(long bytes) =>
        ((decimal)bytes / MiB).ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    // ---------- reusable controls ----------

    /// <summary>
    /// Click, then press the new combination. Shows the stored gesture otherwise. Only combinations
    /// with Ctrl, Alt or Win plus a letter, digit or F-key are accepted; Esc cancels.
    /// </summary>
    private sealed class HotkeyRecorder : Button
    {
        private string _stored = string.Empty;
        public bool IsRecording { get; private set; }
        public event EventHandler<HotkeyGesture>? GestureRecorded;

        public HotkeyRecorder()
        {
            // A Button subclass does not pick up the implicit Button style by itself.
            SetResourceReference(StyleProperty, typeof(Button));
            MinWidth = 150;
            Padding = new Thickness(12, 5, 12, 5);
            Click += (_, _) => { if (!IsRecording) Begin(); };
            LostKeyboardFocus += (_, _) => End();
            PreviewKeyDown += OnKey;
        }

        public void Show(string gesture)
        {
            try { _stored = HotkeyGesture.Parse(gesture).ToDisplayString(); }
            catch (ArgumentException) { _stored = gesture; }
            if (!IsRecording) Content = _stored;
        }

        private void Begin()
        {
            IsRecording = true;
            Content = "请按下组合键…";
            Focus();
        }

        private void End()
        {
            IsRecording = false;
            Content = _stored;
        }

        private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!IsRecording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { End(); return; }
            var gesture = HotkeyGesture.FromKeyPress(Keyboard.Modifiers, key);
            if (gesture is null)
            {
                Content = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                    ? string.Join('+', ModifierText(Keyboard.Modifiers)) + "+…"
                    : "需要 Ctrl/Alt/Win + 字母、数字或 F 键";
                return;
            }
            _stored = gesture.ToDisplayString();
            End();
            GestureRecorded?.Invoke(this, gesture);
        }

        private static IEnumerable<string> ModifierText(ModifierKeys modifiers)
        {
            if (modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
            if (modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
            if (modifiers.HasFlag(ModifierKeys.Shift)) yield return "Shift";
            if (modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
        }
    }

    /// <summary>Editable list of process names shown as removable chips, with add-from-running-apps.</summary>
    private sealed class ChipEditor : StackPanel
    {
        private readonly WrapPanel _chips = new();
        private readonly TextBox _input = new() { Width = 170, Padding = new Thickness(6, 4, 6, 4) };
        private List<string> _items = [];
        public event EventHandler<IReadOnlyList<string>>? Changed;

        public ChipEditor(string addLabel, string placeholder)
        {
            Children.Add(_chips);
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(_input, placeholder);
            _input.ToolTip = placeholder;
            var add = new Button { Content = addLabel, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            add.Click += (_, _) => AddFromInput();
            _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddFromInput(); } };
            var running = new Button { Content = "从运行中的应用选 ▾", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            running.Click += (_, _) => ShowRunningApps(running);
            addRow.Children.Add(_input);
            addRow.Children.Add(add);
            addRow.Children.Add(running);
            Children.Add(addRow);
        }

        public void SetItems(IReadOnlyList<string> items)
        {
            _items = items.ToList();
            _chips.Children.Clear();
            foreach (var item in _items) _chips.Children.Add(Chip(item));
            if (_items.Count == 0)
            {
                var empty = new TextBlock { Text = "（空）", Margin = new Thickness(0, 4, 0, 4) };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LabelTertiaryBrush");
                _chips.Children.Add(empty);
            }
        }

        private FrameworkElement Chip(string name)
        {
            var chip = new Border { Padding = new Thickness(9, 2, 2, 2), Margin = new Thickness(0, 4, 6, 0), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
            chip.SetResourceReference(Border.CornerRadiusProperty, "ChipCornerRadius");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = $"移除 {name}" };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"移除 {name}");
            remove.Click += (_, _) => Raise(_items.Where(item => !string.Equals(item, name, StringComparison.OrdinalIgnoreCase)).ToList());
            row.Children.Add(remove);
            chip.Child = row;
            return chip;
        }

        private void AddFromInput()
        {
            var name = Path.GetFileNameWithoutExtension(_input.Text.Trim());
            if (string.IsNullOrWhiteSpace(name)) return;
            _input.Clear();
            Add(name);
        }

        private void Add(string name)
        {
            if (_items.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase))) return;
            Raise([.. _items, name]);
        }

        private void Raise(IReadOnlyList<string> items) => Changed?.Invoke(this, items);

        private void ShowRunningApps(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.ProcessName))
                        names.Add(process.ProcessName);
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
            foreach (var name in names.Where(name => !_items.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                var item = new MenuItem { Header = name };
                var chosen = name;
                item.Click += (_, _) => Add(chosen);
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "没有可添加的应用", IsEnabled = false });
            menu.IsOpen = true;
        }
    }
}
