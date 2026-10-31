using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Services;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using ComboBox = System.Windows.Controls.ComboBox;

namespace ClipboardManager.Windows;

public sealed class SettingsWindow : Window
{
    private readonly IAppSettingsStore _store;
    private readonly IStorageMaintenanceService? _storage;
    private readonly Action<string>? _restoreApplication;
    private readonly StartupRegistrationService _startup = new();
    private readonly TextBox _days = new(), _count = new(), _textBytes = new(), _imageBytes = new(), _cacheBytes = new(), _hotkey = new(), _excluded = new();
    private readonly CheckBox _launch = new() { Content = "登录 Windows 时启动（当前用户）" };
    private readonly CheckBox _topmost = new() { Content = "常驻前台（窗口置顶且打开其他应用时不自动消失）" };
    private readonly CheckBox _hide = new() { Content = "窗口失去焦点时隐藏（未开启常驻前台时生效）" };
    private readonly CheckBox _paste = new() { Content = "使用记录时尝试自动粘贴（Ctrl+V）" };
    private readonly CheckBox _singleClick = new() { Content = "单击记录行直接粘贴到当前光标所在窗口" };
    private readonly CheckBox _compact = new() { Content = "紧凑列表（降低行高与缩略图尺寸，一屏显示更多记录）" };
    private readonly CheckBox _pet = new() { Content = "在标题栏显示小宠物（监听时眨眼、暂停时睡觉、捕获时跳一下）" };
    private readonly ComboBox _backupInterval = new() { MinWidth = 180 };
    private readonly ComboBox _theme = new() { MinWidth = 180, ItemsSource = new[] { "标准（暖纸）", "夹夹（二次元）", "Kami（纸）" } };
    private readonly Button _save = new() { Content = "保存", IsEnabled = false, Padding = new Thickness(20, 7, 20, 7) };
    private readonly Button _cancel = new() { Content = "取消", IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(0, 0, 8, 0) };
    private bool _saving;

    public SettingsWindow(IAppSettingsStore store, IStorageMaintenanceService? storage = null, Action<string>? restoreApplication = null)
    {
        _store = store; _storage = storage; _restoreApplication = restoreApplication;
        Title = "设置 · 仅本机"; Width = 620; Height = 700; MinWidth = 450; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("DialogWindowStyle") is Style dialogStyle) Style = dialogStyle;
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_cancel); buttons.Children.Add(_save); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var fields = new StackPanel();
        AddHeading(fields, "历史与存储");
        AddField(fields, "保留天数（1–3650）", _days);
        AddField(fields, "最大历史条数（1–100000）", _count);
        AddField(fields, "单条文本上限（字节，最多 16777216）", _textBytes);
        AddField(fields, "单张图片上限（字节，最多 104857600）", _imageBytes);
        AddField(fields, "历史图片缓存额度（字节，不小于单图上限）", _cacheBytes);
        AddNote(fields, "1 MiB = 1048576 字节。收藏及其附件不因普通历史清理而删除；调低额度不会立即清空现有记录。");
        AddField(fields, "自动备份周期", _backupInterval);
        AddField(fields, "界面主题（保存后立即生效）", _theme);
        _backupInterval.ItemsSource = new[] { "关闭", "每 24 小时", "每 48 小时", "每 72 小时" };
        if (_storage is not null)
        {
            var maintenance = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
            var stats = new Button { Content = "查看占用", Padding = new Thickness(10, 5, 10, 5) };
            var backup = new Button { Content = "导出备份", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            var restore = new Button { Content = "导入备份", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), IsEnabled = _restoreApplication is not null };
            var cleanup = new Button { Content = "预览并清理无引用缓存", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            stats.Click += async (_, _) => await ShowStorageStatsAsync();
            backup.Click += async (_, _) => await CreateBackupAsync();
            restore.Click += async (_, _) => await RestoreBackupAsync();
            cleanup.Click += async (_, _) => await CleanupAssetsAsync();
            maintenance.Children.Add(stats); maintenance.Children.Add(backup); maintenance.Children.Add(restore); maintenance.Children.Add(cleanup); fields.Children.Add(maintenance);
            AddNote(fields, "导入完整备份会替换历史、个人配置、团队配置、设置及托管附件；确认后应用会自动退出、恢复并重新启动。恢复前会保留当前数据用于失败回滚。");
        }
        AddHeading(fields, "桌面交互");
        AddField(fields, "全局呼出快捷键（例如 Alt+V、Ctrl+Shift+F2）", _hotkey);
        AddNote(fields, "快捷键修改在重启后生效；若已被其他应用占用，启动时会提示。数字主键写作 D0–D9。");
        foreach (var box in new[] { _launch, _topmost, _hide, _paste, _singleClick, _compact, _pet }) { box.Margin = new Thickness(0, 5, 0, 5); fields.Children.Add(box); }
        AddNote(fields, "自动粘贴只会尝试发送到调用前窗口；失败时保留剪切板，请手动粘贴。开机启动项指向当前程序位置，移动程序后请重新保存设置。");
        AddHeading(fields, "隐私与应用排除");
        _excluded.AcceptsReturn = true; _excluded.TextWrapping = TextWrapping.Wrap; _excluded.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _excluded.MinHeight = 100;
        AddField(fields, "排除的进程名（每行一个，例如 Bitwarden；不含路径）", _excluded);
        AddNote(fields, "排除在读取剪切板载荷之前生效。保存设置不会改变当前手动/定时暂停状态。");
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = fields });
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        _save.Click += async (_, _) => await SaveAsync();
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private static void AddHeading(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 10) });

    // Resolved from the palette rather than a named system colour, so the dialog tracks
    // the theme instead of drifting to its own grey.
    private static void AddNote(Panel panel, string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 10) };
        if (System.Windows.Application.Current?.TryFindResource("LabelSecondaryBrush") is System.Windows.Media.Brush brush) note.Foreground = brush;
        panel.Children.Add(note);
    }
    private static void AddField(Panel panel, string label, TextBox box)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        box.Margin = new Thickness(0, 5, 0, 10); box.Padding = new Thickness(7);
        panel.Children.Add(box);
    }
    private static void AddField(Panel panel, string label, ComboBox box)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        box.Margin = new Thickness(0, 5, 0, 10); box.Padding = new Thickness(7);
        panel.Children.Add(box);
    }
    private async Task LoadAsync()
    {
        try
        {
            var settings = await _store.LoadAsync();
            _days.Text = settings.RetentionDays.ToString(CultureInfo.InvariantCulture);
            _count.Text = settings.MaxHistoryItems.ToString(CultureInfo.InvariantCulture);
            _textBytes.Text = settings.MaxTextBytes.ToString(CultureInfo.InvariantCulture);
            _imageBytes.Text = settings.MaxPersistedImageBytes.ToString(CultureInfo.InvariantCulture);
            _cacheBytes.Text = settings.MaxImageCacheBytes.ToString(CultureInfo.InvariantCulture);
            _hotkey.Text = settings.GlobalHotkey;
            _excluded.Text = string.Join(Environment.NewLine, settings.ExcludedProcesses);
            _launch.IsChecked = settings.LaunchAtStartup;
            _topmost.IsChecked = settings.AlwaysOnTop;
            _hide.IsChecked = settings.AutoHideWhenDeactivated;
            _paste.IsChecked = settings.EnableAutomaticPaste;
            _singleClick.IsChecked = settings.SingleClickToPaste;
            _compact.IsChecked = settings.CompactList;
            _pet.IsChecked = settings.ShowPanelPet;
            _theme.SelectedIndex = settings.Theme switch { "jiajia" => 1, "kami" => 2, _ => 0 };
            _backupInterval.SelectedIndex = settings.AutoBackupIntervalHours switch { 24 => 1, 48 => 2, 72 => 3, _ => 0 };
            _save.IsEnabled = true;
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "读取设置失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async Task ShowStorageStatsAsync()
    {
        try
        {
            var value = await _storage!.GetUsageStatsAsync();
            System.Windows.MessageBox.Show(this, $"数据库：{FormatBytes(value.DatabaseBytes)}\n图片文件：{FormatBytes(value.BlobBytes)}（{value.BlobFileCount} 个）\n缩略图：{FormatBytes(value.ThumbnailBytes)}（{value.ThumbnailFileCount} 个）\n合计：{FormatBytes(value.TotalBytes)}", "本地存储占用", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "读取存储占用失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task CreateBackupAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "剪切板备份 (*.clipbackup)|*.clipbackup", FileName = AutomaticBackupService.BackupFileName(Environment.MachineName, DateTimeOffset.Now), AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await _storage!.CreateBackupAsync(dialog.FileName); System.Windows.MessageBox.Show(this, "备份已创建。", "本地备份", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "创建备份失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task RestoreBackupAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "剪切板备份 (*.clipbackup)|*.clipbackup", Title = "导入完整剪切板备份" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var validation = await _storage!.ValidateBackupAsync(dialog.FileName);
            if (!validation.IsValid) throw new InvalidDataException(validation.Error ?? "备份校验失败。");
            var created = validation.CreatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
            if (System.Windows.MessageBox.Show(this,
                $"即将用此完整备份替换当前本地数据。\n\n备份时间：{created}\n数据库版本：{validation.SchemaVersion}\n\n应用将自动退出，恢复完成后自动重启。继续吗？",
                "确认导入完整备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _restoreApplication?.Invoke(dialog.FileName);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "导入备份失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task CleanupAssetsAsync()
    {
        try
        {
            var preview = await _storage!.PreviewOrphanedAssetsAsync();
            if (preview.RelativePaths.Count == 0) { System.Windows.MessageBox.Show(this, "没有可清理的无引用托管文件。", "缓存清理", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (System.Windows.MessageBox.Show(this, $"将删除 {preview.RelativePaths.Count} 个无引用托管文件，释放约 {FormatBytes(preview.ReclaimableBytes)}。\n收藏和活动历史引用的文件不会删除。继续吗？", "确认缓存清理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var result = await _storage.CleanupOrphanedAssetsAsync(preview);
            System.Windows.MessageBox.Show(this, $"已删除 {result.DeletedFileCount} 个文件，释放 {FormatBytes(result.ReclaimedBytes)}。失败 {result.FailedRelativePaths.Count} 个。", "缓存清理", MessageBoxButton.OK, result.FailedRelativePaths.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "缓存清理失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static long Number(TextBox box, string name)
    {
        if (!long.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException($"{name}必须为正整数。");
        return number;
    }
    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true; _save.IsEnabled = _cancel.IsEnabled = false;
        try
        {
            // Re-read now rather than cloning the opening snapshot: pause may have changed while this dialog was open.
            var latest = await _store.LoadAsync();
            if (latest.SelectionAi.HotkeyEnabled &&
                HotkeyGesture.Parse(_hotkey.Text.Trim()) == HotkeyGesture.Parse(latest.SelectionAi.Hotkey))
                throw new ArgumentException("主面板快捷键不能与划词快捷键相同。");
            var candidate = latest with
            {
                RetentionDays = checked((int)Number(_days, "保留天数")),
                MaxHistoryItems = checked((int)Number(_count, "历史条数")),
                MaxTextBytes = Number(_textBytes, "文本上限"),
                MaxPersistedImageBytes = Number(_imageBytes, "单图上限"),
                MaxImageCacheBytes = Number(_cacheBytes, "缓存额度"),
                GlobalHotkey = _hotkey.Text.Trim(),
                ExcludedProcesses = _excluded.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LaunchAtStartup = _launch.IsChecked == true,
                AlwaysOnTop = _topmost.IsChecked == true,
                AutoHideWhenDeactivated = _hide.IsChecked == true,
                EnableAutomaticPaste = _paste.IsChecked == true,
                SingleClickToPaste = _singleClick.IsChecked == true,
                CompactList = _compact.IsChecked == true,
                ShowPanelPet = _pet.IsChecked == true,
                Theme = _theme.SelectedIndex switch { 1 => "jiajia", 2 => "kami", _ => "standard" },
                AutoBackupChoiceCompleted = true,
                AutoBackupIntervalHours = _backupInterval.SelectedIndex switch { 1 => 24, 2 => 48, 3 => 72, _ => 0 }
            };
            AppSettingsValidator.Validate(candidate);
            _ = HotkeyGesture.Parse(candidate.GlobalHotkey);
            if (candidate.ExcludedProcesses.Any(name => name.IndexOfAny(['\\', '/', ':']) >= 0))
                throw new ArgumentException("排除列表请填写进程名，不要填写完整路径。");
            var original = _startup.Read();
            try
            {
                // Registry first: a failure must not persist a setting that claims startup is enabled.
                _startup.SetEnabled(candidate.LaunchAtStartup);
                await _store.SaveAsync(candidate);
            }
            catch (Exception saveError)
            {
                try { _startup.Restore(original); }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException($"保存未完成：{saveError.Message}\n恢复开机启动项也失败：{rollbackError.Message}\n请检查 Windows 开机启动项后重试。", saveError);
                }
                throw;
            }
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex is OverflowException ? "输入数字超出允许范围。" : ex.Message, "保存设置失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _saving = false; _save.IsEnabled = _cancel.IsEnabled = true; }
    }
}
