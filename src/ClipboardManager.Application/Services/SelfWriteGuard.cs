namespace ClipboardManager.Application.Services;

public sealed class SelfWriteGuard
{
    private readonly object _gate = new();
    private string? _expectedHash;
    private ClipboardManager.Domain.Models.ClipboardPayloadType _expectedType = ClipboardManager.Domain.Models.ClipboardPayloadType.Text;

    public void Expect(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        lock (_gate)
        {
            _expectedHash = contentHash;
            _expectedType = ClipboardManager.Domain.Models.ClipboardPayloadType.Text;
        }
    }

    public void Expect(ClipboardManager.Domain.Models.ClipboardPayloadType type, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        lock (_gate) { _expectedType = type; _expectedHash = contentHash; }
    }

    public bool TryConsume(ClipboardManager.Domain.Models.ClipboardPayloadType type, string contentHash)
    {
        lock (_gate)
        {
            if (_expectedType != type || !string.Equals(_expectedHash, contentHash, StringComparison.Ordinal)) return false;
            _expectedHash = null;
            return true;
        }
    }

    public bool TryConsume(string contentHash)
    {
        lock (_gate)
        {
            if (!string.Equals(_expectedHash, contentHash, StringComparison.Ordinal))
            {
                return false;
            }

            _expectedHash = null;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _expectedHash = null;
        }
    }
}
