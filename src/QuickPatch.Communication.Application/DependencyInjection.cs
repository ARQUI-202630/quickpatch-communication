using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;

namespace QuickPatch.Communication.Application;

/// <summary>
/// Capa de aplicación: casos de uso, comandos y consultas (SDD 6.3).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<RecentEventIds>();
        services.AddSingleton<RealtimeNotifier>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<InboxRecorder>();
        services.AddScoped<ListNotificationsHandler>();
        services.AddScoped<MarkNotificationReadHandler>();
        return services;
    }
}