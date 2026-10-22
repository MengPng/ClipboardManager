using ClipboardManager.Domain.Models;

namespace ClipboardManager.Windows.Services;

internal readonly record struct ClipboardCaptureAttribution(
    uint Sequence,
    ClipboardOwnerObservation Owner,
    ClipboardSource Foreground)
{
    public bool MatchesCurrent(
        uint currentSequence,
        ClipboardOwnerObservation currentOwner,
        ClipboardSource currentForeground) =>
        Sequence == currentSequence
        && Owner.Window == currentOwner.Window
        && string.Equals(Foreground.ProcessName, currentForeground.ProcessName, StringComparison.OrdinalIgnoreCase);
}
