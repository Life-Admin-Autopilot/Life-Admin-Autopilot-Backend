using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.Planning;

/// <summary>
/// Reaching the extraction model for <c>POST /api/planning/propose</c>.
///
/// <list type="table">
///   <listheader><term>env</term><description>section — default</description></listheader>
///   <item><term><c>PLANNING_API_KEY</c></term><description><c>Ai:Planning:ApiKey</c> — falls back to <c>EMBEDDINGS_API_KEY</c></description></item>
///   <item><term><c>PLANNING_MODEL</c></term><description><c>Ai:Planning:Model</c> — <c>gemini-3.7-flash</c></description></item>
///   <item><term><c>PLANNING_BASE_URL</c></term><description><c>Ai:Planning:BaseUrl</c> — Google Generative Language</description></item>
/// </list>
///
/// <para>
/// The key falls back to the embeddings key because in practice it is the same
/// Google credential, and making a deployment set it twice is a way to have half a
/// feature working. It stays a SEPARATE name from <c>GEMINI_API_KEY</c>, which
/// <c>AiAvailability</c> owns — setting that one turns six honest 503s into 500s.
/// </para>
///
/// <para>
/// <b>The default model is deliberately not a preview.</b> <c>gemini-3-flash-preview</c>
/// exhausted its free quota mid-session and answered 429 while reporting "high
/// demand"; the flow was moved off it for the same reason.
/// </para>
/// </summary>
public sealed class PlanningOptions
{
    public const string HttpClientName = "planning";

    public const string DefaultModel = "gemini-3.7-flash";

    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = DefaultModel;

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// How long ONE model in the chain gets before it is written off as unavailable.
    ///
    /// <para>
    /// Must be comfortably below <see cref="TimeoutSeconds"/> — that is the budget for
    /// the whole walk, and a per-attempt budget equal to it means the first model that
    /// hangs consumes everything and the fallbacks never run. Measured: a hung
    /// <c>gemini-3.7-flash</c> held the request for the full 60s and surfaced as a 500,
    /// with three healthy fallbacks never tried.
    /// </para>
    /// </summary>
    public int AttemptTimeoutSeconds { get; init; } = 20;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Tried in order when the primary answers 503/429.
    ///
    /// <para>
    /// The free tier's capacity flaps: measured on one key within a single minute,
    /// <c>gemini-3.7-flash</c> answered 503 "high demand" and then succeeded on the
    /// very next call, while its siblings stayed up throughout. A single pinned model
    /// therefore fails a demo for reasons that have nothing to do with the request.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Fallbacks { get; init; } =
        new[] { "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3.1-flash-lite" };

    /// <summary>The primary first, then the fallbacks, with duplicates removed.</summary>
    public IReadOnlyList<string> ModelChain =>
        new[] { Model }.Concat(Fallbacks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public Uri GenerateUriFor(string model) =>
        new($"{BaseUrl.TrimEnd('/')}/models/{model}:generateContent");

    public Uri GenerateUri => GenerateUriFor(Model);

    public static PlanningOptions FromConfiguration(IConfiguration configuration) => new()
    {
        ApiKey = Read(configuration, "PLANNING_API_KEY", "Ai:Planning:ApiKey")
                 ?? Read(configuration, "EMBEDDINGS_API_KEY", "Ai:Embeddings:ApiKey"),
        Model = Read(configuration, "PLANNING_MODEL", "Ai:Planning:Model") ?? DefaultModel,
        BaseUrl = Read(configuration, "PLANNING_BASE_URL", "Ai:Planning:BaseUrl") ?? DefaultBaseUrl,
        TimeoutSeconds = configuration.GetValue("Ai:Planning:TimeoutSeconds", 60),
        AttemptTimeoutSeconds = configuration.GetValue("Ai:Planning:AttemptTimeoutSeconds", 20),
    };

    private static string? Read(IConfiguration configuration, string envKey, string sectionKey) =>
        configuration[envKey] is { Length: > 0 } fromEnv ? fromEnv : configuration[sectionKey];
}
