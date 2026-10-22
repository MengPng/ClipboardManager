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

    public static bool HasConfiguredExclusions(IReadOnlyList<string> excludedProcesses) =>
        excludedProcesses.Any(entry => !string.IsNullOrWhiteSpace(entry));

    public static bool CanRead(AppSettings settings, ClipboardSource source, DateTimeOffset now) =>
        CanRead(settings, source, now, sourceIsKnown: !string.IsNullOrWhiteSpace(source.ProcessName));

    /// <summary>
    /// Decides whether it is safe to materialize a clipboard payload. When the user configured
    /// exclusions, an unresolvable clipboard owner is treated conservatively: it may be an
    /// excluded application's delayed-rendered clipboard content.
    /// </summary>
    public static bool CanRead(AppSettings settings, ClipboardSource source, DateTimeOffset now, bool sourceIsKnown) =>
        !IsPaused(settings, now)
        && (sourceIsKnown || !HasConfiguredExclusions(settings.ExcludedProcesses))
        && !IsExcluded(source.ProcessName, settings.ExcludedProcesses);
}
