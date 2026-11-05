using ClipboardManager.Application.Services.FileSearch;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Interop;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// 划词查找 (ADR-0007): a global hotkey, and an optional floating button that appears only in
/// whitelisted chat apps, read the selected text on demand and look it up in the local
/// Everything index. Nothing is captured until the user asks; nothing leaves the machine.
/// </summary>
public sealed class SelectionSearchService(
    SelectionTextCapture capture,
    WindowsSystemClipboard clipboard,
    ForegroundApplicationService foreground,
    ILogger<SelectionSearchService> logger) : IDisposable
{
    private const int SearchLimit = 120;
    private readonly GlobalHotkeyService _hotkey = new(0x434E);
    private Window? _owner;
    private SelectionGestureMonitor? _gesture;
    private SelectionToolbar? _toolbar;
    private FileSearchPopup? _popup;
    private IReadOnlyList<string> _toolbarApps = [];
    private bool _running;

    public static bool IsIndexAvailable() => EverythingIpcClient.IsRunning();

    /// <summary>Registers the hotkey and toolbar. Throws when the hotkey is taken; the toolbar still starts.</summary>
    public void Start(Window owner, AppSettings settings)
    {
        _owner = owner;
        _hotkey.Pressed += async (_, _) => await TriggerAsync(fromToolbar: false);
        ApplyToolbar(settings);
        ApplyHotkey(settings);
    }

    /// <summary>Rebinds or releases the hotkey. On failure the previous gesture stays registered and the error is rethrown.</summary>
    public void ApplyHotkey(AppSettings settings)
    {
        if (_owner is null) return;
        if (!settings.SelectionSearchEnabled) { _hotkey.Unregister(); return; }
        _hotkey.Rebind(_owner, HotkeyGesture.Parse(settings.SelectionSearchHotkey));
    }

    public void ApplyToolbar(AppSettings settings)
    {
        _toolbarApps = settings.SelectionToolbarApps;
        var wanted = settings.SelectionSearchEnabled && settings.SelectionToolbarEnabled && settings.SelectionToolbarApps.Count > 0;
        if (wanted && _gesture is null)
        {
            try
            {
                var gesture = new SelectionGestureMonitor();
                gesture.Selected += OnSelected;
                gesture.MousePressed += OnMousePressed;
                gesture.Start();
                _gesture = gesture;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Selection toolbar mouse hook failed: {ExceptionType}", exception.GetType().Name);
            }
        }
        else if (!wanted && _gesture is not null)
        {
            _gesture.Dispose();
            _gesture = null;
            _toolbar?.Hide();
        }
    }

    private void OnSelected(System.Windows.Point screenPixels)
    {
        if (_running || _popup is not null) return;
        var source = foreground.GetCurrentSource();
        var process = source.ProcessName is { } name ? Path.GetFileNameWithoutExtension(name) : null;
        if (process is null || !_toolbarApps.Any(app => string.Equals(app, process, StringComparison.OrdinalIgnoreCase))) return;
        _toolbar ??= new SelectionToolbar(async () => await TriggerAsync(fromToolbar: true));
        _toolbar.ShowAt(screenPixels);
    }

    private void OnMousePressed(System.Windows.Point screenPixels)
    {
        if (_toolbar is { IsVisible: true } toolbar && !toolbar.ContainsScreenPoint(screenPixels)) toolbar.Hide();
    }

    private async Task TriggerAsync(bool fromToolbar)
    {
        _toolbar?.Hide();
        if (_owner is null || _running) return;
        if (_popup is not null) { _popup.Activate(); return; }
        // The hotkey pressed while our own window is in front has nothing to read.
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var process);
        if (process == (uint)Environment.ProcessId) return;
        _running = true;
        try
        {
            var result = await capture.CaptureAsync(_owner);
            var text = result.Text ?? string.Empty;
            var request = FileSearchQuery.Parse(text);
            var initial = request.Kind switch
            {
                FileSearchKind.DirectPath => request.DirectPath!,
                FileSearchKind.FileName => request.FileName!,
                FileSearchKind.Keywords => string.Join(' ', request.Keywords),
                _ => text.Length <= FileSearchQuery.MaxKeywordSelectionLength ? text.Trim() : string.Empty,
            };
            var problem = result.Problem ?? request.Problem;
            ShowPopup(initial, problem, result.TargetWindow);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Selection search failed: {ExceptionType}", exception.GetType().Name);
        }
        finally { _running = false; }
    }

    private void ShowPopup(string query, string? problem, IntPtr returnWindow)
    {
        var popup = new FileSearchPopup(query, problem, returnWindow, clipboard, SearchAsync);
        popup.Closed += (_, _) => _popup = null;
        _popup = popup;
        popup.Show();
    }

    private static Task<IReadOnlyList<FileSearchHit>> SearchAsync(FileSearchRequest request, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<FileSearchHit>>(() => EverythingIpcClient
            .Search(request.IndexQuery, SearchLimit, TimeSpan.FromSeconds(3), cancellationToken)
            .Select(hit => new FileSearchHit(hit.FullPath, hit.IsFolder, hit.Size, hit.ModifiedUtc))
            .ToList(), cancellationToken);

    public void Dispose()
    {
        _gesture?.Dispose();
        _gesture = null;
        _hotkey.Dispose();
        _toolbar?.Close();
        _popup?.Close();
    }

    /// <summary>A small "找文件" button that never takes focus, so the chat app keeps its selection.</summary>
    private sealed class SelectionToolbar : Window
    {
        private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(4) };

        public SelectionToolbar(Action clicked)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            if (TryFindResource("DialogWindowStyle") is Style style) Style = style;
            var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(2) };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
            var button = new Button { Content = "🔍 找文件", Padding = new Thickness(10, 3, 10, 3), Focusable = false, ToolTip = "在本机搜索选中的文件名（Everything）" };
            button.Click += (_, _) => { Hide(); clicked(); };
            border.Child = button;
            Content = border;
            _hide.Tick += (_, _) => Hide();
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLongPtr(handle, -20);
                SetWindowLongPtr(handle, -20, style | 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */);
            };
            IsVisibleChanged += (_, _) => { if (!IsVisible) _hide.Stop(); };
        }

        public void ShowAt(System.Windows.Point screenPixels)
        {
            Show();
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new IntPtr(-1), (int)screenPixels.X + 10, (int)screenPixels.Y + 14, 0, 0, 0x0001 | 0x0010);
            _hide.Stop();
            _hide.Start();
        }

        public bool ContainsScreenPoint(System.Windows.Point screenPixels) =>
            GetWindowRect(new WindowInteropHelper(this).Handle, out var rect) &&
            screenPixels.X >= rect.Left && screenPixels.X < rect.Right && screenPixels.Y >= rect.Top && screenPixels.Y < rect.Bottom;

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(IntPtr window, int index, nint value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    }
}
