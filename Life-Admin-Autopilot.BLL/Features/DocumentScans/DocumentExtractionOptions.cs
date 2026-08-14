using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// Reaching the vision model that reads an uploaded document.
///
/// <list type="table">
///   <listheader><term>env</term><description>section — default</description></listheader>
///   <item><term><c>DOCUMENT_AI_API_KEY</c></term><description><c>Ai:Documents:ApiKey</c> — falls back to the planning key, then the embeddings key</description></item>
///   <item><term><c>DOCUMENT_AI_MODEL</c></term><description><c>Ai:Documents:Model</c> — <c>gemini-3.7-flash</c></description></item>
///   <item><term><c>DOCUMENT_AI_BASE_URL</c></term><description><c>Ai:Documents:BaseUrl</c> — Google Generative Language</description></item>
/// </list>
///
/// <para>
/// <b>Why not <c>GEMINI_API_KEY</c>.</b> That name belongs to <c>AiAvailability</c>,
/// which gates six routes whose honest 503 is the parity target; setting it to turn
/// document scanning on would turn those six into 500s. The same reasoning — and
/// the same three-step fallback — as <see cref="Planning.PlanningOptions"/>, so one
/// Google credential lights up planning, embeddings and scanning together and a
/// deployment cannot end up with half a feature.
/// </para>
///
/// <para>
/// <b>Unset is a supported state, not a misconfiguration.</b> With no key the slice
/// keeps <see cref="NullDocumentExtractor"/> and every scan fails with the
/// reference server's sentence — which is what the no-key parity run asserts.
/// </para>
/// </summary>
public sealed class DocumentExtractionOptions
{
    public const string DefaultModel = "gemini-3.7-flash";

    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = DefaultModel;

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>
    /// The budget for the whole walk. Higher than planning's 60 s because the
    /// request carries the document itself — up to 15 MiB of it — and the upload is
    /// part of the call.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// One model's share of that budget. Must leave room for the fallbacks, or the
    /// first model that hangs spends everything and the chain never reaches them.
    /// </summary>
    public int AttemptTimeoutSeconds { get; init; } = 45;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Tried in order when the primary answers 503/429. Every one reads images and PDFs.</summary>
    public IReadOnlyList<string> Fallbacks { get; init; } =
        new[] { "gemini-3.6-flash", "gemini-3.5-flash" };

    public IReadOnlyList<string> ModelChain =>
        new[] { Model }.Concat(Fallbacks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public Uri GenerateUriFor(string model) =>
        new($"{BaseUrl.TrimEnd('/')}/models/{model}:generateContent");

    public static DocumentExtractionOptions FromConfiguration(IConfiguration configuration) => new()
    {
        ApiKey = Read(configuration, "DOCUMENT_AI_API_KEY", "Ai:Documents:ApiKey")
                 ?? Read(configuration, "PLANNING_API_KEY", "Ai:Planning:ApiKey")
                 ?? Read(configuration, "EMBEDDINGS_API_KEY", "Ai:Embeddings:ApiKey"),
        Model = Read(configuration, "DOCUMENT_AI_MODEL", "Ai:Documents:Model") ?? DefaultModel,
        BaseUrl = Read(configuration, "DOCUMENT_AI_BASE_URL", "Ai:Documents:BaseUrl") ?? DefaultBaseUrl,
        TimeoutSeconds = configuration.GetValue("Ai:Documents:TimeoutSeconds", 120),
        AttemptTimeoutSeconds = configuration.GetValue("Ai:Documents:AttemptTimeoutSeconds", 45),
    };

    private static string? Read(IConfiguration configuration, string envKey, string sectionKey) =>
        configuration[envKey] is { Length: > 0 } fromEnv ? fromEnv : configuration[sectionKey];
}
