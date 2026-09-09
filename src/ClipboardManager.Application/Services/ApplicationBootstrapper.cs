using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class ApplicationBootstrapper(
    IDatabaseInitializer databaseInitializer,
    IDeviceIdentityStore deviceIdentityStore,
    IIdentityProvider identityProvider,
    IWorkspaceService workspaceService,
    IAppSettingsStore settingsStore,
    ILocalRegistrationStore registrationStore,
    ClipboardHistoryService historyService,
    CollectionService collectionService)
{
    public async Task<BootstrapContext> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await databaseInitializer.InitializeAsync(cancellationToken);
        var device = await deviceIdentityStore.GetOrCreateAsync(cancellationToken);
        var identity = await identityProvider.GetSessionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Local identity provider returned no session.");
        var workspace = await workspaceService.GetCurrentAsync(cancellationToken);
        var workspaces = await workspaceService.GetAvailableAsync(cancellationToken);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        await registrationStore.EnsureRegisteredAsync(identity, device, workspaces, cancellationToken);
        await historyService.CleanupAsync(cancellationToken);
        // Each config space owns its own 未分类 folder, so every workspace is seeded here
        // rather than lazily on first view.
        foreach (var available in workspaces)
        {
            await collectionService.GetFoldersAsync(available.Id, cancellationToken);
        }

        return new BootstrapContext(identity, device, workspace, workspaces, settings);
    }
}

public sealed record BootstrapContext(
    IdentitySession Identity,
    DeviceIdentity Device,
    Workspace Workspace,
    IReadOnlyList<Workspace> Workspaces,
    AppSettings Settings);
