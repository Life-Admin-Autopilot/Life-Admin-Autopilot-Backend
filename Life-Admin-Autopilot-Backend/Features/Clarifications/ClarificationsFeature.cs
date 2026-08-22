using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Clarifications;

/// <summary>
/// The clarifications slice's one DI extension — <c>routes/me.clarifications.ts</c>.
///
/// <para>
/// No <c>IMongoIndexProvider</c>: <c>KernelIndexProvider</c> already declares the
/// card stack's <c>{userId, status, createdAt}</c> index and the partial-unique
/// <c>{userId, sourceKey}</c> idempotency index, and this slice adds no further
/// uniqueness invariant.
/// </para>
///
/// <para>
/// No worker either, even though one writes here: the reminder tick settles any
/// question still open after seven days. That update lives in Node's
/// <c>lib/reminderWorker.ts</c>, so it is ported where Node put it — see
/// <c>StaleClarificationSettler</c>, registered by the notifications slice.
/// </para>
/// </summary>
public static class ClarificationsFeature
{
    public static IServiceCollection AddClarificationsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<ClarificationRepository>();
        services.TryAddScoped<ClarificationTaskUpdater>();

        // The typed-answer interpreter rides the Gemini-direct planning seam; the
        // options TryAdd cannot clobber PlanningFeature's own registration.
        services.TryAddSingleton(
            Life_Admin_Autopilot.BLL.Features.Planning.PlanningOptions.FromConfiguration(configuration));
        services.AddHttpClient<CustomAnswerInterpreter>();

        // The create route's whole behaviour. It composes the Matters slice's
        // TaskWriteService rather than owning a second create, because a held item's
        // task must be indistinguishable from any other — the entire point of
        // creating it up front is that it shows up in Matters like everything else.
        services.TryAddScoped<ClarificationHoldService>();

        // Its sibling for the other order: hold creates the task WITH the question,
        // this asks about a task that already exists. POST /me/tasks resolves it, so
        // it is registered here beside the service it mirrors rather than in the
        // Matters slice, where the reason the two exist would not be visible.
        services.TryAddScoped<MatterGapService>();

        // This slice owns the clarifications surface, so it owns the erasure. Node
        // deletes the same rows in routes/me.ts's hand-maintained list; nothing had
        // registered it on the .NET side yet.
        services.AddUserDataEraser<ClarificationEraser>();

        return services;
    }
}

/// <summary>
/// Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.
/// </summary>
public sealed class ClarificationsModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddClarificationsFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapClarificationEndpoints();
}
