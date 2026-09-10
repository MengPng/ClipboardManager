using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Infrastructure.Cloud;

public sealed class CloudModeService(IAppSettingsStore settings, ICloudAccountService accounts) : ICloudModeService
{
    public AppMode Mode { get; private set; } = AppMode.Local;
    public CloudStatus Status { get; private set; } = new(CloudConnectionState.LocalOnly, "仅本地");
    public event EventHandler<CloudStatus>? StatusChanged;

    public async Task SetModeAsync(AppMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == AppMode.Cloud)
        {
            var current = await settings.LoadAsync(cancellationToken);
            if (!Uri.TryCreate(current.CloudApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                Set(new(CloudConnectionState.NeedsConfiguration, "请先配置 HTTPS 云端地址"));
                throw new InvalidOperationException(Status.Message);
            }
            if (await accounts.GetSessionAsync(cancellationToken) is null)
            {
                Set(new(CloudConnectionState.NeedsSignIn, "需要登录"));
                throw new UnauthorizedAccessException(Status.Message);
            }
            Mode = AppMode.Cloud;
            Set(new(CloudConnectionState.Connected, "已登录，尚未同步"));
        }
        else { Mode = AppMode.Local; Set(new(CloudConnectionState.LocalOnly, "仅本地")); }

        var latest = await settings.LoadAsync(cancellationToken);
        await settings.SaveAsync(latest with { CloudModeEnabled = Mode == AppMode.Cloud }, cancellationToken);
    }

    private void Set(CloudStatus status) { Status = status; StatusChanged?.Invoke(this, status); }
}
