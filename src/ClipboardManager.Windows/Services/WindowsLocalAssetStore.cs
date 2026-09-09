using ClipboardManager.Domain.Contracts;
using System.IO;
using ClipboardManager.Infrastructure.Paths;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Windows.Services;

public sealed class WindowsLocalAssetStore(AppPaths paths) : ILocalAssetStore
{
    public async Task<StoredAsset> StoreImageAsync(byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        paths.EnsureCreated();
        var hash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var extension = mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
        var target = Path.Combine(paths.BlobsPath, hash + extension);
        if (!File.Exists(target)) await File.WriteAllBytesAsync(target, imageBytes, cancellationToken);
        var thumbnail = Path.Combine(paths.ThumbnailsPath, hash + ".png");
        if (!File.Exists(thumbnail)) await CreateThumbnailAsync(target, thumbnail, cancellationToken);
        return new StoredAsset(target, thumbnail, hash, imageBytes.LongLength, mimeType);
    }

    public async Task<StoredAsset> ImportImageAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("图片文件不存在。", sourcePath);
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var extension = Path.GetExtension(sourcePath);
        var mime = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg" : "image/png";
        return await StoreImageAsync(bytes, mime, cancellationToken);
    }

    private static Task CreateThumbnailAsync(string sourcePath, string targetPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoder = BitmapDecoder.Create(new Uri(sourcePath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var scale = Math.Min(1d, 320d / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource thumbnail = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumbnail));
            using var stream = File.Create(targetPath);
            encoder.Save(stream);
        }, cancellationToken);
}
