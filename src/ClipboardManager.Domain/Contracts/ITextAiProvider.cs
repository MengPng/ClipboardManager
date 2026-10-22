using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ITextAiProvider
{
    Task<AiTextResponse> CompleteAsync(AiTextRequest request, CancellationToken cancellationToken = default);
}
