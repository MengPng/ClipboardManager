using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class ClipboardCaptureService(
    IClipboardHistoryRepository repository,
    IAppSettingsStore settingsStore,
    IWorkspaceService workspaceService,
    IDeviceIdentityStore deviceIdentityStore,
    IIdentityProvider identityProvider,
    SelfWriteGuard selfWriteGuard)
{
    public event EventHandler<ClipboardItem>? ItemCaptured;

    public async Task<CaptureResult> CaptureTextAsync(
        string? text,
        ClipboardSource source,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (CapturePrivacyPolicy.IsPaused(settings, DateTimeOffset.UtcNow))
        {
            return new CaptureResult(CaptureDisposition.IgnoredPaused);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new CaptureResult(CaptureDisposition.IgnoredEmpty);
        }

        if (CapturePrivacyPolicy.IsExcluded(source.ProcessName, settings.ExcludedProcesses))
        {
            return new CaptureResult(CaptureDisposition.IgnoredExcludedApplication);
        }

        var contentSize = TextContentService.GetUtf8ByteCount(text);
        if (contentSize > settings.MaxTextBytes)
        {
            return new CaptureResult(CaptureDisposition.IgnoredTooLarge);
        }

        var hash = TextContentService.ComputeHash(text);
        if (selfWriteGuard.TryConsume(hash))
        {
            return new CaptureResult(CaptureDisposition.IgnoredSelfWrite);
        }

        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var device = await deviceIdentityStore.GetOrCreateAsync(cancellationToken);
        var identity = await identityProvider.GetSessionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var capture = new TextCapture(
            workspace.Id,
            identity?.IdentityId,
            device.DeviceId,
            text,
            TextContentService.CreatePreview(text),
            hash,
            source.ApplicationName,
            source.ProcessName,
            now,
            contentSize);
        var result = await repository.CaptureTextAsync(capture, cancellationToken);
        if (result.Item is not null)
        {
            ItemCaptured?.Invoke(this, result.Item);
        }

        return result;
    }

}
