using ClipboardManager.Application.Services;
using ClipboardManager.Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<ISyncEngine, LocalOnlySyncEngine>();
        return services;
    }
}
