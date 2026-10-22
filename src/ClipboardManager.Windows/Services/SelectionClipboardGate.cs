using ClipboardManager.Windows.Interop;

namespace ClipboardManager.Windows.Services;

// The gate remembers clipboard sequence numbers, rather than dropping every event
// during a time window. Unrelated applications' copies remain eligible for history.
public sealed class SelectionClipboardGate
{
    private readonly HashSet<uint> _transient = [];
    private uint _sourceProcess;
    private bool _restoring;
    private bool _active;
    public bool IsActive => _active;

    public void Begin(uint sourceProcess) { _sourceProcess = sourceProcess; _active = true; }
    public void Restoring() => _restoring = true;
    public void End() { _active = false; _restoring = false; }

    public bool Observe(uint sequence)
    {
        if (_transient.Contains(sequence)) return true;
        if (!_active) return false;
        var owner = SelectionNative.GetClipboardOwner();
        NativeMethods.GetWindowThreadProcessId(owner, out var process);
        if (process != _sourceProcess && !(_restoring && process == (uint)Environment.ProcessId)) return false;
        Remember(sequence);
        return true;
    }

    public void Remember(uint sequence)
    {
        if (_transient.Count >= 256) _transient.Clear();
        _transient.Add(sequence);
    }
}
