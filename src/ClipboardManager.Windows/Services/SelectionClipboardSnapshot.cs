using System.ComponentModel;
using System.Runtime.InteropServices;
using ClipboardManager.Windows.Interop;

namespace ClipboardManager.Windows.Services;

internal sealed class SelectionClipboardSnapshot : IDisposable
{
    private readonly List<(uint Format, IntPtr Handle)> _formats = [];
    public uint Sequence { get; private set; }
    public IntPtr OwnerWindow { get; private set; }

    public static SelectionClipboardSnapshot Capture(IntPtr owner)
    {
        if (!NativeMethods.OpenClipboard(owner)) throw new Win32Exception(Marshal.GetLastWin32Error(), "剪切板暂时被占用，请稍后再试。");
        var snapshot = new SelectionClipboardSnapshot();
        try
        {
            long total = 0;
            snapshot.OwnerWindow = SelectionNative.GetClipboardOwner();
            uint format = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                format = SelectionNative.EnumClipboardFormats(format);
                if (format == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0) throw new InvalidOperationException("无法完整枚举剪切板格式，已取消划词。");
                    break;
                }
                // Owner-display, palette, private/GDI and metafile-picture handles cannot
                // be safely treated as HGLOBAL. Abort before issuing Ctrl+C.
                if (format is 3 or 9 or 0x80 or >= 0x81 and <= 0x8E or >= 0x200 and <= 0x3FF)
                    throw new InvalidOperationException("当前剪切板含无法安全恢复的特殊格式，请先复制普通文本再使用划词。");
                // Windows synthesizes CF_BITMAP from DIB formats. Preserve their pixel
                // payload instead of cloning a potentially inaccessible synthesized GDI handle.
                if (format == 2 && (SelectionNative.IsClipboardFormatAvailable(8) || SelectionNative.IsClipboardFormatAvailable(17)))
                    continue;
                var source = SelectionNative.GetClipboardData(format);
                if (source == IntPtr.Zero) throw new InvalidOperationException("无法完整备份当前剪切板，已取消划词。");
                IntPtr clone;
                if (format == 2)
                {
                    if (SelectionNative.GetObject(source, Marshal.SizeOf<SelectionNative.Bitmap>(), out var bitmap) == 0 ||
                        !ClipboardResourceLimits.TryEstimateBitmapBytes(bitmap.Width, bitmap.Height, bitmap.Planes * bitmap.BitsPixel, out var size) ||
                        !ClipboardResourceLimits.TryAdd(total, size, ClipboardResourceLimits.MaxSnapshotBytes, out total))
                        throw new InvalidOperationException("剪切板位图无法安全备份或内容超过 64 MiB，已取消划词。");
                    clone = SelectionNative.CopyImage(source, 0, 0, 0, 0x2000);
                }
                else if (format == 14)
                {
                    var size = SelectionNative.GetEnhMetaFileBits(source, 0, IntPtr.Zero);
                    if (size == 0 ||
                        !ClipboardResourceLimits.TryAdd(total, size, ClipboardResourceLimits.MaxSnapshotBytes, out total))
                        throw new InvalidOperationException("剪切板增强图元文件无法安全备份或内容超过 64 MiB，已取消划词。");
                    clone = SelectionNative.CopyEnhMetaFile(source, null);
                }
                else
                {
                    var size = SelectionNative.GlobalSize(source);
                    if (size == 0 ||
                        !ClipboardResourceLimits.TryAdd(total, checked((long)size), ClipboardResourceLimits.MaxSnapshotBytes, out total))
                        throw new InvalidOperationException("剪切板格式不支持备份或内容超过 64 MiB，已取消划词。");
                    clone = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, size);
                    if (clone == IntPtr.Zero) throw new OutOfMemoryException();
                    var from = NativeMethods.GlobalLock(source);
                    var to = NativeMethods.GlobalLock(clone);
                    bool copied = false;
                    try
                    {
                        if (from == IntPtr.Zero || to == IntPtr.Zero) throw new InvalidOperationException("无法备份剪切板。");
                        unsafe { Buffer.MemoryCopy((void*)from, (void*)to, (long)size, (long)size); }
                        copied = true;
                    }
                    finally
                    {
                        if (from != IntPtr.Zero) NativeMethods.GlobalUnlock(source);
                        if (to != IntPtr.Zero) NativeMethods.GlobalUnlock(clone);
                        if (!copied) NativeMethods.GlobalFree(clone);
                    }
                }
                if (clone == IntPtr.Zero) throw new InvalidOperationException($"无法备份剪切板资源（格式 {format}）。");
                snapshot._formats.Add((format, clone));
            }
            snapshot.Sequence = NativeMethods.GetClipboardSequenceNumber();
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
        finally { NativeMethods.CloseClipboard(); }
    }

    public bool TryRestore(IntPtr owner, uint expectedSequence, SelectionClipboardGate gate)
    {
        if (!NativeMethods.OpenClipboard(owner)) return false;
        try
        {
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return false;
            gate.Restoring();
            if (!NativeMethods.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (int i = 0; i < _formats.Count; i++)
            {
                var value = _formats[i];
                if (NativeMethods.SetClipboardData(value.Format, value.Handle) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "恢复剪切板失败。");
                _formats[i] = (value.Format, IntPtr.Zero);
            }
            gate.Remember(NativeMethods.GetClipboardSequenceNumber());
            return true;
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    public void Dispose()
    {
        foreach (var (format, handle) in _formats)
        {
            if (handle == IntPtr.Zero) continue;
            if (format == 2) SelectionNative.DeleteObject(handle);
            else if (format == 14) SelectionNative.DeleteEnhMetaFile(handle);
            else NativeMethods.GlobalFree(handle);
        }
        _formats.Clear();
    }
}
