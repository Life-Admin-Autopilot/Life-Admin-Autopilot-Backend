using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>
/// Reaching the embedding model. Nothing here is a literal in the provider.
///
/// <list type="table">
///   <listheader><term>env</term><description>section — default</description></listheader>
///   <item><term><c>EMBEDDINGS_API_KEY</c></term><description><c>Ai:Embeddings:ApiKey</c> — none</description></item>
///   <item><term><c>EMBEDDINGS_MODEL</c></term><description><c>Ai:Embeddings:Model</c> — <c>gemini-embedding-001</c></description></item>
///   <item><term><c>EMBEDDINGS_BASE_URL</c></term><description><c>Ai:Embeddings:BaseUrl</c> — Google Generative Language</description></item>
/// </list>
///
/// <para>
/// <b>Why NOT <c>GEMINI_API_KEY</c>, despite being the same vendor and the same
/// key.</b> That name is load-bearing elsewhere: <c>AiAvailability</c> reads it as
/// "is the AI slice wired?", and six routes — <c>/me/tasks/search|summarize|
/// estimate-backlog|categorize|translate</c> and the custom clarification answer —
/// pass their 503 gate on it and then immediately throw <c>NotWiredHere</c>. Setting
/// it to switch on embeddings would turn six honest 503s into 500s. Retrieval gets
/// its own name so the two switches stay independent.
/// </para>
/// </summary>
public sealed class EmbeddingOptions
{
    public const string HttpClientName = "embeddings";

    public const string DefaultModel = "gemini-embedding-001";

    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = DefaultModel;

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Absent key ⇒ the whole knowledge slice stays off, the way Langflow does.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public Uri EmbedUri => new($"{BaseUrl.TrimEnd('/')}/models/{Model}:embedContent");

    public static EmbeddingOptions FromConfiguration(IConfiguration configuration) => new()
    {
        ApiKey = Read(configuration, "EMBEDDINGS_API_KEY", "Ai:Embeddings:ApiKey"),
        Model = Read(configuration, "EMBEDDINGS_MODEL", "Ai:Embeddings:Model") ?? DefaultModel,
        BaseUrl = Read(configuration, "EMBEDDINGS_BASE_URL", "Ai:Embeddings:BaseUrl") ?? DefaultBaseUrl,
        TimeoutSeconds = configuration.GetValue("Ai:Embeddings:TimeoutSeconds", 30),
    };

    private static string? Read(IConfiguration configuration, string envKey, string sectionKey) =>
        configuration[envKey] is { Length: > 0 } fromEnv ? fromEnv : configuration[sectionKey];
}
