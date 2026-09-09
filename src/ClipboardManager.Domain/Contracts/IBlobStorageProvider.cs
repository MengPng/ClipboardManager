using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface IBlobStorageProvider
{
    Task<UploadTicket> CreateUploadAsync(
        BlobDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task CompleteUploadAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(string blobId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string blobId, CancellationToken cancellationToken = default);
}
