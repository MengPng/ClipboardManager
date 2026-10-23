using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Keeps the clipboard panel beside a supported chat window without activating
/// or modifying the target process.
/// </summary>
public sealed class WindowDockingService : IDisposable
{
    private static readonly HashSet<string> SupportedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WeChat",
        "Weixin",
        "WXWork"
    };

    private readonly WinEventDelegate _locationChangedDelegate;
    private readonly WinEventDelegate _targetStateChangedDelegate;
    private IntPtr _locationHook;
    private IntPtr _minimizeHook;
    private IntPtr _visibilityHook;
    private IntPtr _destroyHook;
    private IntPtr _targetWindow;
    private Window? _panel;
    private bool _updatePending;
    private bool _hiddenForTargetState;

    public WindowDockingService()
    {
        _locationChangedDelegate = OnLocationChanged;
        _targetStateChangedDelegate = OnTargetStateChanged;
    }

    public bool IsDocked => _targetWindow != IntPtr.Zero && _panel is not null;
    public bool IsTemporarilyHidden => IsDocked && _hiddenForTargetState;

    public bool TryDockToForeground(Window panel)
    {
        var target = GetForegroundWindow();
        if (!IsSupportedTarget(target))
        {
            Undock();
            return false;
        }

        Undock();
        _targetWindow = target;
        _panel = panel;
        InstallHooks();
        QueuePositionUpdate();
        return true;
    }

    public bool ShouldRemainVisibleAfterDeactivation()
    {
        if (!IsDocked || !IsWindow(_targetWindow) || IsIconic(_targetWindow)) return false;

        var foreground = GetForegroundWindow();
        if (foreground == _targetWindow) return true;

        GetWindowThreadProcessId(_targetWindow, out var targetProcessId);
        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        return targetProcessId != 0 && targetProcessId == foregroundProcessId;
    }

    public void Undock()
    {
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        if (_minimizeHook != IntPtr.Zero)
        {
            UnhookWinEvent(_minimizeHook);
            _minimizeHook = IntPtr.Zero;
        }

        if (_destroyHook != IntPtr.Zero)
        {
            UnhookWinEvent(_destroyHook);
            _destroyHook = IntPtr.Zero;
        }

        if (_visibilityHook != IntPtr.Zero)
        {
            UnhookWinEvent(_visibilityHook);
            _visibilityHook = IntPtr.Zero;
        }

        _targetWindow = IntPtr.Zero;
        _panel = null;
        _updatePending = false;
        _hiddenForTargetState = false;
    }

    private static bool IsSupportedTarget(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)) return false;
        if (!GetWindowRect(window, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return SupportedProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private void InstallHooks()
    {
        GetWindowThreadProcessId(_targetWindow, out var processId);
        if (processId == 0) return;

        _locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero,
            _locationChangedDelegate, processId, 0, WinEventOutOfContext);
        _minimizeHook = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero,
            _targetStateChangedDelegate, processId, 0, WinEventOutOfContext);
        _destroyHook = SetWinEventHook(EventObjectDestroy, EventObjectDestroy, IntPtr.Zero,
            _targetStateChangedDelegate, processId, 0, WinEventOutOfContext);
        _visibilityHook = SetWinEventHook(EventObjectShow, EventObjectHide, IntPtr.Zero,
            _targetStateChangedDelegate, processId, 0, WinEventOutOfContext);
    }

    private void OnLocationChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window == _targetWindow && objectId == ObjectIdWindow) QueuePositionUpdate();
    }

    private void OnTargetStateChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window != _targetWindow || (objectId != ObjectIdWindow && objectId != 0)) return;

        if (eventType == EventObjectDestroy)
        {
            var panel = _panel;
            panel?.Dispatcher.BeginInvoke(() => { panel.Hide(); Undock(); });
            return;
        }

        if (eventType is EventSystemMinimizeStart or EventObjectHide)
        {
            _hiddenForTargetState = true;
            var panel = _panel;
            panel?.Dispatcher.BeginInvoke(() => panel.Hide());
            return;
        }

        if (eventType is EventSystemMinimizeEnd or EventObjectShow) RestorePanelAfterTargetReturns();
    }

    private void RestorePanelAfterTargetReturns()
    {
        var panel = _panel;
        if (panel is null) return;
        panel.Dispatcher.BeginInvoke(async () =>
        {
            // Some WeChat/WeCom builds emit the restore event before their top-level
            // window becomes visible. Retry briefly so the panel is not left hidden.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(attempt == 0 ? 80 : 100);
                if (!IsWindow(_targetWindow)) return;
                if (IsIconic(_targetWindow) || !IsWindowVisible(_targetWindow)) continue;

                _hiddenForTargetState = false;
                panel.ShowActivated = false;
                if (!panel.IsVisible) panel.Show();
                PositionPanel();
                return;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Reposition() => QueuePositionUpdate();

    /// <summary>
    /// Hands the current dock target to another window without tearing down the
    /// hooks — used when the collapsed desktop pet inherits the panel's docking,
    /// and when the panel takes it back on expand. A fresh hook install would
    /// lose position continuity for the same target.
    /// </summary>
    public void SwapPanel(Window panel)
    {
        _panel = panel;
        _hiddenForTargetState = false;
        QueuePositionUpdate();
    }

    private void QueuePositionUpdate()
    {
        var panel = _panel;
        if (panel is null || _updatePending) return;
        _updatePending = true;
        panel.Dispatcher.BeginInvoke(() =>
        {
            _updatePending = false;
            PositionPanel();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void PositionPanel()
    {
        var panel = _panel;
        if (panel is null || !panel.IsVisible || !IsWindow(_targetWindow) || IsIconic(_targetWindow)) return;
        if (!GetWindowRect(_targetWindow, out var targetBounds)) return;

        var monitor = MonitorFromWindow(_targetWindow, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var panelHandle = new WindowInteropHelper(panel).EnsureHandle();
        var dpi = GetDpiForWindow(_targetWindow);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var width = Math.Min((int)Math.Ceiling(panel.ActualWidth * scale), monitorInfo.Work.Right - monitorInfo.Work.Left);
        var height = Math.Min((int)Math.Ceiling(panel.ActualHeight * scale), monitorInfo.Work.Bottom - monitorInfo.Work.Top);
        if (width <= 0 || height <= 0) return;

        const int gap = 8;
        var rightX = targetBounds.Right + gap;
        var leftX = targetBounds.Left - gap - width;
        var x = rightX + width <= monitorInfo.Work.Right
            ? rightX
            : leftX >= monitorInfo.Work.Left
                ? leftX
                : Math.Clamp(rightX, monitorInfo.Work.Left, monitorInfo.Work.Right - width);
        var y = Math.Clamp(targetBounds.Top, monitorInfo.Work.Top, monitorInfo.Work.Bottom - height);

        SetWindowPos(panelHandle, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
    }

    public void Dispose() => Undock();

    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
