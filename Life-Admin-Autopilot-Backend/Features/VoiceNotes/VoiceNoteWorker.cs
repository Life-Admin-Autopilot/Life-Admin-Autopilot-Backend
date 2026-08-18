using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Ops;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// Claim → transcribe → extract → gate → persist → notify. Port of the worker in
/// <c>lib/voiceNoteTranscriber.ts</c>.
///
/// <para>
/// <b>With no AI provider configured this worker legitimately fails every
/// note.</b> That is the reference server's behaviour without
/// <c>GEMINI_API_KEY</c>, and the parity target: <c>ai_not_configured</c> is a
/// 503, the retry classifier reads 503 as transient, and the note therefore burns
/// its whole attempt ladder — roughly twenty seconds — before settling at
/// <c>failed</c>. See <see cref="NullVoiceTranscriber"/>.
/// </para>
/// </summary>
internal sealed class VoiceNoteWorker : KernelPollingWorker
{
    private readonly Random _jitter = new();

    public VoiceNoteWorker(IServiceProvider services, ILogger<VoiceNoteWorker> logger)
        : base(services, logger)
    {
    }

    protected override TimeSpan Interval => VoiceNoteRetryPolicy.PollInterval;

    protected override string WorkerName => "voice-note";

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();

        // The transcription kill switch, checked BEFORE claiming — same reasoning as
        // the document-scan worker, and it matters more here. This worker's retry
        // classifier reads a 503 as transient, so a note that claimed and then hit a
        // pulled switch would burn its entire attempt ladder in about twenty seconds
        // and settle at `failed`. Pulling the switch for a minute would permanently
        // fail every note recorded in that minute, each one a recording the user has
        // already dismissed from the screen and cannot retake.
        //
        // Not claiming leaves them `pending` and the queue drains when the switch
        // returns, with no attempt spent.
        var flags = scope.ServiceProvider.GetRequiredService<IFeatureFlagStore>();

        if (await flags.IsDisabledAsync(FeatureFlags.Transcription, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var notes = scope.ServiceProvider.GetRequiredService<IVoiceNoteRepository>();

        var claimed = await notes
            .ClaimNextAsync(DateTime.UtcNow, VoiceNoteRetryPolicy.Lock, cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return;
        }

        await ProcessAsync(scope.ServiceProvider, notes, claimed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotent on reclaim: a note that already has a transcript AND staged
    /// extraction re-persists from what is stored and NEVER re-transcribes — LLM
    /// nondeterminism would shift the idempotency keys and fan one item out into
    /// two Tasks.
    /// </summary>
    private async Task ProcessAsync(
        IServiceProvider services,
        IVoiceNoteRepository notes,
        VoiceNoteDocument note,
        CancellationToken cancellationToken)
    {
        try
        {
            var hasExtraction = !string.IsNullOrEmpty(note.Transcript)
                && (note.ExtractedTasks.Count > 0
                    || note.ReviewItems.Count > 0
                    || note.ClarifyItems.Count > 0);

            var held = hasExtraction
                ? await RepersistAsync(services, note, cancellationToken).ConfigureAwait(false)
                : await TranscribeAndExtractAsync(services, notes, note, cancellationToken).ConfigureAwait(false);

            note.LockedUntil = null;
            note.LastError = null;
            note.FailureReason = null;
            await notes.SaveAsync(note, cancellationToken).ConfigureAwait(false);

            await NotifyAsync(services, notes, note, held, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleFailureAsync(notes, note, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The only place a retry attempt is SPENT. Claiming or reclaiming a note must
    /// not burn one — otherwise a crash or a lock expiry eats the retry budget and a
    /// note reaches <c>failed</c> without ever genuinely failing
    /// <c>maxAttempts</c> times. Reclaiming the same in-progress note re-enters here
    /// and does count, which is correct: it re-transcribes from scratch.
    /// </summary>
    /// <returns>How many of the note's matters ended up with a question attached.</returns>
    private static async Task<int> TranscribeAndExtractAsync(
        IServiceProvider services,
        IVoiceNoteRepository notes,
        VoiceNoteDocument note,
        CancellationToken cancellationToken)
    {
        note.Attempts += 1;
        note.Status = "transcribing";
        note.LockedUntil = DateTime.UtcNow.Add(VoiceNoteRetryPolicy.Lock);
        await notes.SaveAsync(note, cancellationToken).ConfigureAwait(false);

        var storage = services.GetRequiredService<IVoiceNoteStorage>();
        var transcriber = services.GetRequiredService<IVoiceTranscriber>();
        var locale = await ReadLocaleAsync(services, note, cancellationToken).ConfigureAwait(false);

        var bytes = await storage.GetAsync(note.StorageKey, cancellationToken).ConfigureAwait(false);

        note.Transcript = await transcriber
            .TranscribeAsync(
                new VoiceTranscriptionRequest(bytes, note.MimeType ?? "audio/m4a", locale),
                cancellationToken)
            .ConfigureAwait(false);

        note.Status = "extracting";
        note.LockedUntil = DateTime.UtcNow.Add(VoiceNoteRetryPolicy.Lock);
        await notes.SaveAsync(note, cancellationToken).ConfigureAwait(false);

        // Silence short-circuits: an empty transcript is not a failure, it just has
        // nothing to extract. Calling the extractor on '' would spend a model round
        // to be told the same thing.
        var drafts = string.IsNullOrWhiteSpace(note.Transcript)
            ? Array.Empty<DraftVoiceItem>()
            : await services.GetRequiredService<IVoiceExtractor>()
                .ExtractAsync(
                    new VoiceExtractionRequest(
                        note.UserId,
                        note.Transcript,
                        note.Timezone,
                        locale,

                        // When they SPOKE, not when we got to it. A note dictated at
                        // 23:55 and claimed at 00:02 has to read "tomorrow" as the
                        // day the speaker meant; anchoring on the worker's clock
                        // moves every relative date in a late-night capture by a day.
                        SpokenAt: note.ClientCapturedAt),
                    cancellationToken)
                .ConfigureAwait(false);

        var gate = VoiceItemGate.GateAndKey(note.Id.ToString(), drafts);
        var outcome = await services.GetRequiredService<IVoiceExtractionCommit>()
            .ApplyAsync(note, gate, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Held;
    }

    /// <summary>
    /// Recovery path — re-persist from what is already stored. The staged
    /// clarify items are re-upserted too, which is why this cannot just skip
    /// straight to the status write.
    /// </summary>
    private static async Task<int> RepersistAsync(
        IServiceProvider services,
        VoiceNoteDocument note,
        CancellationToken cancellationToken)
    {
        var persistence = services.GetRequiredService<IVoiceNoteTaskPersistence>();
        var created = await persistence
            .PersistAsync(
                note.UserId,
                note.Id,
                note.ExtractedTasks.Select(VoiceExtractionCommit.ToSeed).ToList(),
                cancellationToken)
            .ConfigureAwait(false);

        VoiceExtractionCommit.Backlink(note, created);

        var held = await services.GetRequiredService<IVoiceClarificationStaging>()
            .PersistAsync(note, cancellationToken)
            .ConfigureAwait(false);

        note.Status = note.ReviewItems.Count > 0 ? "needs_review" : "ready";

        return held;
    }

    private async Task HandleFailureAsync(
        IVoiceNoteRepository notes,
        VoiceNoteDocument note,
        Exception error,
        CancellationToken cancellationToken)
    {
        Logger.LogError(
            error,
            "voiceNote:job-error noteId={NoteId} attempts={Attempts}",
            note.Id,
            note.Attempts);

        if (VoiceNoteRetryPolicy.IsTransient(error) && note.Attempts < note.MaxAttempts)
        {
            note.Status = "pending";
            note.NextRunAt = DateTime.UtcNow.Add(VoiceNoteRetryPolicy.Backoff(note.Attempts, _jitter));
            note.LockedUntil = null;
            note.LastError = error.Message;
        }
        else
        {
            note.Status = "failed";

            // Both hold the same text, and only one of them survives toJSON.
            // failureReason is what the error screen renders; lastError is machinery.
            note.FailureReason = error.Message;
            note.LastError = error.Message;
            note.LockedUntil = null;
        }

        try
        {
            await notes.SaveAsync(note, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveFailure)
        {
            // Save-on-failure is best effort. The lease will expire and the note gets
            // reclaimed; losing this write costs a poll, not the note.
            Logger.LogWarning(saveFailure, "voiceNote:failure-save-failed noteId={NoteId}", note.Id);
        }
    }

    /// <summary>
    /// Always-surface (product rule), and now the ONLY place the outcome exists: the
    /// recording surface closed the moment the upload was accepted, so a note that
    /// says nothing has, from the user's side, done nothing.
    ///
    /// <para>
    /// The outbox guard is <c>notifiedAt</c>, so a crash between the commit and the
    /// feed write re-sends rather than double-sends. Note this covers the COMPLETION
    /// row only — the per-question rows are guarded separately, by whether their
    /// clarification was actually inserted, because they are written where that is
    /// known.
    /// </para>
    /// </summary>
    private async Task NotifyAsync(
        IServiceProvider services,
        IVoiceNoteRepository notes,
        VoiceNoteDocument note,
        int held,
        CancellationToken cancellationToken)
    {
        if ((note.Status != "needs_review" && note.Status != "ready") || note.NotifiedAt is not null)
        {
            return;
        }

        await services.GetRequiredService<VoiceNoteOutcomeNotifier>()
            .CompletedAsync(note.UserId, note.ExtractedTasks.Count, held, cancellationToken)
            .ConfigureAwait(false);

        note.NotifiedAt = DateTime.UtcNow;

        try
        {
            await notes.SaveAsync(note, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // notifiedAt persistence is best effort — a duplicate notification after
            // a crash beats losing the completion row entirely.
            Logger.LogWarning(ex, "voiceNote:notified-save-failed noteId={NoteId}", note.Id);
        }
    }

    private static async Task<string?> ReadLocaleAsync(
        IServiceProvider services,
        VoiceNoteDocument note,
        CancellationToken cancellationToken)
    {
        var profiles = services.GetRequiredService<IAccountProfileRepository>();
        var user = await profiles.FindByIdAsync(note.UserId, cancellationToken).ConfigureAwait(false);
        return user?.Locale;
    }
}
