namespace ClipboardManager.Domain.Contracts;

using ClipboardManager.Domain.Models;

public interface ISystemClipboard
{
    Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken = default);
    Task<string?> ReadTextAsync(CancellationToken cancellationToken = default);
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
    Task WriteImageAsync(string imagePath, CancellationToken cancellationToken = default);
    Task WriteFilesAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default);
}
