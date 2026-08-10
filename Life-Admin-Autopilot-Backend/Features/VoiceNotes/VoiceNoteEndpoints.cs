namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// Ports <c>server/src/routes/me.voiceNotes.ts</c> — five operations across three
/// paths.
///
/// <para>
/// Registration order mirrors the Node router so the two files read the same way.
/// Unlike the document-scan router there is no literal-vs-parameter hazard here:
/// <c>/me/voice-notes</c> has no sibling literal segment competing with
/// <c>/{id}</c>.
/// </para>
/// </summary>
public static class VoiceNoteEndpoints
{
    public static IEndpointRouteBuilder MapVoiceNoteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapVoiceNoteUpload();
        endpoints.MapVoiceNoteReads();
        endpoints.MapVoiceNoteWrites();

        return endpoints;
    }
}
