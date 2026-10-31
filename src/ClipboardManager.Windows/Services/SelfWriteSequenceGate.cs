namespace ClipboardManager.Windows.Services;

// The gate remembers the clipboard sequence numbers of this tool's own writes, rather than
// dropping every event during a time window. Unrelated applications' copies stay eligible for history.
public sealed class SelfWriteSequenceGate
{
    private readonly HashSet<uint> _remembered = [];

    public bool Observe(uint sequence)
    {
        lock (_remembered) return _remembered.Contains(sequence);
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
