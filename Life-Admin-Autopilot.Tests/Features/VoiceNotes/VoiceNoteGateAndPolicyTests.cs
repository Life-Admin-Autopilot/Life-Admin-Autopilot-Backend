using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.Tests.Features.VoiceNotes;

/// <summary>
/// The deterministic core of the slice: the three-lane gate, the idempotency key
/// it stamps, the retry classifier that decides how long a note takes to fail,
/// and the notification copy.
/// </summary>
public sealed class VoiceNoteGateAndPolicyTests
{
    private const string NoteId = "6a78bc9caa461ae1dc64a294";

    // ---- The gate ----------------------------------------------------------

    [Theory]
    [InlineData("high", "clear", "autoSave")]
    [InlineData("medium", "clear", "review")]
    [InlineData("low", "clear", "review")]
    [InlineData("high", "vague_date", "review")]
    [InlineData("high", "low_asr", "review")]
    public void auto_saves_only_a_fully_confident_and_fully_clear_item(
        string confidence,
        string reviewReason,
        string expectedLane)
    {
        // Conservative by product decision: a wrong auto-saved task is the worst
        // failure mode in a personal app, so BOTH signals must be clean.
        var gate = VoiceItemGate.GateAndKey(NoteId, new[] { Draft("Pay the bill", confidence, reviewReason) });

        Assert.Equal(expectedLane == "autoSave" ? 1 : 0, gate.AutoSave.Count);
        Assert.Equal(expectedLane == "review" ? 1 : 0, gate.Review.Count);
        Assert.Empty(gate.Clarify);
    }

    [Fact]
    public void routes_a_clarifiable_item_to_the_question_lane_however_confident_it_is()
    {
        // Presence of a clarification block decides the lane, NOT confidence — a
        // high-confidence item with an unanswered question must still be asked
        // about, never silently auto-saved.
        var item = Draft("Renew the passport", "high", "clear") with
        {
            Clarification = new DraftClarification(
                "Which Tuesday did you mean?",
                "date",
                "high",
                new[] { new DraftClarifyOption("Tue 11 Aug", new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc)) }),
        };

        var gate = VoiceItemGate.GateAndKey(NoteId, new[] { item });

        Assert.Empty(gate.AutoSave);
        Assert.Empty(gate.Review);
        Assert.Single(gate.Clarify);
    }

    [Fact]
    public void assigns_the_same_key_to_the_same_note_position_and_title()
    {
        // The whole reason a worker reclaim is safe: re-running extraction produces
        // the SAME keys, so the upserts are no-ops instead of duplicates.
        var first = VoiceItemGate.MakeItemKey(NoteId, 0, "Book the MOT");
        var second = VoiceItemGate.MakeItemKey(NoteId, 0, "Book the MOT");

        Assert.Equal(first, second);
        Assert.Equal(24, first.Length);
    }

    [Theory]
    [InlineData("Book the MOT", "Book  the   MOT")]
    [InlineData("Book the MOT", "book the mot")]
    [InlineData("Book the MOT", "  Book the MOT!  ")]
    public void normalises_punctuation_case_and_spacing_out_of_the_key(string a, string b)
    {
        // normalizeTitle: lowercase, NFKD, non-alphanumerics to spaces, collapse,
        // trim. Two phrasings of the same matter must not become two Tasks.
        Assert.Equal(VoiceItemGate.MakeItemKey(NoteId, 0, a), VoiceItemGate.MakeItemKey(NoteId, 0, b));
    }

    [Fact]
    public void turns_interior_punctuation_into_separate_words_rather_than_deleting_it()
    {
        // The flip side, and it is the JS behaviour, not a bug to round off:
        // punctuation becomes a SPACE, so "M.O.T" normalises to "m o t" and does not
        // collide with "MOT". Deleting the punctuation instead would silently merge
        // two genuinely different titles onto one idempotency key.
        Assert.NotEqual(
            VoiceItemGate.NormalizeTitle("Book the MOT"),
            VoiceItemGate.NormalizeTitle("Book the M.O.T"));

        Assert.Equal("book the m o t", VoiceItemGate.NormalizeTitle("Book the M.O.T!"));
    }

    [Fact]
    public void gives_different_positions_different_keys_even_for_an_identical_title()
    {
        // One recording genuinely can name the same errand twice; the position keeps
        // them apart so neither is swallowed by the other's upsert.
        Assert.NotEqual(
            VoiceItemGate.MakeItemKey(NoteId, 0, "call the vet"),
            VoiceItemGate.MakeItemKey(NoteId, 1, "call the vet"));
    }

    [Fact]
    public void gives_different_notes_different_keys_for_the_same_item()
    {
        Assert.NotEqual(
            VoiceItemGate.MakeItemKey(NoteId, 0, "call the vet"),
            VoiceItemGate.MakeItemKey("6a78bc9caa461ae1dc64a295", 0, "call the vet"));
    }

    // ---- The retry classifier ---------------------------------------------

    [Fact]
    public void treats_ai_not_configured_as_transient_which_is_why_a_note_takes_20_seconds_to_fail()
    {
        // This single classification is the whole reason an unconfigured server
        // burns the attempt ladder instead of failing on the first tick. Reproducing
        // the DELAY is part of parity: the harness polls to a terminal state.
        var error = new AppException(503, "ai_not_configured", NullVoiceTranscriber.NotConfiguredMessage);

        Assert.True(VoiceNoteRetryPolicy.IsTransient(error));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(504)]
    public void treats_the_four_gemini_overload_statuses_as_transient(int status)
    {
        Assert.True(VoiceNoteRetryPolicy.IsTransient(new AppException(status, "boom", "nope")));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public void treats_ordinary_client_errors_as_permanent(int status)
    {
        Assert.False(VoiceNoteRetryPolicy.IsTransient(new AppException(status, "boom", "nope")));
    }

    [Theory]
    [InlineData("model is overloaded")]
    [InlineData("Service UNAVAILABLE")]
    [InlineData("please try again later")]
    [InlineData("internal error")]
    public void also_classifies_by_message_for_errors_that_carry_no_status(string message)
    {
        Assert.True(VoiceNoteRetryPolicy.IsTransient(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData(1, 2_000)]
    [InlineData(2, 4_000)]
    [InlineData(3, 8_000)]
    [InlineData(10, 60_000)]
    public void doubles_the_backoff_per_attempt_up_to_a_one_minute_cap(int attempts, int expectedFloorMs)
    {
        // Jitter adds up to a second on top, so the assertion is a window.
        var delay = VoiceNoteRetryPolicy.Backoff(attempts, new Random(1));

        Assert.InRange(delay.TotalMilliseconds, expectedFloorMs, expectedFloorMs + 1_000);
    }

    [Fact]
    public void uses_a_shorter_lock_than_the_document_scan_worker()
    {
        // 120 s against 180 s. One voice extraction is a single pass; a scan is a
        // multi-page vision call. Sharing one constant would either strand voice
        // notes for an extra minute after a crash or cut scans off mid-flight.
        Assert.Equal(TimeSpan.FromMinutes(2), VoiceNoteRetryPolicy.Lock);
    }

    // ---- Notification copy -------------------------------------------------

    [Theory]
    [InlineData(0, 0, "Nothing to file from that one.")]
    [InlineData(1, 0, "1 matter filed")]
    [InlineData(3, 0, "3 matters filed")]
    [InlineData(0, 1, "1 matter filed · 1 need your input")]
    [InlineData(2, 2, "4 matters filed · 2 need your input")]
    public void counts_filed_matters_across_both_lanes(int filed, int held, string expected)
    {
        // The count the user cares about is how many matters got FILED — the held
        // lane produces a real Task too, so it is added in, not reported separately.
        // The second half says what is WAITING ON THEM rather than what the machine
        // is unsure about: "2 guesses to confirm" described our state, not theirs.
        Assert.Equal(expected, VoiceNoteOutcomeNotifier.CompletionBody(filed, held));
    }

    // ---- The source quote --------------------------------------------------

    [Fact]
    public void drops_an_empty_transcript_quote_rather_than_storing_a_blank()
    {
        Assert.Null(VoiceClarificationStaging.ClampSourceText("   "));
        Assert.Null(VoiceClarificationStaging.ClampSourceText(null));
    }

    [Fact]
    public void clamps_a_long_transcript_to_600_characters_with_an_ellipsis()
    {
        var clamped = VoiceClarificationStaging.ClampSourceText(new string('a', 1_000));

        Assert.NotNull(clamped);
        Assert.Equal(600, clamped!.Length);
        Assert.EndsWith("…", clamped, StringComparison.Ordinal);
    }

    [Fact]
    public void keeps_a_transcript_that_fits_verbatim()
    {
        Assert.Equal("remember the milk", VoiceClarificationStaging.ClampSourceText("  remember the milk  "));
    }

    // ---- Storage keys ------------------------------------------------------

    [Fact]
    public void always_stores_audio_under_an_m4a_extension()
    {
        // Node hard-codes `.m4a` regardless of which of the four accepted content
        // types arrived. Deriving the extension from the mime type — as the
        // document-scan key builder legitimately does — would put the two servers'
        // files in different places.
        Assert.Equal(
            "6a78bbbeaa461ae1dc64a0bf/6a78bc9caa461ae1dc64a294.m4a",
            VoiceNoteStorageKeys.Build("6a78bbbeaa461ae1dc64a0bf", "6a78bc9caa461ae1dc64a294"));
    }

    // ---- The no-key seam ---------------------------------------------------

    [Fact]
    public async Task fails_transcription_with_the_verbatim_node_sentence()
    {
        // Not a stub that fabricates a transcript: the reference server genuinely
        // cannot transcribe without a key, and this exact sentence is what lands in
        // the note's failureReason.
        var error = await Assert.ThrowsAsync<AppException>(() =>
            new NullVoiceTranscriber().TranscribeAsync(
                new VoiceTranscriptionRequest(Array.Empty<byte>(), "audio/m4a", null)));

        Assert.Equal(503, error.Status);
        Assert.Equal("ai_not_configured", error.Code);
        Assert.Equal("AI is not configured. Set GEMINI_API_KEY in server/.env to enable.", error.Message);
    }

    private static DraftVoiceItem Draft(string title, string confidence, string reviewReason) =>
        new(title, "finance", "normal", confidence, reviewReason, Array.Empty<string>());
}
