using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.VoiceNotes.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// <c>POST /me/voice-notes</c> — the raw-bytes upload.
///
/// <para>
/// <b>The body is the audio.</b> There is no multipart form and no field name;
/// the metadata rides on <c>x-voice-note-*</c> headers precisely so the request
/// can stay a single binary body rather than dragging multer in for one field.
/// </para>
///
/// <para>
/// Unlike the document-scan upload there is NO content-type check in the handler
/// and NO quota admission — voice notes are bounded only by the rate limiter.
/// </para>
/// </summary>
public static class VoiceNoteUploadEndpoint
{
    /// <summary>
    /// The <c>express.raw({ type: [...] })</c> filter. A request whose type is not
    /// on this list is never parsed, so <c>req.body</c> stays <c>{}</c> and the
    /// handler's first check reports <c>empty_body</c> — NOT an unsupported-media
    /// error, and NOT the transport 413 even for an enormous body.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedMime = new[]
    {
        "audio/m4a", "audio/mp4", "audio/aac", "application/octet-stream",
    };

    public static IEndpointRouteBuilder MapVoiceNoteUpload(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/me/voice-notes", async (
            HttpContext context,
            IVoiceNoteRepository notes,
            IVoiceNoteStorage storage,
            VoiceNoteOptions options,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var contentType = ReadContentType(context.Request);

            // The type filter runs BEFORE the size ceiling, exactly as the middleware
            // order does in Node: an 11 MiB text/plain body is `empty_body` (the
            // parser skipped it), while an 11 MiB audio/m4a body is a 500 (the parser
            // took it and blew its limit).
            if (!AllowedMime.Contains(contentType, StringComparer.Ordinal))
            {
                throw AppException.BadRequest("empty_body", "No audio payload received.");
            }

            var upload = await RawBodyReader.ReadAsync(
                context,
                options.MaxBytes,
                emptyMessage: "No audio payload received.",
                tooLargeMessage: $"Voice note exceeds {options.MaxMegabytes}MB.",
                cancellationToken: cancellationToken);

            var metadata = VoiceMetadataBinder.Read(context.Request);

            // The id is generated up front so the storage key can be computed before
            // the first write — the schema marks `storageKey` required.
            var id = ObjectId.GenerateNewId();
            var key = VoiceNoteStorageKeys.Build(caller.IdString, id.ToString());

            // Bytes first, row second. A processing failure then never costs the user
            // their capture — a retry is a worker re-claim, not a recording they have
            // to make again.
            await storage.PutAsync(key, upload.Bytes, cancellationToken);

            var now = DateTime.UtcNow;
            var note = new VoiceNoteDocument
            {
                Id = id,
                UserId = caller.Id,
                StorageKey = key,
                DurationMs = metadata.DurationMs,
                ByteSize = upload.Bytes.Length,
                Source = metadata.Source,
                Status = "pending",
                ClientCapturedAt = metadata.CapturedAt,
                Timezone = metadata.Timezone,
                MimeType = upload.ContentType,
                NextRunAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await notes.InsertAsync(note, cancellationToken);

            // Node kicks `enqueueTranscription(note.id)` here for low latency. The
            // .NET worker polls on a 2 s tick and claims the same row, so the only
            // difference is up to two seconds before the note leaves `pending` —
            // invisible to a client that polls the note, which is the only way this
            // state is ever observed.
            return Results.Json(
                new VoiceSingleResponse { VoiceNote = note.ToDto() },
                statusCode: StatusCodes.Status202Accepted);
        })
        .RequireAuthorization()

        // SHARED bucket with POST /ai/voice/transcribe — one user's twelve voice
        // actions a minute, whichever surface they come from. Use the constant; a
        // literal would silently create a private bucket and double the real budget.
        .RateLimited(KernelRateLimiters.AiVoice);

        return endpoints;
    }

    /// <summary>
    /// Node: <c>(req.header('content-type') ?? '').split(';')[0]?.trim()</c> — the
    /// parameters after the semicolon are dropped, so
    /// <c>audio/m4a; charset=binary</c> still matches.
    /// </summary>
    private static string ReadContentType(HttpRequest request) =>
        request.Headers.ContentType.ToString().Split(';')[0].Trim();
}
