using System.Runtime.InteropServices;

namespace ClipboardManager.Windows.Services;

internal static class SelectionNative
{
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint GlobalSize(IntPtr data);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern IntPtr CopyImage(IntPtr image, uint type, int width, int height, uint flags);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CopyEnhMetaFile(IntPtr value, string? file);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteEnhMetaFile(IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetObject(IntPtr value, int bufferLength, out Bitmap valueBuffer);
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern uint GetEnhMetaFileBits(IntPtr value, uint bufferSize, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }
}
