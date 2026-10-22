using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using ClipboardManager.Windows.Interop;

namespace ClipboardManager.Windows.Services;

public interface ISelectionTextCapture
{
    Task<string?> CaptureAsync(Window owner, CancellationToken cancellationToken = default);
}

public sealed class SelectionTextCapture(
    SelectionClipboardGate gate, IAppSettingsStore settings, ForegroundApplicationService foreground) : ISelectionTextCapture
{
    private bool _busy;

    public async Task<string?> CaptureAsync(Window owner, CancellationToken cancellationToken = default)
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            var config = await settings.LoadAsync(cancellationToken);
            if (!CapturePrivacyPolicy.CanRead(config, foreground.GetCurrentSource(), DateTimeOffset.UtcNow))
                throw new InvalidOperationException("监听已暂停或当前应用已排除，划词未读取内容。");
            var target = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(target, out var process);
            if (process == 0 || process == (uint)Environment.ProcessId) return null;
            // Do not synthesize key-up events for modifiers the user is still holding.
            for (int i = 0; i < 40 && ModifiersDown(); i++) await Task.Delay(25, cancellationToken);
            if (ModifiersDown() || NativeMethods.GetForegroundWindow() != target) return null;
            var handle = new WindowInteropHelper(owner).EnsureHandle();
            SelectionClipboardSnapshot? snapshot = null;
            var initialOwner = foreground.GetClipboardOwner();
            if (!CapturePrivacyPolicy.CanRead(config, initialOwner.Source, DateTimeOffset.UtcNow, initialOwner.IsKnown))
                throw new InvalidOperationException("当前剪切板来源已排除或无法确认，划词未读取内容。");
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { snapshot = SelectionClipboardSnapshot.Capture(handle); break; }
                catch (System.ComponentModel.Win32Exception) when (attempt < 3) { await Task.Delay(30, cancellationToken); }
            }
            using var saved = snapshot!;
            var snapshotOwner = foreground.GetClipboardOwner();
            if (!CapturePrivacyPolicy.CanRead(config, snapshotOwner.Source, DateTimeOffset.UtcNow, snapshotOwner.IsKnown) ||
                snapshotOwner.Window != saved.OwnerWindow ||
                NativeMethods.GetForegroundWindow() != target || NativeMethods.GetClipboardSequenceNumber() != saved.Sequence) return null;
            gate.Begin(process);
            uint copiedSequence = 0;
            try
            {
                var keys = new[] { Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true) };
                if (NativeMethods.SendInput(4, keys, Marshal.SizeOf<NativeMethods.INPUT>()) != 4)
                {
                    NativeMethods.SendInput(2, [Key(0x43, true), Key(0x11, true)], Marshal.SizeOf<NativeMethods.INPUT>());
                    await Task.Delay(50);
                    throw new InvalidOperationException("无法向当前应用发送复制按键，请手动复制后重试。");
                }
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(25, cancellationToken);
                    var sequence = NativeMethods.GetClipboardSequenceNumber();
                    if (sequence == saved.Sequence) continue;
                    if (!gate.Observe(sequence)) throw new InvalidOperationException("划词期间其他应用修改了剪切板，已保留最新内容。");
                    copiedSequence = sequence;
                    if (NativeMethods.GetForegroundWindow() != target) throw new InvalidOperationException("目标窗口已改变，已取消划词。");
                    if (!NativeMethods.OpenClipboard(handle)) continue;
                    try
                    {
                        if (NativeMethods.GetClipboardSequenceNumber() != sequence) continue;
                        var data = SelectionNative.GetClipboardData(NativeMethods.CfUnicodeText);
                        if (data == IntPtr.Zero) return null;
                        var bytes = SelectionNative.GlobalSize(data);
                        if (bytes > (SelectionAiService.MaxSelectionCharacters + 1) * 2)
                            throw new InvalidOperationException("所选文字超过 32000 字，请缩小选择范围。");
                        var pointer = NativeMethods.GlobalLock(data);
                        if (pointer == IntPtr.Zero) continue;
                        try
                        {
                            var text = ReadNullTerminatedText(pointer, checked((int)bytes));
                            return string.IsNullOrWhiteSpace(text) ? null : text;
                        }
                        finally { NativeMethods.GlobalUnlock(data); }
                    }
                    finally { NativeMethods.CloseClipboard(); }
                }
                return null;
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
                            if (restored) gate.Remember(NativeMethods.GetClipboardSequenceNumber());
                            if (!restored) await Task.Delay(30);
                        }
                        if (!restored) throw new InvalidOperationException("剪切板被占用或已改变，未覆盖最新内容；原剪切板未能恢复。");
                    }
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new InvalidOperationException("原剪切板未能完整恢复，请检查当前剪切板后重试。", ex);
                }
                finally { gate.End(); }
            }
        }
        finally { _busy = false; }
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
}
