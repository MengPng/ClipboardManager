using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ILanShareService
{
    Task<IReadOnlyList<LanShareDevice>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<LanShareResult> SendAsync(LanShareDevice device, ClipboardItem item, string? pin = null,
        IProgress<LanShareProgress>? progress = null, CancellationToken cancellationToken = default);
}
