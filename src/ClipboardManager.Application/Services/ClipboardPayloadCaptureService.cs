using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipboardManager.Application.Services;

public sealed class ClipboardPayloadCaptureService(
    ClipboardCaptureService textCaptureService,
    IClipboardHistoryRepository repository,
    IWorkspaceService workspaceService,
    IDeviceIdentityStore deviceStore,
    IIdentityProvider identityProvider,
    ILocalAssetStore assetStore,
    IAppSettingsStore settingsStore,
    SelfWriteGuard selfWriteGuard)
{
    public event EventHandler<ClipboardItem>? ItemCaptured;

    public async Task<CaptureResult> CaptureAsync(ClipboardPayload payload, ClipboardSource source, CancellationToken cancellationToken = default)
    {
        if (payload.Type == ClipboardPayloadType.Text)
            return await textCaptureService.CaptureTextAsync(payload.Text, source, cancellationToken);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (CapturePrivacyPolicy.IsPaused(settings, DateTimeOffset.UtcNow)) return new CaptureResult(CaptureDisposition.IgnoredPaused);
        if (CapturePrivacyPolicy.IsExcluded(source.ProcessName, settings.ExcludedProcesses))
            return new CaptureResult(CaptureDisposition.IgnoredExcludedApplication);

        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var device = await deviceStore.GetOrCreateAsync(cancellationToken);
        var identity = await identityProvider.GetSessionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        ClipboardItem item;
        if (payload.Type == ClipboardPayloadType.Image && payload.ImageBytes is not null)
        {
            if (payload.ImageBytes.LongLength > settings.MaxPersistedImageBytes) return new CaptureResult(CaptureDisposition.IgnoredTooLarge);
            var asset = await assetStore.StoreImageAsync(payload.ImageBytes, payload.ImageMimeType ?? "image/png", cancellationToken);
            if (selfWriteGuard.TryConsume(ClipboardPayloadType.Image, asset.ContentHash)) return new CaptureResult(CaptureDisposition.IgnoredSelfWrite);
            item = NewItem(workspace.Id, identity?.IdentityId, device.DeviceId, ClipboardItemType.Image, asset.ContentHash, source, now, asset.ByteSize) with
            { ImagePath = asset.LocalPath, ThumbnailPath = asset.ThumbnailPath, PreviewText = "图片" };
        }
        else if (payload.Type == ClipboardPayloadType.Files && payload.Files is { Count: > 0 })
        {
            var normalized = payload.Files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var manifest = JsonSerializer.Serialize(normalized);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized)))).ToLowerInvariant();
            if (selfWriteGuard.TryConsume(ClipboardPayloadType.Files, hash)) return new CaptureResult(CaptureDisposition.IgnoredSelfWrite);
            var itemType = ClipboardFileClassifier.IsSingleImageFile(normalized)
                ? ClipboardItemType.ImageFile
                : ClipboardItemType.Files;
            item = NewItem(workspace.Id, identity?.IdentityId, device.DeviceId, itemType, hash, source, now, Encoding.UTF8.GetByteCount(manifest)) with
            { FileManifest = manifest, PreviewText = normalized.Length == 1 ? Path.GetFileName(normalized[0]) : $"{normalized.Length} 个文件" };
        }
        else return new CaptureResult(CaptureDisposition.IgnoredEmpty);

        var existing = await repository.SearchAsync(new ClipboardQuery(workspace.Id, ItemType: item.ItemType, Limit: 500), cancellationToken);
        var duplicate = existing.FirstOrDefault(x => x.ContentHash == item.ContentHash);
        var disposition = duplicate is null ? CaptureDisposition.Created : CaptureDisposition.Merged;
        if (duplicate is not null) item = item with { Id = duplicate.Id, CreatedAt = duplicate.CreatedAt, IsFavorite = duplicate.IsFavorite, IsPinned = duplicate.IsPinned, UseCount = duplicate.UseCount, LocalVersion = duplicate.LocalVersion + 1 };
        await repository.UpsertAsync(item, cancellationToken);
        ItemCaptured?.Invoke(this, item);
        return new CaptureResult(disposition, item);
    }

    private static ClipboardItem NewItem(Guid workspace, Guid? owner, Guid device, ClipboardItemType type, string hash, ClipboardSource source, DateTimeOffset now, long size) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspace, OwnerId = owner, DeviceId = device, ItemType = type,
        ContentHash = hash, SourceApp = source.ApplicationName, SourceProcess = source.ProcessName,
        CreatedAt = now, UpdatedAt = now, CapturedAt = now, ContentSize = size
    };
}
