using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ClipboardManager.Windows.Interop;

namespace ClipboardManager.Windows.Services;

public interface ISelectionGestureMonitor : IDisposable
{
    event Action<System.Windows.Point>? Selected;
    event Action<System.Windows.Point>? MousePressed;
    void Start();
    void Stop();
}

public sealed class SelectionGestureMonitor : ISelectionGestureMonitor
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly MouseHook _callback;
    private IntPtr _hook, _window;
    private SelectionNative.Point _start, _lastClick;
    private long _lastUp;
    private bool _down;
    public event Action<System.Windows.Point>? Selected;
    public event Action<System.Windows.Point>? MousePressed;

    public SelectionGestureMonitor() => _callback = OnMouse;
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册鼠标选区监听。");
    }
    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _down = false; _lastUp = 0;
    }
    public void Dispose() => Stop();

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        // Only record gestures here; clipboard access runs later on the dispatcher.
        if (code >= 0 && (message == new IntPtr(0x201) || message == new IntPtr(0x202)))
        {
            var point = Marshal.PtrToStructure<MouseData>(data).Point;
            if (message == new IntPtr(0x201))
            {
                _down = true; _start = point; _window = NativeMethods.GetForegroundWindow();
                // The compact no-activate toolbar does not always deactivate when the user
                // clicks its original application. Report the next press so it can dismiss
                // itself only when the screen point is outside its bounds.
                _dispatcher.BeginInvoke(() => MousePressed?.Invoke(new System.Windows.Point(point.X, point.Y)),
                    DispatcherPriority.Background);
            }
            else if (_down)
            {
                _down = false;
                if (_window == NativeMethods.GetForegroundWindow())
                {
                    NativeMethods.GetWindowThreadProcessId(_window, out var process);
                    if (process != 0 && process != (uint)Environment.ProcessId)
                    {
                        var now = Environment.TickCount64;
                        bool drag = Math.Abs(point.X - _start.X) + Math.Abs(point.Y - _start.Y) >= 8;
                        bool doubleClick = now - _lastUp < NativeMethods.GetDoubleClickTime() &&
                            Math.Abs(point.X - _lastClick.X) + Math.Abs(point.Y - _lastClick.Y) < 8;
                        _lastUp = now; _lastClick = point;
                        if (drag || doubleClick)
                        {
                            var expectedWindow = _window;
                            _dispatcher.BeginInvoke(() =>
                            {
                                if (_hook != IntPtr.Zero && expectedWindow == NativeMethods.GetForegroundWindow())
                                    Selected?.Invoke(new System.Windows.Point(point.X, point.Y));
                            }, DispatcherPriority.Background);
                        }
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public SelectionNative.Point Point; public uint Mouse, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
