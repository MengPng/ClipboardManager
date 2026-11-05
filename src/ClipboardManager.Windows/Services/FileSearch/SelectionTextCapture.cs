using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace ClipboardManager.Windows.Services;

public sealed record SelectionCapture(string? Text, IntPtr TargetWindow, string? Problem)
{
    public static SelectionCapture Fail(IntPtr target, string problem) => new(null, target, problem);
}

/// <summary>
/// Reads the text the user selected in the foreground application, only when the user asked
/// for it (hotkey or toolbar click). UI Automation is tried first because it leaves the
/// clipboard untouched; the fallback sends Ctrl+C, reads the text and restores the previous
/// clipboard so nothing lands in history (ADR-0007). Selected text is never logged.
/// </summary>
public sealed class SelectionTextCapture(
    SelectionClipboardGate gate, IAppSettingsStore settings, ForegroundApplicationService foreground,
    ILogger<SelectionTextCapture> logger)
{
    public const int MaxSelectionCharacters = 4000;
    private static readonly TimeSpan AutomationTimeout = TimeSpan.FromMilliseconds(700);

    // Ctrl+C in a console interrupts the running program instead of copying.
    private static readonly string[] ConsoleClasses = ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "VirtualConsoleClass", "PuTTY"];
    private static readonly string[] ConsoleProcesses = ["WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "wezterm-gui", "alacritty", "mintty", "putty", "OpenConsole"];

    private bool _busy;

    public async Task<SelectionCapture> CaptureAsync(Window owner, CancellationToken cancellationToken = default)
    {
        var target = NativeMethods.GetForegroundWindow();
        if (_busy) return SelectionCapture.Fail(target, "上一次划词还没结束。");
        _busy = true;
        try
        {
            NativeMethods.GetWindowThreadProcessId(target, out var process);
            if (target == IntPtr.Zero || process == 0 || process == (uint)Environment.ProcessId)
                return SelectionCapture.Fail(target, "请先在其他应用里选中文字，再按快捷键。");
            var config = await settings.LoadAsync(cancellationToken);
            var source = foreground.GetCurrentSource();
            if (CapturePrivacyPolicy.IsPaused(config, DateTimeOffset.UtcNow))
                return SelectionCapture.Fail(target, "监听已暂停，划词不会读取任何内容。");
            if (CapturePrivacyPolicy.IsExcluded(source.ProcessName, config.ExcludedProcesses))
                return SelectionCapture.Fail(target, "当前应用在排除列表中，划词不会读取它的内容。");

            var automationText = await TryReadWithAutomationAsync(cancellationToken);
            if (automationText is not null) return Accept(automationText, target);

            if (IsConsole(target, source.ProcessName))
                return SelectionCapture.Fail(target, "终端窗口里按 Ctrl+C 会中断程序，请先手动复制再搜索。");
            return await CaptureWithCopyAsync(owner, config, target, process, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException exception)
        {
            return SelectionCapture.Fail(target, exception.Message);
        }
        catch (Exception exception)
        {
            // Type only: the exception message never contains the selected text, but stay conservative.
            logger.LogWarning("Selection capture failed: {ExceptionType}", exception.GetType().Name);
            return SelectionCapture.Fail(target, "没能读取选中的文字，请手动复制后重试。");
        }
        finally { _busy = false; }
    }

    private static SelectionCapture Accept(string text, IntPtr target)
    {
        if (text.Length > MaxSelectionCharacters) return SelectionCapture.Fail(target, "选中的文字太长，请只选文件名或关键词。");
        return string.IsNullOrWhiteSpace(text)
            ? SelectionCapture.Fail(target, "没有读到选中的文字。")
            : new SelectionCapture(text, target, null);
    }

    private async Task<string?> TryReadWithAutomationAsync(CancellationToken cancellationToken)
    {
        // UIA calls are cross-process and can hang on a busy target; never on the UI thread, never unbounded.
        var read = Task.Run(() =>
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused is null || !focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
                var ranges = ((TextPattern)pattern).GetSelection();
                if (ranges.Length == 0) return null;
                var text = string.Concat(ranges.Select(range => range.GetText(MaxSelectionCharacters + 1)));
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
                or COMException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }, cancellationToken);
        var finished = await Task.WhenAny(read, Task.Delay(AutomationTimeout, cancellationToken));
        if (finished != read)
        {
            _ = read.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            logger.LogInformation("UI Automation selection read timed out; falling back to copy.");
            return null;
        }
        return await read;
    }

    private static bool IsConsole(IntPtr window, string? processName)
    {
        var buffer = new char[256];
        var length = GetClassNameW(window, buffer, buffer.Length);
        var className = length > 0 ? new string(buffer, 0, length) : string.Empty;
        return ConsoleClasses.Any(name => className.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ||
               (processName is not null && ConsoleProcesses.Any(name =>
                   string.Equals(Path.GetFileNameWithoutExtension(processName), name, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<SelectionCapture> CaptureWithCopyAsync(
        Window owner, ClipboardManager.Domain.Models.AppSettings config, IntPtr target, uint process, CancellationToken cancellationToken)
    {
        // Do not synthesize Ctrl+C while the user still holds the hotkey's modifiers.
        for (int i = 0; i < 60 && ModifiersDown(); i++) await Task.Delay(25, cancellationToken);
        if (ModifiersDown()) return SelectionCapture.Fail(target, "请松开快捷键后再试。");
        if (NativeMethods.GetForegroundWindow() != target) return SelectionCapture.Fail(target, "目标窗口已切换，已取消。");
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        var initialOwner = foreground.GetClipboardOwner();
        if (!CapturePrivacyPolicy.CanRead(config, initialOwner.Source, DateTimeOffset.UtcNow, initialOwner.IsKnown))
            return SelectionCapture.Fail(target, "当前剪切板来源已排除或无法确认，划词未读取内容。");
        SelectionClipboardSnapshot? snapshot = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { snapshot = SelectionClipboardSnapshot.Capture(handle); break; }
            catch (System.ComponentModel.Win32Exception) when (attempt < 3) { await Task.Delay(30, cancellationToken); }
        }
        using var saved = snapshot!;
        var snapshotOwner = foreground.GetClipboardOwner();
        if (snapshotOwner.Window != saved.OwnerWindow ||
            NativeMethods.GetForegroundWindow() != target || NativeMethods.GetClipboardSequenceNumber() != saved.Sequence)
            return SelectionCapture.Fail(target, "剪切板正在变化，请稍后再试。");
        gate.Begin(process);
        uint copiedSequence = 0;
        try
        {
            var keys = new[] { Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true) };
            if (NativeMethods.SendInput(4, keys, Marshal.SizeOf<NativeMethods.INPUT>()) != 4)
            {
                NativeMethods.SendInput(2, [Key(0x43, true), Key(0x11, true)], Marshal.SizeOf<NativeMethods.INPUT>());
                await Task.Delay(50, CancellationToken.None);
                return SelectionCapture.Fail(target, "无法向当前应用发送复制按键（可能是管理员权限窗口），请手动复制后重试。");
            }
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(25, cancellationToken);
                var sequence = NativeMethods.GetClipboardSequenceNumber();
                if (sequence == saved.Sequence) continue;
                if (!gate.Observe(sequence)) return SelectionCapture.Fail(target, "划词期间其他应用修改了剪切板，已保留最新内容。");
                copiedSequence = sequence;
                if (!NativeMethods.OpenClipboard(handle)) continue;
                try
                {
                    if (NativeMethods.GetClipboardSequenceNumber() != sequence) continue;
                    var data = SelectionNative.GetClipboardData(NativeMethods.CfUnicodeText);
                    if (data == IntPtr.Zero) return SelectionCapture.Fail(target, "选中的内容不是文字。");
                    var bytes = SelectionNative.GlobalSize(data);
                    if (bytes > (nuint)((MaxSelectionCharacters + 1) * 2))
                        return SelectionCapture.Fail(target, "选中的文字太长，请只选文件名或关键词。");
                    var pointer = NativeMethods.GlobalLock(data);
                    if (pointer == IntPtr.Zero) continue;
                    try { return Accept(ReadNullTerminatedText(pointer, checked((int)bytes)), target); }
                    finally { NativeMethods.GlobalUnlock(data); }
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            return SelectionCapture.Fail(target, "没有读到选中的文字。请确认已选中文字，或手动复制后重试。");
        }
        finally
        {
            try
            {
                if (copiedSequence == 0)
                {
                    var sequence = NativeMethods.GetClipboardSequenceNumber();
                    if (sequence != saved.Sequence && gate.Observe(sequence)) copiedSequence = sequence;
                }
                if (copiedSequence != 0 && NativeMethods.GetClipboardSequenceNumber() == copiedSequence)
                {
                    var restored = false;
                    for (int i = 0; i < 4 && !restored; i++)
                    {
                        restored = saved.TryRestore(handle, copiedSequence, gate);
                        if (!restored) await Task.Delay(30, CancellationToken.None);
                    }
                    if (!restored) logger.LogWarning("Selection capture could not restore the previous clipboard.");
                }
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                logger.LogWarning("Selection capture restore failed: {Error}", exception.NativeErrorCode);
            }
            finally { gate.End(); }
        }
    }

    private static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (SelectionNative.GetAsyncKeyState(key) & 0x8000) != 0);

    internal static unsafe string ReadNullTerminatedText(IntPtr pointer, int byteCount)
    {
        if (byteCount % 2 != 0) throw new InvalidOperationException("剪切板文本格式不完整。");
        ReadOnlySpan<char> characters = new((char*)pointer, byteCount / 2);
        int end = characters.IndexOf('\0');
        if (end < 0) throw new InvalidOperationException("剪切板文本缺少结束标记。");
        return new string(characters[..end]);
    }

    private static NativeMethods.INPUT Key(ushort key, bool up) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = key, Flags = up ? NativeMethods.KeyEventKeyUp : 0 } }
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, char[] buffer, int length);
}
