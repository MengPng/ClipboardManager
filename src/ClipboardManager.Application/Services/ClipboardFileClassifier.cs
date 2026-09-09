namespace ClipboardManager.Application.Services;

public static class ClipboardFileClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".heic", ".heif", ".avif", ".ico", ".svg"
    };

    public static bool IsSingleImageFile(IReadOnlyList<string> paths) =>
        paths.Count == 1 && ImageExtensions.Contains(Path.GetExtension(paths[0]));
}
