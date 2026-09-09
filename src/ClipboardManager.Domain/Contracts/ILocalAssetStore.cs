namespace ClipboardManager.Domain.Contracts;

public sealed record StoredAsset(string LocalPath, string? ThumbnailPath, string ContentHash, long ByteSize, string MimeType);

public interface ILocalAssetStore
{
    Task<StoredAsset> StoreImageAsync(byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default);
    Task<StoredAsset> ImportImageAsync(string sourcePath, CancellationToken cancellationToken = default);
}
