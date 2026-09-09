using ClipboardManager.Domain.Contracts;
using ClipboardManager.Infrastructure.Data;
using ClipboardManager.Infrastructure.Identity;
using ClipboardManager.Infrastructure.Lan;
using ClipboardManager.Infrastructure.Paths;
using ClipboardManager.Infrastructure.Sync;
using ClipboardManager.Infrastructure.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace ClipboardManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string? appDataRoot = null)
    {
        services.AddSingleton(new AppPaths(appDataRoot));
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<DatabaseSafetyBackup>();
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddSingleton<IStorageMaintenanceService, StorageMaintenanceService>();
        services.AddSingleton<IPersonalConfigTransferService, PersonalConfigTransferService>();
        services.AddSingleton<ILanShareService, LocalSendLanShareService>();
        services.AddSingleton<IClipboardHistoryRepository, SqliteClipboardHistoryRepository>();
        services.AddSingleton<ICollectionRepository, SqliteCollectionRepository>();
        services.AddSingleton<IClipboardItemRepository>(provider => provider.GetRequiredService<IClipboardHistoryRepository>());
        services.AddSingleton<ILocalRegistrationStore, SqliteLocalRegistrationStore>();
        services.AddSingleton<IDeviceIdentityStore, JsonDeviceIdentityStore>();
        services.AddSingleton<IIdentityProvider, LocalAnonymousIdentityProvider>();
        services.AddSingleton<IWorkspaceService, LocalWorkspaceService>();
        services.AddSingleton<IRemoteSyncProvider, UnsupportedRemoteSyncProvider>();
        return services;
    }
}
