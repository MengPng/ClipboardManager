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

public sealed class WindowsSystemClipboard : ISystemClipboard
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

    public Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken = default) => InvokeOnStaAsync(() =>
    {
        if (System.Windows.Clipboard.ContainsFileDropList())
            return new ClipboardPayload(ClipboardPayloadType.Files, Files: System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray());
        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image is null) return new ClipboardPayload(ClipboardPayloadType.None);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream);
            return new ClipboardPayload(ClipboardPayloadType.Image, ImageBytes: stream.ToArray(), ImageMimeType: "image/png");
        }
        if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            return new ClipboardPayload(ClipboardPayloadType.Text, Text: System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText));
        return new ClipboardPayload(ClipboardPayloadType.None);
    }, cancellationToken);

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

    private static bool TryWriteTextDirect(string text, IntPtr ownerWindow)
    {
        // Passing NULL here makes EmptyClipboard set the owner to NULL, after which
        // SetClipboardData can fail. The app's message window is a stable owner.
        if (ownerWindow == IntPtr.Zero || !NativeMethods.OpenClipboard(ownerWindow)) return false;
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
            NativeMethods.CloseClipboard();
        }
    }

    public Task WriteImageAsync(string imagePath, CancellationToken cancellationToken = default) => InvokeOnStaAsync<object?>(() =>
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(imagePath); image.EndInit(); image.Freeze();
        System.Windows.Clipboard.SetImage(image); return null;
    }, cancellationToken);

    public Task WriteFilesAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default) => InvokeOnStaAsync<object?>(() =>
    {
        var list = new StringCollection(); list.AddRange(files.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray());
        if (list.Count == 0) throw new InvalidOperationException("文件均已失效。");
        System.Windows.Clipboard.SetFileDropList(list); return null;
    }, cancellationToken);

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
