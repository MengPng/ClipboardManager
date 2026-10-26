using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Interop;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Windows.Services;

public sealed class WindowsSystemClipboard : ISystemClipboard, IClipboardTextRewriter
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(160),
        TimeSpan.FromMilliseconds(320),
        TimeSpan.FromMilliseconds(500)
    ];
    private static readonly TimeSpan LockWaitTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(25);

    // OLE pumps messages while marshaling clipboard data, so a queued write can re-enter the
    // dispatcher while our own read still holds the clipboard open. Serialize every access.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken = default)
    {
        var material = await InvokeOnStaAsync(ReadMaterial, cancellationToken);
        if (material.Image is null) return material.Payload;

        // Clipboard/OLE acquisition stays on the dispatcher. PNG encoding only touches a frozen
        // bitmap and can safely run elsewhere without holding the clipboard access gate.
        var imageBytes = await Task.Run(() => EncodePng(material.Image), cancellationToken);
        return new ClipboardPayload(ClipboardPayloadType.Image, ImageBytes: imageBytes, ImageMimeType: "image/png");
    }

    private static ClipboardReadMaterial ReadMaterial()
    {
        if (System.Windows.Clipboard.ContainsFileDropList())
            return new ClipboardReadMaterial(new ClipboardPayload(ClipboardPayloadType.Files, Files: System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray()));
        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image is null) return new ClipboardReadMaterial(new ClipboardPayload(ClipboardPayloadType.None));
            if (!ClipboardResourceLimits.TryEstimateBitmapBytes(
                    image.PixelWidth, image.PixelHeight, image.Format.BitsPerPixel, out _))
                throw new InvalidOperationException("剪切板图片解码后超过 64 MiB 安全限制。");
            var frozen = (BitmapSource)image.Clone();
            frozen.Freeze();
            return new ClipboardReadMaterial(new ClipboardPayload(ClipboardPayloadType.None), frozen);
        }
        if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            return new ClipboardReadMaterial(new ClipboardPayload(ClipboardPayloadType.Text, Text: System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText)));
        return new ClipboardReadMaterial(new ClipboardPayload(ClipboardPayloadType.None));
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new BoundedMemoryStream(ClipboardResourceLimits.MaxEncodedImageBytes);
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record ClipboardReadMaterial(ClipboardPayload Payload, BitmapSource? Image = null);

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => (await ReadAsync(cancellationToken)).Text;

    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default) => InvokeOnStaAsync<object?>(() =>
    {
        // Use the deterministic Win32 path first. If OLE fails after opening the clipboard,
        // an immediate raw fallback can still be denied until OLE releases it.
        if (!TryWriteTextDirect(text, GetClipboardOwnerWindow()))
            System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
        return null;
    }, cancellationToken);

    private static IntPtr GetClipboardOwnerWindow()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    public Task<bool> TryReplaceTextAsync(string expectedText, uint expectedSequence, string replacement,
        CancellationToken cancellationToken = default) => InvokeOnStaAsync(() =>
    {
        var owner = GetClipboardOwnerWindow();
        if (owner == IntPtr.Zero) throw new InvalidOperationException("剪切板窗口尚未就绪。");
        if (!NativeMethods.OpenClipboard(owner)) throw new ExternalException("系统剪切板被占用。", unchecked((int)0x800401D0));
        try
        {
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return false;
            var handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
            if (handle == IntPtr.Zero) return false;
            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            string? current;
            try { current = Marshal.PtrToStringUni(pointer); }
            finally { NativeMethods.GlobalUnlock(handle); }
            if (!string.Equals(current, expectedText, StringComparison.Ordinal)) return false;
            // Allocation and replacement occur while holding the same lock as the comparison.
            if (!TryWriteTextUnderLock(replacement))
            {
                var restored = TryWriteTextUnderLock(expectedText);
                throw new InvalidOperationException(restored
                    ? "计算结果写入失败，已恢复原算式，请稍后重试。"
                    : "计算结果写入失败，原内容恢复也失败；请重新复制。",
                    new ExternalException("系统剪切板写入失败。", unchecked((int)0x800401D0)));
            }
            return true;
        }
        finally { NativeMethods.CloseClipboard(); }
    }, cancellationToken);

    private static bool TryWriteTextDirect(string text, IntPtr ownerWindow)
    {
        // Passing NULL here makes EmptyClipboard set the owner to NULL, after which
        // SetClipboardData can fail. The app's message window is a stable owner.
        if (ownerWindow == IntPtr.Zero || !NativeMethods.OpenClipboard(ownerWindow)) return false;
        try { return TryWriteTextUnderLock(text); }
        finally { NativeMethods.CloseClipboard(); }
    }

    private static bool TryWriteTextUnderLock(string text)
    {
        var block = IntPtr.Zero;
        try
        {
            block = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)((text.Length + 1) * sizeof(char)));
            if (block == IntPtr.Zero) return false;

            var target = NativeMethods.GlobalLock(block);
            if (target == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally { NativeMethods.GlobalUnlock(block); }

            if (!NativeMethods.EmptyClipboard()) return false;
            if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, block) == IntPtr.Zero) return false;

            block = IntPtr.Zero; // The clipboard owns the block once SetClipboardData succeeds.
            return true;
        }
        finally
        {
            if (block != IntPtr.Zero) NativeMethods.GlobalFree(block);
        }
    }

    public Task WriteImageAsync(string imagePath, CancellationToken cancellationToken = default) => InvokeOnStaAsync<object?>(() =>
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(imagePath); image.EndInit(); image.Freeze();
        System.Windows.Clipboard.SetImage(image); return null;
    }, cancellationToken);

    public Task WriteFilesAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default) => InvokeOnStaAsync<object?>(() =>
    {
        var paths = files.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("文件均已失效。");
        // OleSetClipboard/Flush can collide with another process reading the clipboard
        // immediately after WM_CLIPBOARDUPDATE. CF_HDROP is a plain global memory block,
        // so write it during one bounded Win32 lock just like text.
        if (!TryWriteFilesDirect(paths, GetClipboardOwnerWindow()))
        {
            var list = new StringCollection();
            list.AddRange(paths);
            System.Windows.Clipboard.SetFileDropList(list);
        }
        return null;
    }, cancellationToken);

    private static bool TryWriteFilesDirect(IReadOnlyList<string> files, IntPtr ownerWindow)
    {
        if (ownerWindow == IntPtr.Zero || !NativeMethods.OpenClipboard(ownerWindow)) return false;
        try
        {
            const int dropFilesHeaderBytes = 20;
            var payload = string.Join('\0', files) + "\0\0";
            var block = NativeMethods.GlobalAlloc(
                NativeMethods.GmemMoveable,
                (nuint)(dropFilesHeaderBytes + payload.Length * sizeof(char)));
            if (block == IntPtr.Zero) return false;
            try
            {
                var target = NativeMethods.GlobalLock(block);
                if (target == IntPtr.Zero) return false;
                try
                {
                    // DROPFILES: pFiles, pt.X, pt.Y, fNC, fWide. The header is 20 bytes
                    // on both x86 and x64 and is followed by a double-null-terminated UTF-16 list.
                    Marshal.WriteInt32(target, 0, dropFilesHeaderBytes);
                    Marshal.WriteInt32(target, 4, 0);
                    Marshal.WriteInt32(target, 8, 0);
                    Marshal.WriteInt32(target, 12, 0);
                    Marshal.WriteInt32(target, 16, 1);
                    Marshal.Copy(payload.ToCharArray(), 0, IntPtr.Add(target, dropFilesHeaderBytes), payload.Length);
                }
                finally { NativeMethods.GlobalUnlock(block); }

                if (!NativeMethods.EmptyClipboard()) return false;
                if (NativeMethods.SetClipboardData(NativeMethods.CfHdrop, block) == IntPtr.Zero) return false;
                block = IntPtr.Zero; // The clipboard owns the block once SetClipboardData succeeds.
                return true;
            }
            finally
            {
                if (block != IntPtr.Zero) NativeMethods.GlobalFree(block);
            }
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    private static async Task<T> InvokeOnStaAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            Exception? last = null;
            foreach (var delay in RetryDelays)
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                await WaitForUnlockedClipboardAsync(cancellationToken);
                try { return await System.Windows.Application.Current.Dispatcher.InvokeAsync(operation).Task; }
                catch (ExternalException exception) { last = exception; }
            }
            throw new InvalidOperationException($"系统剪切板写入失败{DescribeHolder()}{DescribeError(last)}，请稍后重试。", last);
        }
        finally { Gate.Release(); }
    }

    // An OLE clipboard call blocks the STA thread for about a second when the lock is held,
    // so poll off the dispatcher first instead of stacking blocking attempts.
    private static async Task WaitForUnlockedClipboardAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LockWaitTimeout;
        while (NativeMethods.GetOpenClipboardWindow() != IntPtr.Zero && DateTime.UtcNow < deadline)
        {
            await Task.Delay(LockPollInterval, cancellationToken);
        }
    }

    private static string DescribeError(Exception? exception) =>
        exception is ExternalException external ? $"（错误码 0x{external.ErrorCode:X8}）" : string.Empty;

    private static string DescribeHolder()
    {
        var window = NativeMethods.GetOpenClipboardWindow();
        if (window == IntPtr.Zero) return string.Empty;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return $"（{process.ProcessName}）";
        }
        catch (ArgumentException) { return string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
        catch (System.ComponentModel.Win32Exception) { return string.Empty; }
    }
}
