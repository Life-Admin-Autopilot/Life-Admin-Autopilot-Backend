using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
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
        services.TryAddScoped<AiConfirmedToolRunner>();

        // THE SEAM, and the ONE line that decides which side of it you are on.
        //
        // Selection is on CONFIGURATION PRESENCE, not on a feature flag: a Langflow
        // base URL and flow id together mean "there is an agent to talk to", and
        // their absence keeps NotConfiguredAiProvider — which is what holds the
        // parity target, since Node with no GEMINI_API_KEY is exactly a server whose
        // isAiConfigured() is false. A deployment that sets neither behaves as it did
        // before this slice existed, byte for byte.
        //
        // Still `TryAdd`, so a test or a later slice can register its own provider
        // ahead of this call and win without editing this file.
        var langflow = LangflowOptions.FromConfiguration(configuration);
        services.TryAddSingleton(langflow);

        if (langflow.IsConfigured)
        {
            var binding = LangflowInputBinding.FromConfiguration(configuration);

            // Say so at boot when the flow has an agent but no way to reach it.
            //
            // An unbound input node is the quietest misconfiguration in the system:
            // the run is accepted, Langflow streams a healthy sources → token → done,
            // and the agent — handed an empty prompt — answers "I couldn't quite make
            // that out." Nothing anywhere reports an error, so it reads as the model
            // being stupid rather than as LANGFLOW_INPUT_NODE being unset.
            if (!binding.IsBound)
            {
                Console.Error.WriteLine(
                    "ai:langflow-input-unbound — LANGFLOW_INPUT_NODE is not set, so the user's "
                    + "message will NOT reach the agent and every turn will answer as if the "
                    + "prompt were empty. Set it to the flow's input node (e.g. PlanningInput-v4).");
            }

            services.TryAddSingleton(binding);
            services.AddHttpClient(LangflowOptions.HttpClientName);

            // The read behind the agent's MY TASKS block. Registered here rather than
            // unconditionally because nothing else consults it: with no Langflow
            // configured the provider is NotConfiguredAiProvider and no prompt is
            // ever assembled.
            services.TryAddScoped<AiGroundingRepository>();
            services.TryAddScoped<IAiProvider, LangflowAiProvider>();

            // Reset rotates the conversation's session generation, which is what
            // makes the reset honest on its own. This is the courtesy on top: the
            // retired session's transcript is dropped from Langflow's own store.
            // Best-effort at the call site, so a wedged agent cannot fail a reset.
            services.TryAddScoped<IAgentSessionMemory, LangflowSessionMemory>();
        }
        else
        {
            services.TryAddScoped<IAiProvider, NotConfiguredAiProvider>();

            // No external agent, nothing to forget — and no outbound call on a route
            // the parity reference answers without one.
            services.TryAddScoped<IAgentSessionMemory, NoAgentSessionMemory>();
        }

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
