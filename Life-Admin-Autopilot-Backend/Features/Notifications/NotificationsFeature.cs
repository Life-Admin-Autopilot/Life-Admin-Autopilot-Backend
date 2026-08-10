using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Notifications;

/// <summary>
/// The notifications + reminders slice's one DI extension —
/// <c>routes/me.notifications.ts</c>, <c>routes/me.reminders.ts</c> and
/// <c>lib/reminderWorker.ts</c>.
///
/// <para>
/// No <c>IMongoIndexProvider</c>: <c>KernelIndexProvider</c> already declares the
/// feed's <c>{userId, readAt, createdAt}</c> index and the worker's
/// <c>{status, reminders.firedAt, reminders.at}</c> claim scan, and neither
/// carries a uniqueness invariant this slice would add.
/// </para>
/// </summary>
public static class NotificationsFeature
{
    public static IServiceCollection AddNotificationsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<NotificationRepository>();
        services.TryAddScoped<ReminderTaskRepository>();
        services.TryAddScoped<ReminderUserTimezoneReader>();
        services.TryAddScoped<StaleClarificationSettler>();
        services.TryAddScoped<ReminderTick>();

        // This slice owns the notifications collection, so it owns the erasure.
        services.AddUserDataEraser<NotificationEraser>();

        services.AddKernelWorker<ReminderWorker>();

        return services;
    }
}

/// <summary>
/// Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.
/// </summary>
public sealed class NotificationsModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddNotificationsFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapNotificationEndpoints();
        endpoints.MapReminderEndpoints();
    }
}
