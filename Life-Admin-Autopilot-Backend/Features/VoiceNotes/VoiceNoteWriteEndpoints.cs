using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.VoiceNotes.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// The two mutating routes: manual re-extraction and the review commit. Neither
/// is rate limited in Node — note that this leaves the AI cost of
/// <c>extract-tasks</c> ungated, which is the reference behaviour and not
/// something to "fix" here.
/// </summary>
public static class VoiceNoteWriteEndpoints
{
    public const string NotReadyCode = "voice_note_not_ready";
    public const string NotReadyMessage = "Voice note transcript is not ready yet.";

    public static IEndpointRouteBuilder MapVoiceNoteWrites(this IEndpointRouteBuilder endpoints)
    {
        // POST /me/voice-notes/{id}/extract-tasks — manual / fallback re-extraction.
        // The worker already does this automatically; this re-runs extraction on the
        // STORED transcript and never re-transcribes.
        endpoints.MapPost("/me/voice-notes/{id}/extract-tasks", async (
            HttpContext context,
            string id,
            IVoiceNoteRepository notes,
            IAccountProfileRepository profiles,
            IVoiceExtractor extractor,
            IVoiceExtractionCommit commit,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // BODY FIRST, note second. Deliberate, and the order is contractual: a
            // malformed timezone reaching the extractor used to crash
            // Intl.DateTimeFormat with a RangeError and surface as a 500, so Node
            // validates at the boundary to turn that into a 400.
            var requestedTimezone = await VoiceExtractBinder.ReadTimezoneAsync(context, cancellationToken);

            var note = await VoiceNoteReadEndpoints
                .FindOrThrowAsync(notes, id, caller.Id, cancellationToken);

            // Falsy, not null-check: Node tests `!note.transcript`, so a note whose
            // transcription came back as silence ('') is "not ready" too.
            if (string.IsNullOrEmpty(note.Transcript))
            {
                throw AppException.BadRequest(NotReadyCode, NotReadyMessage);
            }

            // Caller-supplied timezone (already IANA-validated) wins; otherwise fall
            // back to the one captured with the note.
            var timezone = requestedTimezone ?? note.Timezone;

            var drafts = await extractor.ExtractAsync(
                new VoiceExtractionRequest(
                    note.Transcript,
                    timezone,
                    await ReadLocaleAsync(profiles, note, cancellationToken)),
                cancellationToken);

            var gate = VoiceItemGate.GateAndKey(note.Id.ToString(), drafts);
            var created = await commit.ApplyAsync(note, gate, cancellationToken);

            await notes.SaveAsync(note, cancellationToken);

            return Results.Ok(new VoiceTasksResponse
            {
                Tasks = created.Select(t => t.ToDto()).ToList(),
                VoiceNote = note.ToDto(),
            });
        })
        .RequireAuthorization();

        // POST /me/voice-notes/{id}/review — commit a review pass.
        endpoints.MapPost("/me/voice-notes/{id}/review", async (
            HttpContext context,
            string id,
            IVoiceNoteRepository notes,
            IVoiceNoteTaskPersistence persistence,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // THREE phases, in this order, because Node's are in this order and all
            // three boundaries are observable:
            //
            //   1. express.json() — GLOBAL middleware, so it has already parsed (and
            //      already thrown) before the route's first line. Malformed or
            //      oversize is a 500 and it beats the lookup: verified live, a
            //      malformed body against an unknown id is 500, NOT 404.
            //   2. the note lookup            -> 404 voice_note_not_found
            //   3. the route's own zod schema -> 400 invalid_review
            //
            // Steps 2 and 3 are the OPPOSITE way round from extract-tasks above, and
            // that too is observable: a bad-but-parseable payload against a
            // stranger's note is 404 here and 400 there.
            var raw = await VoiceReviewBinder.ReadRawAsync(context, cancellationToken);

            var note = await VoiceNoteReadEndpoints
                .FindOrThrowAsync(notes, id, caller.Id, cancellationToken);

            // There is NO status precondition either, unlike the document-scan review
            // which demands `ready_for_review`. Calling this on a pending or failed
            // note is legal: with an empty body it simply clears the (empty) review
            // lane and marks the note `ready` with a `reviewedAt` stamp.
            var body = VoiceReviewBinder.Parse(raw);

            var held = new Dictionary<string, VoiceReviewItemDocument>(StringComparer.Ordinal);
            foreach (var item in note.ReviewItems)
            {
                held[item.Key] = item;
            }

            var accepted = new List<VoiceExtractedTaskDocument>();
            foreach (var accept in body.Accepts)
            {
                // Stale or already handled. Ignored idempotently rather than
                // rejected — the review card can be committed twice.
                if (!held.TryGetValue(accept.Key, out var item))
                {
                    continue;
                }

                accepted.Add(new VoiceExtractedTaskDocument
                {
                    Key = item.Key,
                    Title = accept.Title ?? item.Title,
                    Domain = accept.Domain ?? item.Domain,
                    Priority = accept.Priority ?? item.Priority,

                    // Forced by the accept, not carried: the user has confirmed it.
                    Confidence = "high",
                    ReviewReason = "clear",

                    // Carried, never supplied by the caller — the estimate came from
                    // the extraction pass that actually heard the transcript. The user
                    // retunes it afterwards via PATCH /me/tasks/{id}, which stamps
                    // source 'user'.
                    Estimate = item.Estimate,

                    DueAt = accept.DueAt ?? item.DueAt,
                    Notes = accept.Notes ?? item.Notes,
                });
            }

            var created = await persistence.PersistAsync(
                caller.Id,
                note.Id,
                accepted.Select(VoiceExtractionCommit.ToSeed).ToList(),
                cancellationToken);

            var idByKey = created
                .Where(t => t.SourceTaskKey is not null)
                .GroupBy(t => t.SourceTaskKey!)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

            foreach (var record in accepted)
            {
                record.TaskId = idByKey.TryGetValue(record.Key, out var taskId) ? taskId : null;
            }

            var handled = new HashSet<string>(
                accepted.Select(a => a.Key).Concat(body.Discards),
                StringComparer.Ordinal);

            // Accepted records are APPENDED to the audit list; every handled key —
            // accepted OR discarded — leaves the review lane.
            note.ExtractedTasks = note.ExtractedTasks.Concat(accepted).ToList();
            note.ReviewItems = note.ReviewItems.Where(r => !handled.Contains(r.Key)).ToList();

            if (note.ReviewItems.Count == 0)
            {
                note.Status = "ready";
                note.ReviewedAt = DateTime.UtcNow;
            }

            await notes.SaveAsync(note, cancellationToken);

            return Results.Ok(new VoiceTasksResponse
            {
                Tasks = created.Select(t => t.ToDto()).ToList(),
                VoiceNote = note.ToDto(),
            });
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// Read at extraction time rather than stamped at upload: a note can sit in the
    /// queue across a language change, and the review card is read after the
    /// extraction, not before it.
    /// </summary>
    internal static async Task<string?> ReadLocaleAsync(
        IAccountProfileRepository profiles,
        VoiceNoteDocument note,
        CancellationToken cancellationToken)
    {
        var user = await profiles.FindByIdAsync(note.UserId, cancellationToken).ConfigureAwait(false);
        return user?.Locale;
    }
}
