namespace ClipboardManager.Domain.Contracts;

/// <summary>Compare and replace under a single native clipboard lock; never replace a newer copy.</summary>
public interface IClipboardTextRewriter
{
    Task<bool> TryReplaceTextAsync(string expectedText, uint expectedSequence, string replacement,
        CancellationToken cancellationToken = default);
}
