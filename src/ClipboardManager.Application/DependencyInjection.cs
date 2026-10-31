using ClipboardManager.Application.Services;
using ClipboardManager.Application.Services.ConfigSync;
using ClipboardManager.Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClipboardManager.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ApplicationBootstrapper>();
        services.AddSingleton<SelfWriteGuard>();
        services.AddSingleton<ClipboardCaptureService>();
        services.AddSingleton<ClipboardPayloadCaptureService>();
        services.AddSingleton<ClipboardHistoryService>();
        services.AddSingleton<ClipboardRestoreService>();
        services.AddSingleton<CollectionService>();
        services.AddSingleton<CollectionRestoreService>();
        services.AddSingleton<TodoService>();
        services.AddSingleton<ClipboardCalculationService>();
        services.AddSingleton<ISyncEngine, LocalOnlySyncEngine>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PairingSessions>();
        services.AddSingleton<ConfigSyncEndpoint>();
        services.AddSingleton<IConfigSyncEndpoint>(provider => provider.GetRequiredService<ConfigSyncEndpoint>());
        services.AddSingleton<ConfigPublishService>();
        services.AddSingleton<ConfigSubscriptionService>();
        services.AddSingleton<ConfigSyncCoordinator>();
        return services;
    }
}
