using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Models;
using ClipboardManager.Windows.Services;

internal static class ClipboardSafetyProbe
{
    // Pure synthetic regression checks: this mode never opens, reads, writes, or logs the system clipboard.
    public static int Run()
    {
        var now = DateTimeOffset.UtcNow;
        Require(!CapturePrivacyPolicy.CanRead(
            new AppSettings { ExcludedProcesses = ["KeePass"] },
            new ClipboardSource(null, null),
            now), "UNKNOWN_OWNER_REJECTED_WITH_EXCLUSIONS");
        Require(CapturePrivacyPolicy.CanRead(
            new AppSettings { ExcludedProcesses = [] },
            new ClipboardSource(null, null),
            now), "UNKNOWN_OWNER_ALLOWED_WITHOUT_EXCLUSIONS");
        Require(ClipboardResourceLimits.TryEstimateBitmapBytes(4096, 4096, 32, out var exact)
            && exact == ClipboardResourceLimits.MaxDecodedImageBytes, "IMAGE_DECODED_LIMIT_EXACT");
        Require(!ClipboardResourceLimits.TryEstimateBitmapBytes(4097, 4096, 32, out _), "IMAGE_DECODED_LIMIT_REJECTS_OVERSIZE");
        Require(!ClipboardResourceLimits.TryAdd(
            ClipboardResourceLimits.MaxSnapshotBytes - 1,
            2,
            ClipboardResourceLimits.MaxSnapshotBytes,
            out _), "SNAPSHOT_CUMULATIVE_LIMIT_REJECTS_OVERSIZE");
        Console.WriteLine("CLIPBOARD_SAFETY_SYNTHETIC_OK");
        return 0;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Clipboard safety probe failed: {name}");
    }
}
