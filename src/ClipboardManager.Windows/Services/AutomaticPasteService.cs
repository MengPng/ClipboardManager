using ClipboardManager.Windows.Interop;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ClipboardManager.Windows.Services;

public sealed class AutomaticPasteService : IDisposable
{
    private IntPtr _targetWindow;
    private IntPtr _hook;
    private readonly NativeMethods.WinEventDelegate _hookDelegate;
    private readonly uint _currentProcessId;
    private bool _targetLocked;
    public string? LastFailureReason { get; private set; }

    public AutomaticPasteService()
    {
        _currentProcessId = (uint)Environment.ProcessId;
        _hookDelegate = OnWinEvent;
        try
        {
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EventSystemForeground,
                NativeMethods.EventSystemForeground,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                NativeMethods.WinEventOutOfContext);
        }
        catch
        {
            // If SetWinEventHook fails under restrictive security policies, fallback to GetForegroundWindow
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_targetLocked || hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && pid != _currentProcessId)
        {
            _targetWindow = hwnd;
        }
    }

    public void RememberCurrentTarget()
    {
        var fore = NativeMethods.GetForegroundWindow();
        if (fore != IntPtr.Zero && NativeMethods.IsWindow(fore))
        {
            NativeMethods.GetWindowThreadProcessId(fore, out uint pid);
            if (pid != 0 && pid != _currentProcessId)
            {
                _targetWindow = fore;
                // Keep the exact window that was foreground immediately before the
                // manager opens. Clipboard notifications and shell transitions must
                // not replace it before the user chooses an item.
                _targetLocked = true;
            }
        }
    }

    public async Task<bool> TryPasteAsync(Window? activeManagerWindow = null, CancellationToken cancellationToken = default)
    {
        LastFailureReason = null;
        var target = _targetWindow;
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            RememberCurrentTarget();
            target = _targetWindow;
        }
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            LastFailureReason = "未找到原目标窗口";
            return false;
        }

        bool wasTopmost = activeManagerWindow?.Topmost == true;
        if (wasTopmost && activeManagerWindow is not null)
        {
            activeManagerWindow.Topmost = false;
        }

        try
        {
            uint currentThreadId = NativeMethods.GetCurrentThreadId();
            uint targetThreadId = NativeMethods.GetWindowThreadProcessId(target, out _);
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            uint foregroundThreadId = foregroundWindow == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
            bool attachedTarget = false;
            bool attachedForeground = false;
            if (currentThreadId != targetThreadId && targetThreadId != 0)
            {
                attachedTarget = NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
            }
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId && foregroundThreadId != targetThreadId)
            {
                attachedForeground = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            try
            {
                if (NativeMethods.IsIconic(target))
                {
                    NativeMethods.ShowWindow(target, NativeMethods.SwRestore);
                }
                NativeMethods.BringWindowToTop(target);
                // The return value can be zero when the target is already foreground.
                // Verify the actual foreground window after the focus transition instead.
                NativeMethods.SetForegroundWindow(target);
                // Do not call SetFocus on the top-level window: doing so replaces the
                // dialog's focused edit control, so Ctrl+V never reaches the textbox.
            }
            finally
            {
                if (attachedForeground) NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
                if (attachedTarget) NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            // Brief delay for the target application (e.g. WeChat, text editors) to process focus transition
            await Task.Delay(80, cancellationToken);
            if (NativeMethods.GetForegroundWindow() != target)
            {
                LastFailureReason = "原目标窗口未成为前台窗口";
                return false;
            }

            var inputs = new NativeMethods.INPUT[]
            {
                Key(NativeMethods.VkControl, false), Key(NativeMethods.VkV, false),
                Key(NativeMethods.VkV, true), Key(NativeMethods.VkControl, true)
            };
            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent == inputs.Length) return true;
            LastFailureReason = $"系统仅发送了 {sent}/{inputs.Length} 个粘贴按键事件";
            return false;
        }
        finally
        {
            _targetLocked = false;
            if (wasTopmost && activeManagerWindow is not null)
            {
                // Restore topmost status after keystrokes have been dispatched
                await Task.Delay(120, cancellationToken);
                activeManagerWindow.Topmost = true;
            }
        }
    }

    private static NativeMethods.INPUT Key(ushort key, bool up) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = key, Flags = up ? NativeMethods.KeyEventKeyUp : 0 } }
    };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
