using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipboardManager.Windows.Services;

/// <summary>Places the panel inside the monitor under the cursor using physical pixels.</summary>
public static class DesktopWindowPlacement
{
    public static void PlaceAtCursor(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        // Moving into another monitor causes WPF to process its DPI change; remeasure then.
        SetWindowPos(handle, IntPtr.Zero, info.Work.Left, info.Work.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        window.Dispatcher.InvokeAsync(() =>
        {
            var currentDpi = GetDpiForWindow(handle);
            var currentScale = currentDpi == 0 ? scale : currentDpi / 96d;
            var width = Math.Min((int)Math.Ceiling(window.ActualWidth * currentScale), info.Work.Right - info.Work.Left);
            var height = Math.Min((int)Math.Ceiling(window.ActualHeight * currentScale), info.Work.Bottom - info.Work.Top);
            var x = Math.Clamp(cursor.X - width / 2, info.Work.Left, info.Work.Right - width);
            var y = Math.Clamp(cursor.Y - 40, info.Work.Top, info.Work.Bottom - height);
            SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
