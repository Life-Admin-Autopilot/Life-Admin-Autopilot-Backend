using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// The two <c>ai_not_configured</c> messages this slice owns.
///
/// <para>
/// <b>The API carries EIGHT distinct literals under this one code</b>, and the
/// differ compares them byte-for-byte:
/// </para>
///
/// <list type="number">
///   <item>"Search by description is unavailable." — <c>POST /me/tasks/search</c></item>
///   <item>"Summaries are unavailable right now." — <c>POST /me/tasks/summarize</c></item>
///   <item>"Estimates are unavailable right now." — <c>POST /me/tasks/estimate-backlog</c></item>
///   <item>"Categorising is unavailable right now." — <c>POST /me/tasks/categorize</c></item>
///   <item>"Translating is unavailable right now." — <c>POST /me/tasks/translate</c></item>
///   <item>
///     "Typing your own answer needs AI configured. Pick one of the suggestions
///     instead." — <c>POST /me/clarifications/{id}/answer</c> (slice H)
///   </item>
///   <item><see cref="Ask"/> — <c>POST /ai/ask</c></item>
///   <item><see cref="Transcribe"/> — <c>POST /ai/voice/transcribe</c></item>
/// </list>
///
/// <para>
/// The first five live in <c>Features/Tasks/AiAvailability.cs</c> and are NOT
/// duplicated here. Note items 7 and 8 differ by exactly two words: transcribe
/// omits the trailing <c>" to enable."</c>. That is not a typo in the source and
/// must not be tidied up.
/// </para>
/// </summary>
internal static class AiShellErrors
{
    public const string NotConfiguredCode = "ai_not_configured";

    /// <summary><c>POST /ai/ask</c>. Also the string <c>getGeminiClient()</c> throws.</summary>
    public static AppException Ask() =>
        new(503, NotConfiguredCode, "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.");

    /// <summary><c>POST /ai/voice/transcribe</c>. NO trailing " to enable." — verified live.</summary>
    public static AppException Transcribe() =>
        new(503, NotConfiguredCode, "AI is not configured. Set GEMINI_API_KEY in server/.env.");
}
