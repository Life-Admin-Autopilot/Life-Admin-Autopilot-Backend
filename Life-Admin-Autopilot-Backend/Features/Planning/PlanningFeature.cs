using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Planning;

/// <summary>
/// Propose/commit — SRS §7.1, stories #30 and #35.
///
/// <para>
/// Depends on the Matters slice for <c>TaskWriteService</c>/<c>TaskRepository</c>
/// and on Knowledge for duplicate detection; both use <c>TryAdd</c>, so whichever
/// module the scanner reaches first wins and this one is a no-op for them.
/// </para>
/// </summary>
public static class PlanningFeature
{
    public static IServiceCollection AddPlanningFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = PlanningOptions.FromConfiguration(configuration);
        services.TryAddSingleton(options);

        services.AddHttpClient<PlanningService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        return services;
    }
}

/// <summary>Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.</summary>
public sealed class PlanningModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddPlanningFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPlanningEndpoints();
}
