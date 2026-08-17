using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.DAL.Kernel.Ops;

/// <summary>
/// What a customer is told when an operator has pulled a kill switch.
///
/// <para>
/// <b>The operator's reason is deliberately NOT in these messages.</b> The reason
/// field on a flag is written for the audit log — "Gemini billing cap hit",
/// "prompt regression", "provider outage" — and it is internal operational
/// language about a vendor and a budget. Forwarding it to every customer would
/// leak how the product is built and read as an apology written by an engineer.
/// The console records why; the customer is told what and for how long.
/// </para>
///
/// <para>
/// <b>503, matching <c>ai_not_configured</c>.</b> Both mean "this deployment
/// cannot do that right now, and no amount of fixing the request will help" —
/// which is exactly what a client needs to distinguish from a 4xx it caused.
/// The switch is expected to be temporary, so the copy says so; nothing here
/// invites the user to change their request.
/// </para>
/// </summary>
public static class FeatureDisabled
{
    public const string Code = "feature_disabled";

    /// <summary><c>POST /ai/ask</c> and <c>POST /ai/tools/confirm/{callId}</c>.</summary>
    public static AppException AiChat() =>
        new(503, Code, "Chat is paused right now. Nothing you typed is lost — try again shortly.");

    /// <summary>
    /// The document-scan worker.
    ///
    /// <para>
    /// Not thrown by the upload route: an upload with the switch off still stores
    /// the bytes and the row, and the worker simply stops claiming. So the honest
    /// message is "queued", not "failed" — the capture is safe and will be read
    /// when the switch comes back.
    /// </para>
    /// </summary>
    public static AppException DocumentScan() =>
        new(503, Code, "Reading documents is paused right now. Your upload is saved and will be read shortly.");

    /// <summary><c>POST /ai/voice/transcribe</c> and <c>POST /api/speech/transcribe</c>.</summary>
    public static AppException Transcription() =>
        new(503, Code, "Voice transcription is paused right now. Try again shortly.");
}
