namespace ClipboardManager.Windows.Services;

/// <summary>
/// Clipboard sequence numbers this app produced itself: restores written by the app, and the
/// transient copy/restore pair of a 划词查找 capture. Observed sequences are not recorded in history.
/// </summary>
public sealed class SelfWriteSequenceGate(SelectionClipboardGate? selection = null)
{
    private readonly HashSet<uint> _remembered = [];

    public bool Observe(uint sequence)
    {
        lock (_remembered)
        {
            if (_remembered.Contains(sequence)) return true;
        }
        return selection?.Observe(sequence) == true;
    }

    public void Remember(uint sequence)
    {
        // Observe runs on the consumer thread while Remember runs on the UI thread.
        lock (_remembered)
        {
            if (_remembered.Count >= 256) _remembered.Clear();
            _remembered.Add(sequence);
        }
    }
}
