using ClipboardManager.Application.Services.FileSearch;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Interop;
using ClipboardManager.Windows.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipboardManager.Windows;

/// <summary>
/// 划词查找 results: an editable query, up to 20 ranked local files and four actions.
/// Enter copies the file itself (paste into a chat to send it), Ctrl+C copies the path,
/// Shift+Enter shows it in Explorer, double-click or Ctrl+Enter opens it with its default app
/// (executable types ask first). Closing returns focus to the window the text came from.
/// </summary>
public sealed class FileSearchPopup : Window
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(4);
    private readonly Func<FileSearchRequest, CancellationToken, Task<IReadOnlyList<FileSearchHit>>> _search;
    private readonly WindowsSystemClipboard _clipboard;
    private readonly IntPtr _returnWindow;
    private readonly TextBox _query = new() { Padding = new Thickness(8, 6, 8, 6), FontSize = 14 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 6) };
    private readonly ListBox _results = new() { MaxHeight = 330, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly Button _copyFile = ActionButton("复制文件");
    private readonly Button _copyPath = ActionButton("复制路径");
    private readonly Button _reveal = ActionButton("打开位置");
    private readonly Button _open = ActionButton("打开");
    private CancellationTokenSource? _running;
    private bool _closing;
    private bool _busyAction;

    public FileSearchPopup(
        string initialQuery, string? initialProblem, IntPtr returnWindow, WindowsSystemClipboard clipboard,
        Func<FileSearchRequest, CancellationToken, Task<IReadOnlyList<FileSearchHit>>> search)
    {
        _search = search;
        _clipboard = clipboard;
        _returnWindow = returnWindow;
        Title = "划词查找";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        if (TryFindResource("DialogWindowStyle") is Style style) Style = style;

        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12) };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        var root = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = new Button { Content = "", ToolTip = "关闭" };
        close.SetResourceReference(StyleProperty, "IconButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭");
        close.Click += (_, _) => CloseAndReturn();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var title = new TextBlock { Text = "找本机文件", VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(StyleProperty, "TitleTextStyle");
        header.Children.Add(title);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        root.Children.Add(header);

        System.Windows.Automation.AutomationProperties.SetName(_query, "搜索内容");
        _query.Text = initialQuery;
        _query.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        root.Children.Add(_query);
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        root.Children.Add(_status);

        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        _results.SelectionChanged += (_, _) => UpdateActions();
        // Double-click a result opens it like Explorer does; clicks on the scrollbar or padding are ignored.
        _results.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
                ItemsControl.ContainerFromElement(_results, source) is not ListBoxItem) return;
            e.Handled = true;
            Open();
        };
        root.Children.Add(_results);

        // 四个按钮一排，不标快捷键（键盘操作照常可用）。
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _copyFile.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _copyFile.Click += async (_, _) => await CopyFileAsync();
        _copyPath.Click += async (_, _) => await CopyPathAsync();
        _reveal.Click += (_, _) => Reveal();
        _open.Click += (_, _) => Open();
        actions.Children.Add(_open);
        actions.Children.Add(_reveal);
        actions.Children.Add(_copyPath);
        actions.Children.Add(_copyFile);
        root.Children.Add(actions);


        frame.Child = root;
        Content = frame;

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RunSearchAsync(); };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_busyAction) SafeClose(); };
        Closed += (_, _) => { _running?.Cancel(); _debounce.Stop(); };
        Loaded += async (_, _) =>
        {
            DesktopWindowPlacement.PlaceAtCursor(this);
            Activate();
            _query.Focus();
            _query.SelectAll();
            if (initialProblem is not null && string.IsNullOrWhiteSpace(initialQuery)) SetStatus(initialProblem, "WarningBrush");
            else await RunSearchAsync();
        };
        UpdateActions();
    }

    private static Button ActionButton(string text) => new() { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };

    private void SetStatus(string text, string brush = "LabelSecondaryBrush")
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private async Task RunSearchAsync()
    {
        _running?.Cancel();
        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        _results.Items.Clear();
        UpdateActions();
        var request = FileSearchQuery.Parse(_query.Text);
        if (request.Kind == FileSearchKind.Empty)
        {
            SetStatus(request.Problem ?? "请输入文件名或关键词。", "WarningBrush");
            return;
        }
        if (request.Kind == FileSearchKind.DirectPath && request.DirectPath is { } path)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var info = File.Exists(path) ? (FileSystemInfo)new FileInfo(path) : new DirectoryInfo(path);
                Show([new FileSearchHit(info.FullName, info is DirectoryInfo, (info as FileInfo)?.Length, info.LastWriteTimeUtc)], "选中的是完整路径。");
                return;
            }
            // A path from another computer still names the file; look it up by name.
            var fallback = FileSearchQuery.Parse(Path.GetFileName(path.TrimEnd('\\', '/')));
            if (fallback.Kind is FileSearchKind.Empty or FileSearchKind.DirectPath)
            {
                SetStatus("这个路径在本机不存在。", "WarningBrush");
                return;
            }
            request = fallback;
        }

        SetStatus("正在搜索…");
        try
        {
            var hits = await _search(request, cancellation.Token).WaitAsync(SearchTimeout, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var ranked = FileSearchRanker.Rank(hits, request, DateTime.UtcNow);
            if (ranked.Count == 0)
            {
                SetStatus(request.Kind == FileSearchKind.FileName
                    ? $"本机没有找到「{request.FileName}」。可以删掉部分文字后再搜。"
                    : "没有找到包含这些关键词的文件。", "WarningBrush");
                return;
            }
            Show(ranked, request.Kind == FileSearchKind.FileName ? $"按文件名「{request.FileName}」找到 {ranked.Count} 个结果" : $"找到 {ranked.Count} 个结果");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (TimeoutException) { SetStatus("Everything 没有及时响应，请稍后重试。", "WarningBrush"); }
        catch (EverythingUnavailableException exception) { SetStatus(exception.Message, "WarningBrush"); }
        catch (Exception exception) { SetStatus("搜索失败：" + exception.Message, "DangerBrush"); }
    }

    private void Show(IReadOnlyList<FileSearchHit> hits, string status)
    {
        _results.Items.Clear();
        foreach (var hit in hits) _results.Items.Add(ResultItem(hit));
        _results.SelectedIndex = 0;
        SetStatus(status);
        UpdateActions();
    }

    private static ListBoxItem ResultItem(FileSearchHit hit)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        var top = new DockPanel();
        var meta = new TextBlock { Text = Describe(hit), Margin = new Thickness(12, 0, 0, 0) };
        meta.SetResourceReference(StyleProperty, "MetaTextStyle");
        DockPanel.SetDock(meta, Dock.Right);
        top.Children.Add(meta);
        top.Children.Add(new TextBlock { Text = (hit.IsFolder ? "📁 " : string.Empty) + hit.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(top);
        var folder = new TextBlock { Text = hit.Directory ?? hit.FullPath, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = hit.FullPath };
        folder.SetResourceReference(StyleProperty, "SecondaryTextStyle");
        panel.Children.Add(folder);
        var item = new ListBoxItem { Content = panel, Tag = hit };
        System.Windows.Automation.AutomationProperties.SetName(item, hit.FullPath);
        return item;
    }

    private static string Describe(FileSearchHit hit)
    {
        var parts = new List<string>();
        if (!hit.IsFolder && hit.Size is { } size) parts.Add(FormatSize(size));
        if (hit.ModifiedUtc is { } modified) parts.Add(modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        return string.Join("  ", parts);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private FileSearchHit? Selected => (_results.SelectedItem as ListBoxItem)?.Tag as FileSearchHit;

    private void UpdateActions()
    {
        var any = Selected is not null;
        _copyFile.IsEnabled = _copyPath.IsEnabled = _reveal.IsEnabled = _open.IsEnabled = any;
    }

    private async void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseAndReturn();
                break;
            case Key.Down or Key.Up when _results.Items.Count > 0:
                e.Handled = true;
                var index = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _results.Items.Count - 1);
                _results.SelectedIndex = index;
                _results.ScrollIntoView(_results.SelectedItem);
                break;
            case Key.Enter when modifiers == ModifierKeys.Control:
                e.Handled = true;
                Open();
                break;
            case Key.Enter when modifiers == ModifierKeys.Shift:
                e.Handled = true;
                Reveal();
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                e.Handled = true;
                if (_debounce.IsEnabled) { _debounce.Stop(); await RunSearchAsync(); }
                else await CopyFileAsync();
                break;
            // Ctrl+C in the query box with selected text keeps its normal meaning.
            case Key.C when modifiers == ModifierKeys.Control && !(_query.IsKeyboardFocused && _query.SelectionLength > 0):
                e.Handled = true;
                await CopyPathAsync();
                break;
        }
    }

    private async Task CopyFileAsync()
    {
        if (Selected is not { } hit) return;
        if (!File.Exists(hit.FullPath) && !Directory.Exists(hit.FullPath)) { SetStatus("文件已不存在（可能刚被移动或删除）。", "DangerBrush"); return; }
        await RunActionAsync(() => _clipboard.WriteFilesAsync([hit.FullPath]));
    }

    private async Task CopyPathAsync()
    {
        if (Selected is not { } hit) return;
        await RunActionAsync(() => _clipboard.WriteTextAsync(hit.FullPath));
    }

    private void Reveal()
    {
        if (Selected is not { } hit) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(hit.FullPath) || Directory.Exists(hit.FullPath)) start.ArgumentList.Add("/select," + hit.FullPath);
            else if (hit.Directory is { } directory && Directory.Exists(directory)) start.ArgumentList.Add(directory);
            else { SetStatus("文件和所在文件夹都已不存在。", "DangerBrush"); return; }
            Process.Start(start);
            SafeClose();
        }
        catch (Exception exception) { SetStatus("无法打开位置：" + exception.Message, "DangerBrush"); }
    }

    /// <summary>
    /// Opens the selected result with its default app (a folder opens in Explorer). Only on an explicit
    /// double-click, button or Ctrl+Enter; executable or auto-run types ask first. The opened app takes
    /// the foreground, so focus is not handed back to the source window.
    /// </summary>
    private void Open()
    {
        if (Selected is not { } hit) return;
        if (!File.Exists(hit.FullPath) && !Directory.Exists(hit.FullPath)) { SetStatus("文件已不存在（可能刚被移动或删除）。", "DangerBrush"); return; }
        if (!hit.IsFolder && ConfigSyncPolicy.IsHighRiskFile(hit.Name))
        {
            _busyAction = true;
            try
            {
                var answer = System.Windows.MessageBox.Show(this,
                    $"「{hit.Name}」是可执行或会被系统自动运行的文件类型。确定要打开它吗？",
                    "划词查找", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }
            finally { _busyAction = false; }
        }
        try
        {
            try { Process.Start(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true }); }
            // ERROR_NO_ASSOCIATION: no default app yet, let Windows ask which one to use.
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1155)
            {
                Process.Start(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true, Verb = "openas" });
            }
            SafeClose();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { } // user cancelled the system prompt
        catch (Exception exception) { SetStatus("无法打开：" + exception.Message, "DangerBrush"); }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        _busyAction = true;
        try
        {
            await action();
            CloseAndReturn();
        }
        catch (Exception exception) { SetStatus("复制失败：" + exception.Message, "DangerBrush"); }
        finally { _busyAction = false; }
    }

    private void CloseAndReturn()
    {
        SafeClose();
        if (_returnWindow != IntPtr.Zero && NativeMethods.IsWindow(_returnWindow))
            NativeMethods.SetForegroundWindow(_returnWindow);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        // Closing inside Deactivated re-enters activation; defer it.
        Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
    }
}
