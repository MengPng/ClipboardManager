using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

/// <summary>Shared pre-read and pre-persist privacy decision.</summary>
public static class CapturePrivacyPolicy
{
    public static bool IsPaused(AppSettings settings, DateTimeOffset now) =>
        settings.IsCapturePaused || settings.CapturePausedUntil > now;

    public static bool IsExcluded(string? processName, IReadOnlyList<string> excludedProcesses)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = Path.GetFileNameWithoutExtension(processName.Trim());
        return excludedProcesses.Any(entry => !string.IsNullOrWhiteSpace(entry) &&
            string.Equals(Path.GetFileNameWithoutExtension(entry.Trim()), name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanRead(AppSettings settings, ClipboardSource source, DateTimeOffset now) =>
        !IsPaused(settings, now) && !IsExcluded(source.ProcessName, settings.ExcludedProcesses);
}
