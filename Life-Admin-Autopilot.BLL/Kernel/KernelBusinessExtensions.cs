using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.UserData;
using Life_Admin_Autopilot.DAL.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot.BLL.Kernel;

/// <summary>
/// BLL half of the shared kernel. Called once from <c>AddKernel()</c> in the PL —
/// a slice never calls it.
/// </summary>
public static class KernelBusinessExtensions
{
    public static IServiceCollection AddKernelBusiness(this IServiceCollection services)
    {
        services.AddKernelData();

        services.TryAddScoped<ClarificationCascade>();
        services.TryAddScoped<BulkService>();
        services.TryAddScoped<UserDataErasureService>();
        services.TryAddScoped<ReminderPlanner>();

        // Shared by the ICS and Google importers. Registered here rather than by
        // either feature module: two slices each registering the same type is how
        // the two copies of it came to exist in the first place.
        services.TryAddScoped<ExternalMatterReconciler>();

        // Parity default: no GEMINI_API_KEY means no AI refinement. The AI slice
        // REPLACES this registration — it must use Replace(), not TryAdd.
        services.TryAddSingleton<IReminderRefiner, NullReminderRefiner>();

        return services;
    }
}
