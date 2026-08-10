using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// The AI shell's one DI extension — <c>server/src/modules/ai/routes.ts</c>,
/// <c>quota.ts</c> and <c>conversationService.ts</c>.
/// </summary>
public static class AiFeature
{
    public static IServiceCollection AddAiFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<AiConversationRepository>();
        services.TryAddScoped<AiConversationService>();
        services.TryAddScoped<AiQuotaService>();

        // THE SEAM. `TryAdd` rather than `Replace`, so the Langflow phase can register
        // a real provider ahead of this call and win without editing this file — and
        // so a second slice asking for an IAiProvider gets the same one.
        services.TryAddScoped<IAiProvider, NotConfiguredAiProvider>();

        // This slice owns two collections, so it owns both erasures. The registry is
        // what lets slice K delete an account without a hardcoded collection list.
        // `translationusagecounters` is NOT here — Matters owns and erases that one.
        services.AddUserDataEraser<AiConversationEraser>();
        services.AddUserDataEraser<AiUsageEraser>();

        // aiusagecounters' unique index already comes from KernelIndexProvider; this
        // adds the conversation key, whose uniqueness the upsert path depends on.
        services.AddMongoIndexProvider<AiIndexes>();

        return services;
    }
}

/// <summary>Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.</summary>
public sealed class AiModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddAiFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAiConversationEndpoints();
        endpoints.MapAiStreamEndpoints();
    }
}
