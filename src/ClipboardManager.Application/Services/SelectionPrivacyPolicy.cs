using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

/// <summary>
/// Per-application gate for the selection AI feature. The feature is global by
/// default; processes on the exclusion list deactivate it entirely — neither the
/// hotkey nor the floating toolbar activates, and no clipboard payload is read.
/// Matching is by process name (never path), case-insensitive, trimmed.
/// </summary>
public static class SelectionPrivacyPolicy
{
    public static bool IsProcessExcluded(IReadOnlyList<string> excludedProcesses, string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        foreach (var entry in excludedProcesses)
        {
            var name = entry.Trim();
            if (name.Length > 0 && string.Equals(name, processName.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
