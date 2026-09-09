using ClipboardManager.Windows.Interop;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Windows.Services;

/// <summary>
/// Builds the leading visual for a history or collection row: a decoded thumbnail for images and a
/// shell icon for files. A missing or damaged source yields <c>null</c> so the row still renders.
/// </summary>
internal static class ListPreviewProvider
{
    private const int ThumbnailWidth = 96;

    // Shell lookups are cached per extension so a virtualized list does not call the shell for every row.
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? LoadThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad keeps the file unlocked; DecodePixelWidth keeps a long list from holding full-size bitmaps.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = ThumbnailWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException) { return null; }
        catch (FileFormatException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static ImageSource? LoadFileIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var isDirectory = Directory.Exists(path);
        var key = CacheKey(path, isDirectory);
        if (IconCache.TryGetValue(key, out var cached)) return cached;

        var icon = ExtractIcon(path, isDirectory);
        IconCache[key] = icon;
        return icon;
    }

    /// <summary>Returns the type description Explorer shows, such as "PNG 图像" or "Microsoft Word 文档".</summary>
    public static string? LoadTypeName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var isDirectory = Directory.Exists(path);
        var key = CacheKey(path, isDirectory);
        if (TypeNameCache.TryGetValue(key, out var cached)) return cached;

        var name = ExtractTypeName(path, isDirectory);
        TypeNameCache[key] = name;
        return name;
    }

    private static string CacheKey(string path, bool isDirectory)
    {
        if (isDirectory) return "\u0001directory";
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? "\u0001none" : extension;
    }

    private static string? ExtractTypeName(string path, bool isDirectory)
    {
        var info = default(NativeMethods.ShFileInfo);
        var flags = NativeMethods.ShgfiTypeName | NativeMethods.ShgfiUseFileAttributes;
        var attributes = isDirectory ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal;
        // No SHGFI_ICON here, so the call returns no handle to destroy.
        var result = NativeMethods.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(), flags);
        if (result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.TypeName)) return null;
        return info.TypeName;
    }

    private static ImageSource? ExtractIcon(string path, bool isDirectory)
    {
        var info = default(NativeMethods.ShFileInfo);
        // USEFILEATTRIBUTES resolves by extension alone, so deleted paths still get their type icon.
        var flags = NativeMethods.ShgfiIcon | NativeMethods.ShgfiLargeIcon | NativeMethods.ShgfiUseFileAttributes;
        var attributes = isDirectory ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal;
        var result = NativeMethods.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (ArgumentException) { return null; }
        catch (Win32Exception) { return null; }
        finally { NativeMethods.DestroyIcon(info.IconHandle); }
    }
}
