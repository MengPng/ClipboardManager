using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Interop;
using System.Diagnostics;

namespace ClipboardManager.Windows.Services;

public sealed class ForegroundApplicationService
{
    public ClipboardSource GetCurrentSource()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return new ClipboardSource(null, null);
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return new ClipboardSource(null, null);
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            var title = process.MainWindowTitle;
            var applicationName = string.IsNullOrWhiteSpace(title) ? processName : processName;
            return new ClipboardSource(applicationName, processName);
        }
        catch (ArgumentException)
        {
            return new ClipboardSource(null, null);
        }
        catch (InvalidOperationException)
        {
            return new ClipboardSource(null, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ClipboardSource(null, null);
        }
    }
}
