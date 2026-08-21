using System.Text;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.VoiceNotes;

/// <summary>
/// The rule that decides whether a spoken matter is filed silently or filed with a
/// question attached — the whole of "voice is fire-and-forget", since the surface has
/// already closed by the time any of this runs.
/// </summary>
public sealed class VoiceAutoFilePolicyTests
{
    private static readonly DateTime MondayMorning = new(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc);

    // Fixed-offset (+03:00, no DST) so the expected local times are arithmetic rather
    // than a second implementation of the timezone database.
    private const string Zone = "Asia/Riyadh";

    [Fact]
    public void files_a_dated_confident_matter_without_asking_anything()
    {
        var item = VoiceAutoFilePolicy.Apply(Draft(dueAt: MondayMorning, timeAssumed: false), Zone);

        Assert.Null(item.Clarification);

        // The gate routes on the clarification, but these two are what the review card
        // renders, so they have to agree with it.
        Assert.Equal("high", item.Confidence);
        Assert.Equal("clear", item.ReviewReason);
        Assert.Equal(MondayMorning, item.DueAt);
    }

    [Fact]
    public void files_an_undated_matter_and_asks_when_it_is_due()
    {
        // REVERSED, deliberately. This test used to assert the opposite — "buy milk"
        // has no date because there is no date, so asking manufactures uncertainty
        // rather than surfacing it. That reasoning is still on the record in
        // VoiceAutoFilePolicy; the product decision went the other way, because an
        // undated matter is filed into a list the user has to remember to open, and
        // remembering is the job the app took on.
        //
        // What has NOT changed is that the matter is filed either way, and files
        // UNDATED: the question is a question, not a condition of saving.
        var item = VoiceAutoFilePolicy.Apply(Draft(dueAt: null, timeAssumed: false), Zone);

        Assert.Null(item.DueAt);
        Assert.NotNull(item.Clarification);
        Assert.Equal("date", item.Clarification!.Kind);
    }

    [Fact]
    public void asks_for_the_time_when_the_clock_was_ours_rather_than_theirs()
    {
        // "Remind me Monday to go to the dentist" — a day, no time. The extractor has
        // to put SOME instant on the row, and left unmarked that guess is presented
        // to the user as though they had chosen it.
        var item = VoiceAutoFilePolicy.Apply(Draft(dueAt: MondayMorning, timeAssumed: true), Zone);

        var clarification = Assert.IsType<DraftClarification>(item.Clarification);
        Assert.Equal("date", clarification.Kind);
        Assert.Equal("vague_date", item.ReviewReason);

        // 06:00Z is 09:00 in +03:00.
        Assert.Equal("What time on Monday 17 August?", clarification.Question);

        // The FIRST option must be the reading the matter was actually filed under —
        // VoiceClarificationStaging files the task at Options[0].DueAt, so a
        // different first option would silently move a matter the card says it is
        // keeping as-is.
        Assert.Equal(MondayMorning, clarification.Options[0].DueAt);
        Assert.Equal("09:00", clarification.Options[0].Label);

        // …and the alternatives are the other ordinary parts of that same local day,
        // never a repeat of the guess.
        Assert.Equal(3, clarification.Options.Count);
        Assert.Equal(
            new DateTime?[] { MondayMorning, MondayMorning.AddHours(5), MondayMorning.AddHours(9) },
            clarification.Options.Select(o => o.DueAt).ToArray().AsEnumerable());
    }

    [Fact]
    public void asks_before_filing_a_second_matter_on_top_of_an_existing_one()
    {
        // The two free slots the caller found, which is what the alternatives are now
        // built from. This used to assert `MondayMorning.AddHours(2.5)` — a fixed jump
        // nothing had checked, which landed back inside the very window it was meant
        // to escape roughly half the time. Accepting that suggestion recreated the
        // clash, and the question could not be asked again because it had been
        // answered.
        var free = new[] { MondayMorning.AddHours(4), MondayMorning.AddHours(6) };

        var item = VoiceAutoFilePolicy.Apply(
            Draft(dueAt: MondayMorning, timeAssumed: false) with
            {
                Conflicts = new[]
                {
                    new PlanningConflict(
                        ObjectId.GenerateNewId(),
                        "Dentist",
                        MondayMorning,
                        "Scheduled within two hours of this.",
                        MatterConflict.TimeClash),
                },
            },
            Zone,
            free);

        var clarification = Assert.IsType<DraftClarification>(item.Clarification);
        Assert.Contains("Dentist", clarification.Question);

        // A clash is never cheap to be wrong about: the failure mode is a
        // double-booking nobody chose to make.
        Assert.Equal("high", clarification.CostOfWrong);
        Assert.Equal("time_clash", item.ReviewReason);

        // Option zero is still the reading the item was filed under — the staging
        // layer files at Options[0], so anything else there silently moves it.
        Assert.Equal(
            new DateTime?[] { MondayMorning, free[0], free[1] },
            clarification.Options.Select(o => o.DueAt).ToArray().AsEnumerable());
    }

    [Fact]
    public void offers_no_alternative_when_no_free_slot_was_found()
    {
        // A genuinely full week. Offering a time anyway would be inventing one, and
        // this is the lane where a suggestion gets tapped without being read — the
        // card still carries Keep, Skip and Discard, and a typed answer.
        var item = VoiceAutoFilePolicy.Apply(
            Draft(dueAt: MondayMorning, timeAssumed: false) with
            {
                Conflicts = new[]
                {
                    new PlanningConflict(
                        ObjectId.GenerateNewId(),
                        "Dentist",
                        MondayMorning,
                        "Scheduled within two hours of this.",
                        MatterConflict.TimeClash),
                },
            },
            Zone,
            Array.Empty<DateTime>());

        var clarification = Assert.IsType<DraftClarification>(item.Clarification);
        Assert.Equal(MondayMorning, Assert.Single(clarification.Options).DueAt);
    }

    [Fact]
    public void tells_a_duplicate_from_a_clash_by_kind_rather_than_by_wording()
    {
        // The reason here says nothing about duplication — under the old
        // `Reason.Contains("already")` test this was a time clash, and the user was
        // asked to move a matter instead of being told they already had it. A
        // localised reason failed the same way for every duplicate.
        var item = VoiceAutoFilePolicy.Apply(
            Draft(dueAt: MondayMorning, timeAssumed: false) with
            {
                Conflicts = new[]
                {
                    new PlanningConflict(
                        ObjectId.GenerateNewId(),
                        "Renew the passport",
                        MondayMorning,
                        "يشبه أمرًا لديك بالفعل.",
                        MatterConflict.Duplicate),
                },
            },
            Zone);

        var clarification = Assert.IsType<DraftClarification>(item.Clarification);
        Assert.Equal("confirm", clarification.Kind);
        Assert.Contains("Renew the passport", clarification.Question);
        Assert.Equal("maybe_duplicate", item.ReviewReason);
    }

    [Fact]
    public void asks_about_the_clash_rather_than_the_hour_when_a_draft_has_both()
    {
        // Both unsettled at once, and one item carries one question. The clash wins:
        // it is the more expensive mistake and the one the user cannot discover for
        // themselves, and answering it settles the assumed hour in the same tap.
        var free = new[] { MondayMorning.AddHours(4) };

        var item = VoiceAutoFilePolicy.Apply(
            Draft(dueAt: MondayMorning, timeAssumed: true) with
            {
                Conflicts = new[]
                {
                    new PlanningConflict(
                        ObjectId.GenerateNewId(),
                        "Dentist",
                        MondayMorning,
                        "Scheduled within two hours of this.",
                        MatterConflict.TimeClash),
                },
            },
            Zone,
            free);

        var clarification = Assert.IsType<DraftClarification>(item.Clarification);
        Assert.Contains("Dentist", clarification.Question);
        Assert.Equal("time_clash", item.ReviewReason);
    }

    [Fact]
    public void asks_whether_a_low_confidence_reading_was_meant_at_all()
    {
        var item = VoiceAutoFilePolicy.Apply(Draft(dueAt: null, timeAssumed: false, confidence: 0.3), Zone);

        var clarification = Assert.IsType<DraftClarification>(item.Clarification);

        // No date option can answer "did you mean this?", so it is a plain
        // confirmation. The single chip is not a dead end — the card stack's Skip and
        // Discard carry the other two answers.
        Assert.Equal("confirm", clarification.Kind);
        Assert.Equal("ambiguous_intent", item.ReviewReason);
        Assert.Single(clarification.Options);
    }

    [Theory]
    [InlineData("finance", "normal", "high")]
    [InlineData("health", "low", "high")]
    [InlineData("home", "urgent", "high")]
    [InlineData("home", "normal", "low")]
    public void treats_money_health_and_urgency_as_expensive_to_get_wrong(
        string domain,
        string priority,
        string expected)
    {
        var item = VoiceAutoFilePolicy.Apply(
            Draft(dueAt: MondayMorning, timeAssumed: true) with { Domain = domain, Priority = priority },
            Zone);

        Assert.Equal(expected, item.Clarification!.CostOfWrong);
    }

    [Fact]
    public void reads_an_unusable_timezone_as_absent_rather_than_failing_the_note()
    {
        // POST /me/voice-notes stores any 1..64 character string verbatim — no IANA
        // check, deliberately, because adding one would reject uploads the reference
        // server accepts. One bad header must not cost the user a recording.
        Assert.Null(VoiceAutoFilePolicy.ResolveZone("Mars/Olympus"));
        Assert.Null(VoiceAutoFilePolicy.ResolveZone(null));
        Assert.Equal(Zone, VoiceAutoFilePolicy.ResolveZone(Zone));

        var item = VoiceAutoFilePolicy.Apply(Draft(dueAt: MondayMorning, timeAssumed: true), timezone: null);

        // Still asks, still files — the times are simply read as UTC.
        Assert.NotNull(item.Clarification);
        Assert.Equal("06:00", item.Clarification!.Options[0].Label);
    }

    // ---- an item with no date ----------------------------------------------
    //
    // Every undated item is asked, including the ones that are undated on purpose.
    // That is the product decision, taken after the narrow version — gated on the
    // extractor's `kind` — turned out to key on a field the prompt defines as a
    // mirror of `dueAt`, and so could never fire. See NeedsADate.

    [Fact]
    public void asks_when_an_item_arrives_with_no_date()
    {
        var item = VoiceAutoFilePolicy.Apply(Draft(null, timeAssumed: false), Zone);

        Assert.NotNull(item.Clarification);
        Assert.Equal("date", item.Clarification!.Kind);
        Assert.Equal("incomplete", item.ReviewReason);
    }

    [Fact]
    public void asks_whatever_the_extractor_called_it()
    {
        // The `kind` the extractor returns is not consulted, and this pins that:
        // the prompt ties it to `dueAt`, so gating on it silently asked nothing.
        foreach (var kind in new[] { "list", "reminder", null })
        {
            var item = VoiceAutoFilePolicy.Apply(Draft(null, timeAssumed: false) with { Kind = kind }, Zone);
            Assert.NotNull(item.Clarification);
        }
    }

    [Fact]
    public void the_first_option_leaves_the_matter_undated()
    {
        // VoiceClarificationStaging files the task at Options[0].DueAt, so a
        // non-null first option would silently date a matter the user never dated
        // while the card claimed to be offering them the choice.
        var item = VoiceAutoFilePolicy.Apply(Draft(null, timeAssumed: false), Zone);

        Assert.Null(item.Clarification!.Options[0].DueAt);
        Assert.All(item.Clarification.Options.Skip(1), o => Assert.NotNull(o.DueAt));
    }

    [Fact]
    public void a_low_confidence_undated_item_is_asked_whether_it_is_real_first()
    {
        // Ordering: below the floor the model is unsure the item was requested at
        // all, and asking WHEN presumes it exists.
        var item = VoiceAutoFilePolicy.Apply(Draft(null, timeAssumed: false, confidence: 0.2), Zone);

        Assert.Equal("ambiguous_intent", item.ReviewReason);
    }

    [Fact]
    public void a_dated_item_is_still_left_alone()
    {
        var item = VoiceAutoFilePolicy.Apply(
            Draft(new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc), timeAssumed: false), Zone);

        Assert.Null(item.Clarification);
        Assert.Equal("clear", item.ReviewReason);
    }

    private static TaskDraft Draft(DateTime? dueAt, bool timeAssumed, double confidence = 0.9) => new(
        "Go to the dentist",
        "health",
        "normal",
        Kind: null,
        dueAt,
        Notes: null,
        SourceType: "voice",
        confidence,
        Conflicts: Array.Empty<PlanningConflict>(),
        TimeAssumed: timeAssumed);
}

/// <summary>
/// Reading the container off the bytes, because the declared one cannot be trusted:
/// the upload route's content-type filter has no <c>audio/wav</c> on it, so the web
/// recorder ships WAV as <c>application/octet-stream</c>.
/// </summary>
public sealed class VoiceAudioFormatTests
{
    [Fact]
    public void recognises_wav_behind_a_generic_content_type()
    {
        Assert.Equal("audio/wav", VoiceAudioFormat.Sniff(Riff("WAVE"), "application/octet-stream"));
    }

    [Fact]
    public void does_not_mistake_avi_or_webp_for_wav()
    {
        // "RIFF" alone opens all three; only the tag at offset 8 says which. Falls
        // through to the declared type rather than claiming to be audio.
        Assert.Equal("audio/mp4", VoiceAudioFormat.Sniff(Riff("AVI "), "audio/mp4"));
    }

    [Fact]
    public void recognises_the_iso_container_m4a_and_mp4_share()
    {
        var bytes = new byte[] { 0, 0, 0, 0x20 }.Concat(Encoding.ASCII.GetBytes("ftypM4A ")).ToArray();

        Assert.Equal("audio/mp4", VoiceAudioFormat.Sniff(bytes, "application/octet-stream"));
    }

    [Fact]
    public void believes_a_caller_that_named_a_real_type_when_the_bytes_say_nothing()
    {
        Assert.Equal("audio/mpeg", VoiceAudioFormat.Sniff(new byte[] { 1, 2, 3, 4 }, "audio/mpeg; codecs=1"));
    }

    [Fact]
    public void falls_back_to_wav_when_neither_the_bytes_nor_the_caller_say_anything()
    {
        // A wrong guess costs one provider round trip. Handing the provider
        // "application/octet-stream" costs the note.
        Assert.Equal("audio/wav", VoiceAudioFormat.Sniff(Array.Empty<byte>(), "application/octet-stream"));
        Assert.Equal("audio/wav", VoiceAudioFormat.Sniff(new byte[] { 9, 9 }, null));
    }

    private static byte[] Riff(string tag) =>
        Encoding.ASCII.GetBytes("RIFF").Concat(new byte[] { 0, 0, 0, 0 })
            .Concat(Encoding.ASCII.GetBytes(tag))
            .ToArray();
}
