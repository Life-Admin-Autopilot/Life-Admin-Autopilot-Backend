using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// The Matters slice's one DI extension. Everything the slice registers is here,
/// so the surface stays greppable.
///
/// <para><c>BulkService</c> and <c>ClarificationCascade</c> are NOT registered
/// here — the kernel already owns them, and this slice consumes them rather than
/// re-registering.</para>
/// </summary>
public static class TasksFeature
{
    public static IServiceCollection AddTasksFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<TaskRepository>();
        services.TryAddScoped<TaskWriteService>();
        services.TryAddScoped<TaskCountsService>();
        services.TryAddScoped<CategorizeService>();
        services.TryAddScoped<TranslateQuotaService>();
        services.TryAddScoped<UserLocaleReader>();

        // Reads one config value at construction; nothing per-request.
        services.TryAddSingleton<AiAvailability>();

        // Additive registries — adding to them never touches another slice's file.
        services.AddUserDataEraser<TaskEraser>();
        services.AddUserDataEraser<TaskBulkOpEraser>();
        services.AddUserDataEraser<TranslationUsageEraser>();
        services.AddMongoIndexProvider<TaskIndexes>();

        return services;
    }
}

/// <summary>
/// Found by the kernel's assembly scanner — no <c>Program.cs</c> edit, which is
/// how seven slices land in parallel without touching a shared file.
/// </summary>
public sealed class TasksModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddTasksFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapTasksEndpoints();
}
