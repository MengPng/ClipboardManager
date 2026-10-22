using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace ClipboardManager.Windows.Services;

public sealed class SelectionAiController(
    IAppSettingsStore settingsStore, ISelectionTextCapture capture,
    ISelectionGestureMonitor gestures, SelectionAiService ai, ILogger<SelectionAiController> logger) : IDisposable
{
    private readonly GlobalHotkeyService _hotkey = new(0x434E);
    private Window? _owner, _toolbar;
    private readonly HashSet<Window> _previews = [];
    private SelectionAiSettings _settings = new();
    private bool _busy, _disposed;
    private (bool Automatic, System.Windows.Point? Point)? _pending;
    private readonly CancellationTokenSource _lifetime = new();

    public async Task StartAsync(Window owner)
    {
        _owner = owner;
        _hotkey.Pressed += async (_, _) => await OpenAsync(false, null);
        gestures.Selected += OnSelection;
        gestures.MousePressed += OnMousePressed;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_disposed) return;
        _hotkey.Dispose(); gestures.Stop(); _toolbar?.Close();
        _settings = (await settingsStore.LoadAsync()).SelectionAi;
        if (_disposed) return;
        if (_settings.HotkeyEnabled)
        {
            try { _hotkey.Register(_owner!, HotkeyGesture.Parse(_settings.Hotkey)); }
            catch (Exception ex)
            {
                logger.LogWarning("Selection hotkey registration failed: {Type}", ex.GetType().Name);
                System.Windows.MessageBox.Show("划词快捷键注册失败，请在设置中更换组合；主剪切板功能仍可使用。", "划词快捷键", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        // The hook also dismisses an already open hotkey toolbar on an outside click.
        // Selection gestures themselves remain disabled unless the floating option is on.
        if (_settings.FloatingToolbarEnabled || _settings.HotkeyEnabled)
        {
            try { gestures.Start(); }
            catch (Exception ex)
            {
                logger.LogWarning("Selection mouse hook registration failed: {Type}", ex.GetType().Name);
                System.Windows.MessageBox.Show("无法启用划词鼠标监听，请稍后重试；主剪切板功能仍可使用。", "划词操作", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void OnSelection(System.Windows.Point point)
    {
        if (_settings.FloatingToolbarEnabled) await OpenAsync(true, point);
    }

    private void OnMousePressed(System.Windows.Point point)
    {
        var toolbar = _toolbar;
        if (toolbar is not { IsVisible: true } || IsToolbarPoint(toolbar, point)) return;
        toolbar.Close();
    }

    private async Task OpenAsync(bool automatic, System.Windows.Point? point)
    {
        if (_disposed || _owner is null) return;
        if (_busy) { _pending = (automatic, point); return; }
        _busy = true;
        try
        {
            var target = ClipboardManager.Windows.Interop.NativeMethods.GetForegroundWindow();
            await Task.Delay(100, _lifetime.Token);
            if (target != ClipboardManager.Windows.Interop.NativeMethods.GetForegroundWindow()) return;
            var text = await capture.CaptureAsync(_owner, _lifetime.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                if (!automatic) System.Windows.MessageBox.Show("未读取到所选文字，请先选中文字；当前应用也可能不支持复制。", "划词", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _toolbar?.Close();
            var window = new Window
            {
                Title = "划词操作", Height = 34, MinHeight = 34, MaxHeight = 34,
                SizeToContent = SizeToContent.Width, ResizeMode = ResizeMode.NoResize,
                ShowActivated = !automatic, Topmost = true, WindowStyle = WindowStyle.None,
                Owner = _owner
            };
            SelectionAiUi.Apply(window);
            var choices = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Height = 32, Margin = new Thickness(3, 0, 3, 0) };
            foreach (var template in _settings.Templates)
            {
                choices.Children.Add(SelectionAiUi.ToolbarButton(template.Name, (_, _) =>
                {
                    if (template.Action == PromptActions.OpenUrl) OpenWebsite(template, window);
                    else OpenPrompt(text, template, window);
                }));
            }
            if (_settings.Templates.Count == 0) choices.Children.Add(SelectionAiUi.ToolbarButton("请先在设置中配置操作", (_, _) => window.Close()));
            window.Content = new Border
            {
                Height = 34,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SurfaceOpaqueBrush"),
                BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                Child = choices
            };
            window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
            bool closing = false;
            window.Closing += (_, _) => closing = true;
            window.Deactivated += (_, _) => { if (!closing) window.Close(); };
            window.Closed += (_, _) => { if (_toolbar == window) _toolbar = null; };
            _toolbar = window;
            window.Show();
            Position(window, point);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning("Selection capture failed: {Type}", ex.GetType().Name);
            if (!automatic || ex.Message.Contains("原剪切板未能", StringComparison.Ordinal))
                System.Windows.MessageBox.Show(ex is InvalidOperationException or System.ComponentModel.Win32Exception ? ex.Message : "划词读取失败，请稍后重试。", "划词", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (!ex.Message.Contains("监听已暂停或当前应用已排除", StringComparison.Ordinal))
                ShowCaptureNotice(ex is InvalidOperationException or System.ComponentModel.Win32Exception ? ex.Message : "划词读取失败，请稍后重试。", point);
        }
        finally
        {
            _busy = false;
            if (_pending is { } next && !_disposed)
            {
                _pending = null;
                _ = _owner.Dispatcher.BeginInvoke(async () => await OpenAsync(next.Automatic, next.Point));
            }
        }
    }

    private void OpenPrompt(string text, PromptTemplate template, Window toolbar)
    {
        var result = new SelectionAiResultWindow(text, template, _settings, ai) { Owner = _owner };
        _previews.Add(result); result.Closed += (_, _) => _previews.Remove(result);
        toolbar.Close(); result.Show();
    }

    private void OpenWebsite(PromptTemplate template, Window toolbar)
    {
        try
        {
            SelectionAiSettings.ValidateTemplate(template);
            Process.Start(new ProcessStartInfo(template.Url!) { UseShellExecute = true });
            toolbar.Close();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Selection website launch failed: {Type}", ex.GetType().Name);
            System.Windows.MessageBox.Show("网站地址无效或默认浏览器无法打开。", "打开网站", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowCaptureNotice(string message, System.Windows.Point? point)
    {
        if (_disposed) return;
        try
        {
            _toolbar?.Close();
            var notice = new Window
            {
                Title = "划词提示", Width = 330, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false,
                Topmost = true, WindowStyle = WindowStyle.ToolWindow, Owner = _owner
            };
            SelectionAiUi.Apply(notice);
            var content = new StackPanel { Margin = new Thickness(12) };
            content.Children.Add(SelectionAiUi.Label(message, true));
            content.Children.Add(SelectionAiUi.Button("关闭", (_, _) => notice.Close()));
            notice.Content = content;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => notice.Close();
            notice.Closed += (_, _) => { timer.Stop(); if (_toolbar == notice) _toolbar = null; };
            _toolbar = notice; notice.Show(); Position(notice, point); timer.Start();
        }
        catch (Exception ex) { logger.LogWarning("Selection notice failed: {Type}", ex.GetType().Name); }
    }

    private static void Position(Window window, System.Windows.Point? point)
    {
        SelectionNative.GetCursorPos(out var cursor);
        var location = new System.Drawing.Point((int)(point?.X ?? cursor.X), (int)(point?.Y ?? cursor.Y));
        var work = System.Windows.Forms.Screen.FromPoint(location).WorkingArea;
        // Move natively using physical desktop coordinates so negative monitor origins
        // and per-monitor scaling do not mix DIP and pixel coordinate spaces.
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        int x = Math.Max(work.Left, Math.Min(location.X + 12, work.Right - width));
        int y = Math.Max(work.Top, Math.Min(location.Y + 16, work.Bottom - height));
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015);
    }

    private static bool IsToolbarPoint(Window window, System.Windows.Point point)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return GetWindowRect(handle, out var rect) &&
               point.X >= rect.Left && point.X < rect.Right &&
               point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        gestures.Selected -= OnSelection; gestures.MousePressed -= OnMousePressed;
        gestures.Dispose(); _hotkey.Dispose(); _toolbar?.Close();
        foreach (var window in _previews.ToArray()) window.Close();
        _lifetime.Dispose();
    }
}
