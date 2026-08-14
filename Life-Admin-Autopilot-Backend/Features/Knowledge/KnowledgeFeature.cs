using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Knowledge;

/// <summary>
/// RAG over the user's own corpus — SRS §3.7 / stories #15, #82, #83.
///
/// <para>
/// <b>Retrieval lives here, not in the flow.</b> The agent reaches it the same way
/// it reaches the other eleven tools: an authenticated call back into this API. That
/// keeps the owner filter, the quota and the erasure cascade on the server that owns
/// the data, rather than trusting a flow to scope its own reads.
/// </para>
/// </summary>
public static class KnowledgeFeature
{
    public static IServiceCollection AddKnowledgeFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = EmbeddingOptions.FromConfiguration(configuration);
        services.TryAddSingleton(options);

        // Registered unconditionally, unlike the Langflow seam: the service reports
        // IsConfigured and the route answers 503 from it, so an unconfigured
        // deployment gets a clear answer instead of a missing-dependency crash.
        services.AddHttpClient<IEmbeddingProvider, GeminiEmbeddingProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.TryAddScoped<ContentChunkRepository>();
        services.TryAddScoped<KnowledgeService>();
        services.TryAddScoped<ConflictService>();
        services.TryAddScoped<Life_Admin_Autopilot.DAL.Features.Tasks.TaskRepository>();

        // The Knowledge Agent phrases the briefing with the same model chain the
        // Planning slice uses, so it shares PlanningOptions rather than defining a
        // second key for the same credential.
        services.TryAddSingleton(Life_Admin_Autopilot.BLL.Features.Planning.PlanningOptions
            .FromConfiguration(configuration));
        services.AddHttpClient<KnowledgeAgentService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddUserDataEraser<ContentChunkEraser>();
        services.AddMongoIndexProvider<KnowledgeIndexes>();

        return services;
    }
}

/// <summary>Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.</summary>
public sealed class KnowledgeModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddKnowledgeFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapKnowledgeEndpoints();
}
