using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipboardManager.Windows.Services;

public sealed record EverythingHit(string FullPath, bool IsFolder, long? Size, DateTime? ModifiedUtc);

public enum EverythingStatus
{
    Available,
    NotRunning,
    Refused,
    TimedOut,
}

public sealed class EverythingUnavailableException(EverythingStatus status, string message) : Exception(message)
{
    public EverythingStatus Status { get; } = status;
}

/// <summary>
/// Talks to a running Everything instance over its documented WM_COPYDATA IPC (QUERY2), the same
/// protocol the Everything SDK DLL uses, so no third-party binary ships with the app. Each search runs
/// on its own short-lived thread with a message-only reply window; nothing here touches the UI thread
/// or the clipboard listener. Only the query text the user explicitly asked for is sent, and only to
/// the local Everything process.
/// </summary>
public static class EverythingIpcClient
{
    private const int WmCopyData = 0x004A;
    private const uint CopyDataQuery2W = 18;
    private const uint ReplyId = 0x434D4553; // "CMES"
    private const uint RequestFullPathAndName = 0x00000004;
    private const uint RequestSize = 0x00000010;
    private const uint RequestDateModified = 0x00000040;
    private const uint SortDateModifiedDescending = 14;
    private const uint ItemFolder = 0x00000001;
    private const uint ItemDrive = 0x00000002;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly string[] WindowClasses =
        ["EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"];

    public const int MaxQueryLength = 512;

    public static bool IsRunning() => FindEverythingWindow() != IntPtr.Zero;

    public static IReadOnlyList<EverythingHit> Search(string query, int maxResults, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > MaxQueryLength) throw new ArgumentException("搜索内容过长。", nameof(query));
        if (maxResults is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxResults));
        IReadOnlyList<EverythingHit>? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = SearchOnThread(query, (uint)maxResults, timeout, cancellationToken); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "Everything IPC" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatch(failure);
        return result ?? [];
    }

    private static void ExceptionDispatch(Exception ex) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

    /// <summary>
    /// Prefers a standalone Everything (default instance, then 1.5 alpha). Any other named instance
    /// ("EVERYTHING_TASKBAR_NOTIFICATION_(name)", e.g. one bundled inside another product) is used only
    /// as a last resort, since its index may be narrower than the user's own installation.
    /// </summary>
    private static IntPtr FindEverythingWindow()
    {
        foreach (var name in WindowClasses)
        {
            var hwnd = FindWindowW(name, null);
            if (hwnd != IntPtr.Zero) return hwnd;
        }
        var fallback = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(256);
            if (GetClassNameW(hwnd, name, name.Capacity) > 0 && name.ToString().StartsWith(WindowClasses[0] + "_(", StringComparison.Ordinal))
            {
                fallback = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return fallback;
    }

    private static IReadOnlyList<EverythingHit> SearchOnThread(string query, uint maxResults, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var target = FindEverythingWindow();
        if (target == IntPtr.Zero) throw new EverythingUnavailableException(EverythingStatus.NotRunning, "未检测到正在运行的 Everything。");

        byte[]? reply = null;
        WndProc proc = (hwnd, message, wParam, lParam) =>
        {
            if (message == WmCopyData && lParam != IntPtr.Zero)
            {
                var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
                if (data.dwData == (IntPtr)ReplyId && data.cbData > 0 && data.lpData != IntPtr.Zero)
                {
                    reply = new byte[data.cbData];
                    Marshal.Copy(data.lpData, reply, 0, data.cbData);
                    return new IntPtr(1);
                }
            }
            return DefWindowProcW(hwnd, message, wParam, lParam);
        };
        var className = "ClipboardManager.EverythingReply." + Environment.CurrentManagedThreadId + "." + Guid.NewGuid().ToString("N");
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = className,
        };
        if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var window = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            window = CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Everything is often elevated; allow its reply through UIPI.
            ChangeWindowMessageFilterEx(window, WmCopyData, 1, IntPtr.Zero);

            var payload = BuildQuery(window, query, maxResults);
            buffer = Marshal.AllocHGlobal(payload.Length);
            Marshal.Copy(payload, 0, buffer, payload.Length);
            var copy = new CopyDataStruct { dwData = (IntPtr)CopyDataQuery2W, cbData = payload.Length, lpData = buffer };
            var sent = SendMessageTimeoutW(target, WmCopyData, window, ref copy, 0x0002 /* SMTO_ABORTIFHUNG */, (uint)Math.Max(100, timeout.TotalMilliseconds), out var accepted);
            if (sent == IntPtr.Zero || accepted == IntPtr.Zero)
                throw new EverythingUnavailableException(EverythingStatus.Refused, "Everything 没有接受查询请求（可能权限不同或版本不支持）。");

            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (reply is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) throw new EverythingUnavailableException(EverythingStatus.TimedOut, "Everything 响应超时。");
                MsgWaitForMultipleObjects(0, IntPtr.Zero, false, (uint)Math.Min(remaining, 50), 0x04FF);
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            return ParseReply(reply);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (window != IntPtr.Zero) DestroyWindow(window);
            UnregisterClassW(className, windowClass.hInstance);
            GC.KeepAlive(proc);
        }
    }

    internal static byte[] BuildQuery(IntPtr replyWindow, string query, uint maxResults)
    {
        var text = Encoding.Unicode.GetBytes(query + "\0");
        var payload = new byte[7 * 4 + text.Length];
        var header = new uint[] { unchecked((uint)replyWindow.ToInt64()), ReplyId, 0, 0, maxResults, RequestFullPathAndName | RequestSize | RequestDateModified, SortDateModifiedDescending };
        Buffer.BlockCopy(header, 0, payload, 0, header.Length * 4);
        Buffer.BlockCopy(text, 0, payload, header.Length * 4, text.Length);
        return payload;
    }

    /// <summary>Parses EVERYTHING_IPC_LIST2: five DWORD header, ITEM2 table, then per-item data in request-flag order.</summary>
    internal static IReadOnlyList<EverythingHit> ParseReply(byte[] reply)
    {
        if (reply.Length < 20) throw new InvalidDataException("Everything 返回的数据不完整。");
        var count = BitConverter.ToUInt32(reply, 4);
        var flags = BitConverter.ToUInt32(reply, 12);
        if (count > 100000 || 20 + count * 8L > reply.Length) throw new InvalidDataException("Everything 返回的数据不完整。");
        var hits = new List<EverythingHit>((int)count);
        for (var i = 0; i < count; i++)
        {
            var itemFlags = BitConverter.ToUInt32(reply, 20 + i * 8);
            var offset = (int)BitConverter.ToUInt32(reply, 24 + i * 8);
            string? path = null; long? size = null; DateTime? modified = null;
            var p = offset;
            if ((flags & 0x1) != 0) p = SkipString(reply, p);
            if ((flags & 0x2) != 0) p = SkipString(reply, p);
            if ((flags & RequestFullPathAndName) != 0) { path = ReadString(reply, p); p = SkipString(reply, p); }
            if ((flags & 0x8) != 0) p = SkipString(reply, p);
            if ((flags & RequestSize) != 0)
            {
                Ensure(reply, p, 8);
                var value = BitConverter.ToInt64(reply, p); p += 8;
                if (value >= 0) size = value;
            }
            if ((flags & 0x20) != 0) p += 8;
            if ((flags & RequestDateModified) != 0)
            {
                Ensure(reply, p, 8);
                var value = BitConverter.ToInt64(reply, p);
                if (value > 0 && value < DateTime.MaxValue.ToFileTimeUtc()) modified = DateTime.FromFileTimeUtc(value);
            }
            if (!string.IsNullOrEmpty(path)) hits.Add(new EverythingHit(path, (itemFlags & (ItemFolder | ItemDrive)) != 0, size, modified));
        }
        return hits;
    }

    private static void Ensure(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length) throw new InvalidDataException("Everything 返回的数据不完整。");
    }

    private static string ReadString(byte[] data, int offset)
    {
        Ensure(data, offset, 4);
        var length = (int)BitConverter.ToUInt32(data, offset);
        Ensure(data, offset + 4, length * 2);
        return Encoding.Unicode.GetString(data, offset + 4, length * 2);
    }

    private static int SkipString(byte[] data, int offset)
    {
        Ensure(data, offset, 4);
        var length = (int)BitConverter.ToUInt32(data, offset);
        return offset + 4 + (length + 1) * 2;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, ref CopyDataStruct lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint milliseconds, uint wakeMask);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
