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

        // Parity default: no GEMINI_API_KEY means no AI refinement. The AI slice
        // REPLACES this registration — it must use Replace(), not TryAdd.
        services.TryAddSingleton<IReminderRefiner, NullReminderRefiner>();

        return services;
    }
}
