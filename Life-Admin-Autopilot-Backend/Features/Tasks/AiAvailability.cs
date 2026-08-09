using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// Port of <c>isAiConfigured()</c> — <c>Boolean(env().GEMINI_API_KEY)</c>.
///
/// <para>
/// The parity reference runs with the key EMPTY, which is what makes every
/// AI-backed Matters route deterministic: each short-circuits to
/// <c>503 ai_not_configured</c> with its OWN message. The messages differ per
/// route and the differ compares them literally, so they live together in
/// <see cref="AiUnavailable"/> rather than being retyped at each call site.
/// </para>
/// </summary>
public sealed class AiAvailability
{
    private readonly bool _configured;

    public AiAvailability(IConfiguration configuration)
    {
        var key = configuration["GEMINI_API_KEY"] ?? configuration["Ai:GeminiApiKey"];
        _configured = !string.IsNullOrEmpty(key);
    }

    public bool IsConfigured => _configured;
}

/// <summary>
/// The five 503 messages this slice can produce. <b>Copy-verbatim territory</b> —
/// each differs, including the British "Categorising", and the contract pins all
/// of them.
/// </summary>
public static class AiUnavailable
{
    public const string Code = "ai_not_configured";

    public static AppException Search() => Throw("Search by description is unavailable.");

    public static AppException Summaries() => Throw("Summaries are unavailable right now.");

    public static AppException Estimates() => Throw("Estimates are unavailable right now.");

    public static AppException Categorising() => Throw("Categorising is unavailable right now.");

    public static AppException Translating() => Throw("Translating is unavailable right now.");

    private static AppException Throw(string message) => new(503, Code, message);

    /// <summary>
    /// The branch that runs once someone DOES configure a key. The model calls
    /// themselves (semantic search, range summaries, backlog estimation,
    /// categorisation, backlog translation) belong to the AI slice, not here — this
    /// slice owns the routes, their validation, their rate limits and their
    /// no-key behaviour, which is the whole of the parity contract while
    /// <c>GEMINI_API_KEY</c> is empty.
    ///
    /// <para>Deliberately a plain exception, so a misconfigured deployment fails
    /// loudly instead of quietly pretending the feature is switched off.</para>
    /// </summary>
    public static Exception NotWiredHere(string route) =>
        new NotSupportedException(
            $"{route} needs a model call, which the AI slice owns. This slice implements the route, " +
            "its validation and its no-key 503 only.");
}
