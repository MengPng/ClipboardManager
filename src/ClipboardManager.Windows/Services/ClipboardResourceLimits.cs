namespace ClipboardManager.Windows.Services;

/// <summary>Central limits for clipboard resources that must be copied in memory.</summary>
public static class ClipboardResourceLimits
{
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;
    public const long MaxDecodedImageBytes = 64L * 1024 * 1024;
    public const long MaxEncodedImageBytes = 64L * 1024 * 1024;

    public static bool TryAdd(long currentBytes, long additionalBytes, long limit, out long totalBytes)
    {
        totalBytes = 0;
        if (currentBytes < 0 || additionalBytes < 0 || limit < 0) return false;
        try
        {
            totalBytes = checked(currentBytes + additionalBytes);
            return totalBytes <= limit;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool TryEstimateBitmapBytes(int width, int height, int bitsPerPixel, out long bytes)
    {
        bytes = 0;
        if (width <= 0 || height <= 0 || bitsPerPixel <= 0) return false;
        try
        {
            bytes = checked(((long)width * height * bitsPerPixel + 7) / 8);
            return bytes <= MaxDecodedImageBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
