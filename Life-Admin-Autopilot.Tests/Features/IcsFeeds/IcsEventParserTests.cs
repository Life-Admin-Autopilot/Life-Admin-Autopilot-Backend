using Life_Admin_Autopilot.BLL.Features.IcsFeeds;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>
/// Port of <c>modules/integrations/ics/parseIcsEvents.test.ts</c>, case for case,
/// plus the reader-level cases (line folding, escaping) the reference gets from its
/// calendar library and this slice has to earn.
/// </summary>
public sealed class IcsEventParserTests
{
    private const string UserTz = "Asia/Dubai";

    private static readonly DateTime WideFrom = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WideTo = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- single events -----------------------------------------------------

    [Fact]
    public void emits_one_occurrence_and_uses_the_bare_uid_as_external_id()
    {
        var events = Parse(Feed(new[]
        {
            "UID:one",
            "DTSTART;TZID=Europe/London:20260903T090000",
            "SUMMARY:Parents evening",
        }));

        var occurrence = Assert.Single(events);
        Assert.Equal("one", occurrence.Uid);
        Assert.Equal("one", occurrence.ExternalId);
        Assert.False(occurrence.IsRecurring);
        Assert.Equal("2026-09-03T08:00:00.000Z", Iso(occurrence.When.DueAt));
        Assert.Equal("exact", occurrence.When.Precision);
        Assert.Equal("high", occurrence.When.Confidence);
    }

    [Fact]
    public void excludes_occurrences_outside_the_window()
    {
        var events = Parse(
            Feed(new[] { "UID:old", "DTSTART:20200101T090000Z", "SUMMARY:Ancient" }),
            from: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Empty(events);
    }

    [Fact]
    public void carries_description_and_location_through()
    {
        var events = Parse(Feed(new[]
        {
            "UID:two",
            "DTSTART:20260903T090000Z",
            "SUMMARY:Dentist",
            "DESCRIPTION:Bring the referral letter",
            "LOCATION:12 High Street",
        }));

        Assert.Equal("Bring the referral letter", events[0].Description);
        Assert.Equal("12 High Street", events[0].Location);
    }

    [Fact]
    public void applies_the_users_default_time_to_an_all_day_event()
    {
        // Nothing is invented: the date is the source's and the time is the user's.
        var events = Parse(Feed(new[] { "UID:allday", "DTSTART;VALUE=DATE:20260903", "SUMMARY:Term starts" }));

        var occurrence = Assert.Single(events);
        Assert.Equal("dateOnly", occurrence.When.Precision);
        Assert.Equal("high", occurrence.When.Confidence);
        Assert.False(occurrence.When.NeedsConfirmation);

        // 09:00 in Asia/Dubai is 05:00Z.
        Assert.Equal("2026-09-03T05:00:00.000Z", Iso(occurrence.When.DueAt));
    }

    [Fact]
    public void treats_an_unresolvable_tzid_as_floating_and_names_it()
    {
        // Outlook emits Windows zone ids. The name is kept so the user can be told
        // what was intended, even though it cannot be honoured.
        var events = Parse(Feed(new[]
        {
            "UID:outlook",
            "DTSTART;TZID=\"GMT Standard Time\":20260903T090000",
            "SUMMARY:Review",
        }));

        var occurrence = Assert.Single(events);
        Assert.Equal("floating", occurrence.When.Precision);
        Assert.Equal("low", occurrence.When.Confidence);
        Assert.True(occurrence.When.NeedsConfirmation);
        Assert.Equal("unrecognised timezone \"GMT Standard Time\" — assumed yours", occurrence.When.When.Note);
    }

    // ---- recurrence --------------------------------------------------------

    [Fact]
    public void expands_a_bounded_series()
    {
        var events = Parse(Feed(new[]
        {
            "UID:weekly",
            "DTSTART:20260901T090000Z",
            "RRULE:FREQ=WEEKLY;COUNT=4",
            "SUMMARY:Swimming",
        }));

        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.True(e.IsRecurring));
    }

    [Fact]
    public void converts_every_occurrence_independently_across_a_dst_boundary()
    {
        // THE test for recurrence correctness. BST ends 2026-10-25, so a weekly 09:00
        // Europe/London series is 08:00Z before and 09:00Z after. Expanding once and
        // adding seven days repeatedly would keep every occurrence at 08:00Z and put
        // the school run an hour early for the rest of the year.
        var events = Parse(Feed(new[]
        {
            "UID:term",
            "DTSTART;TZID=Europe/London:20261020T090000",
            "RRULE:FREQ=WEEKLY;COUNT=3",
            "SUMMARY:Assembly",
        }));

        Assert.Equal(
            new[]
            {
                "2026-10-20T08:00:00.000Z", // BST
                "2026-10-27T09:00:00.000Z", // GMT
                "2026-11-03T09:00:00.000Z", // GMT
            },
            events.Select(e => Iso(e.When.DueAt)));
    }

    [Fact]
    public void gives_each_occurrence_a_distinct_stable_external_id()
    {
        string[] Ids() => Parse(Feed(new[]
        {
            "UID:weekly",
            "DTSTART:20260901T090000Z",
            "RRULE:FREQ=WEEKLY;COUNT=3",
            "SUMMARY:Swimming",
        })).Select(e => e.ExternalId).ToArray();

        var first = Ids();

        Assert.Equal(3, first.Distinct().Count());

        // Stable across polls — otherwise every poll creates three new matters.
        Assert.Equal(first, Ids());
    }

    [Fact]
    public void keeps_external_ids_stable_when_the_user_changes_timezone()
    {
        // Built from the occurrence's own wall clock rather than the resolved UTC
        // instant, so moving abroad re-files existing matters instead of duplicating
        // every one of them.
        string[] Ids(string timezone) => Parse(
            Feed(new[]
            {
                "UID:weekly",
                "DTSTART:20260901T090000",
                "RRULE:FREQ=WEEKLY;COUNT=3",
                "SUMMARY:Swimming",
            }),
            timezone: timezone).Select(e => e.ExternalId).ToArray();

        Assert.Equal(Ids("Asia/Dubai"), Ids("Europe/London"));
    }

    [Fact]
    public void caps_an_unbounded_rrule_instead_of_hanging()
    {
        // FREQ=DAILY with no UNTIL and no COUNT is legal and infinite. A stranger's
        // feed URL must not be able to spin the server.
        var started = DateTime.UtcNow;

        var events = Parse(Feed(new[]
        {
            "UID:forever",
            "DTSTART:20260101T090000Z",
            "RRULE:FREQ=DAILY",
            "SUMMARY:Endless",
        }));

        Assert.InRange(events.Count, 1, IcsEventParser.MaxOccurrencesPerSeries);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void caps_the_whole_feed_not_just_one_series()
    {
        var series = Enumerable
            .Range(0, 20)
            .Select(i => new[]
            {
                $"UID:series-{i}",
                "DTSTART:20260101T090000Z",
                "RRULE:FREQ=DAILY",
                $"SUMMARY:Series {i}",
            })
            .ToArray();

        var events = Parse(Feed(series));

        Assert.True(events.Count <= IcsEventParser.MaxOccurrencesTotal);
    }

    [Fact]
    public void survives_a_by_rule_padded_with_tens_of_thousands_of_tokens()
    {
        // A CPU-exhaustion DoS a stranger can trigger with one RRULE line: the BY*
        // list is re-scanned once per period, so an unbounded list multiplied out into
        // minutes of synchronous work on the subscribe request. Measured at 14 s for
        // 50k tokens before the parse-time cap.
        var padded = "RRULE:FREQ=MONTHLY;BYDAY=" + string.Join(",", Enumerable.Repeat("MO", 50_000));
        var started = DateTime.UtcNow;

        var events = Parse(Feed(new[] { "UID:dos", "DTSTART:20260105T090000Z", padded, "SUMMARY:Padded" }));

        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(5),
            "expansion must stay bounded regardless of how long the BY* list is");

        // Still correct, not merely fast: every Monday, deduplicated.
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(DayOfWeek.Monday, e.When.DueAt.DayOfWeek));
        Assert.Equal(events.Count, events.Select(e => e.ExternalId).Distinct().Count());
    }

    [Fact]
    public void keeps_the_base_occurrence_when_interval_runs_off_the_calendar()
    {
        // FREQ=YEARLY;INTERVAL=999999999 overflows DateTime.AddYears. That throw is
        // swallowed by the per-VEVENT catch, which used to lose the event ENTIRELY —
        // including the one occurrence the publisher actually scheduled — with nothing
        // logged to explain the disappearance.
        var events = Parse(Feed(new[]
        {
            "UID:overflow",
            $"DTSTART:{DateTime.UtcNow.AddDays(30):yyyyMMdd'T'HHmmss}Z",
            "RRULE:FREQ=YEARLY;INTERVAL=999999999",
            "SUMMARY:Once",
        }));

        Assert.Equal("Once", Assert.Single(events).Summary);
    }

    [Fact]
    public void honours_exdate()
    {
        var events = Parse(Feed(new[]
        {
            "UID:weekly",
            "DTSTART:20260901T090000Z",
            "RRULE:FREQ=WEEKLY;COUNT=3",
            "EXDATE:20260908T090000Z",
            "SUMMARY:Swimming",
        }));

        Assert.Equal(
            new[] { "2026-09-01T09:00:00.000Z", "2026-09-15T09:00:00.000Z" },
            events.Select(e => Iso(e.When.DueAt)));
    }

    [Fact]
    public void stops_a_series_at_until()
    {
        var events = Parse(Feed(new[]
        {
            "UID:termly",
            "DTSTART:20260901T090000Z",
            "RRULE:FREQ=WEEKLY;UNTIL=20260916T000000Z",
            "SUMMARY:Assembly",
        }));

        Assert.Equal(
            new[] { "2026-09-01T09:00:00.000Z", "2026-09-08T09:00:00.000Z", "2026-09-15T09:00:00.000Z" },
            events.Select(e => Iso(e.When.DueAt)));
    }

    [Fact]
    public void honours_interval_and_byday()
    {
        // A fortnightly bin collection, the single most common real feed shape.
        var events = Parse(Feed(new[]
        {
            "UID:bins",
            "DTSTART:20260907T070000Z", // a Monday
            "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO;COUNT=3",
            "SUMMARY:Recycling",
        }));

        Assert.Equal(
            new[] { "2026-09-07T07:00:00.000Z", "2026-09-21T07:00:00.000Z", "2026-10-05T07:00:00.000Z" },
            events.Select(e => Iso(e.When.DueAt)));
    }

    [Fact]
    public void honours_a_monthly_ordinal_byday()
    {
        var events = Parse(Feed(new[]
        {
            "UID:club",
            "DTSTART:20260903T180000Z", // first Thursday of September 2026
            "RRULE:FREQ=MONTHLY;BYDAY=1TH;COUNT=3",
            "SUMMARY:Book club",
        }));

        Assert.Equal(
            new[] { "2026-09-03T18:00:00.000Z", "2026-10-01T18:00:00.000Z", "2026-11-05T18:00:00.000Z" },
            events.Select(e => Iso(e.When.DueAt)));
    }

    [Fact]
    public void skips_a_month_with_no_such_day_rather_than_clamping()
    {
        // RFC 5545 skips; clamping to the 28th would invent an occurrence the
        // publisher never scheduled.
        var events = Parse(Feed(new[]
        {
            "UID:rent",
            "DTSTART:20260131T090000Z",
            "RRULE:FREQ=MONTHLY;COUNT=3",
            "SUMMARY:Rent",
        }));

        Assert.Equal(
            new[] { "2026-01-31T09:00:00.000Z", "2026-03-31T09:00:00.000Z", "2026-05-31T09:00:00.000Z" },
            events.Select(e => Iso(e.When.DueAt)));
    }

    // ---- RECURRENCE-ID overrides ------------------------------------------

    [Fact]
    public void replaces_the_overridden_slot_rather_than_emitting_both()
    {
        // A moved instance arrives as a second VEVENT with the same UID. Expanding the
        // master blindly would yield the original slot AND the override — two matters
        // for one event.
        var events = Parse(Feed(
            new[] { "UID:weekly", "DTSTART:20260901T090000Z", "RRULE:FREQ=WEEKLY;COUNT=3", "SUMMARY:Swimming" },
            new[]
            {
                "UID:weekly",
                "RECURRENCE-ID:20260908T090000Z",
                "DTSTART:20260908T140000Z",
                "SUMMARY:Swimming (moved)",
            }));

        Assert.Equal(3, events.Count);
        Assert.Equal(
            new[]
            {
                "2026-09-01T09:00:00.000Z",
                "2026-09-08T14:00:00.000Z", // moved, not 09:00
                "2026-09-15T09:00:00.000Z",
            },
            events.Select(e => Iso(e.When.DueAt)).OrderBy(s => s));
    }

    [Fact]
    public void keys_the_override_to_the_slot_it_replaces_so_siblings_cannot_collide()
    {
        // Two overrides on one series would otherwise both fall back to the bare uid
        // and collide on the (userId, externalSource, externalId) unique index.
        var events = Parse(Feed(
            new[] { "UID:weekly", "DTSTART:20260901T090000Z", "RRULE:FREQ=WEEKLY;COUNT=3", "SUMMARY:Swimming" },
            new[] { "UID:weekly", "RECURRENCE-ID:20260908T090000Z", "DTSTART:20260908T140000Z", "SUMMARY:A" },
            new[] { "UID:weekly", "RECURRENCE-ID:20260915T090000Z", "DTSTART:20260915T160000Z", "SUMMARY:B" }));

        var ids = events.Select(e => e.ExternalId).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("weekly::20260908T0900", ids);
    }

    // ---- resilience --------------------------------------------------------

    [Fact]
    public void skips_a_vevent_with_no_uid_rather_than_dropping_the_feed()
    {
        var events = Parse(Feed(
            new[] { "DTSTART:20260903T090000Z", "SUMMARY:No uid" },
            new[] { "UID:good", "DTSTART:20260904T090000Z", "SUMMARY:Fine" }));

        Assert.Equal("good", Assert.Single(events).Uid);
    }

    [Fact]
    public void skips_a_vevent_with_no_dtstart()
    {
        var events = Parse(Feed(
            new[] { "UID:nodate", "SUMMARY:Undated" },
            new[] { "UID:good", "DTSTART:20260904T090000Z", "SUMMARY:Fine" }));

        Assert.Single(events);
    }

    [Fact]
    public void skips_a_vevent_whose_dtstart_is_unreadable()
    {
        var events = Parse(Feed(
            new[] { "UID:bad", "DTSTART:not-a-timestamp", "SUMMARY:Broken" },
            new[] { "UID:good", "DTSTART:20260904T090000Z", "SUMMARY:Fine" }));

        Assert.Equal("good", Assert.Single(events).Uid);
    }

    [Fact]
    public void falls_back_to_a_placeholder_title_rather_than_an_empty_one()
    {
        var events = Parse(Feed(new[] { "UID:x", "DTSTART:20260903T090000Z" }));

        Assert.Equal("(untitled)", events[0].Summary);
    }

    [Fact]
    public void flags_a_floating_recurring_series_for_confirmation_on_every_occurrence()
    {
        var events = Parse(Feed(new[]
        {
            "UID:f",
            "DTSTART:20260901T090000",
            "RRULE:FREQ=WEEKLY;COUNT=2",
            "SUMMARY:Club",
        }));

        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.True(e.When.NeedsConfirmation);
            Assert.Equal("low", e.When.Confidence);
        });
    }

    [Fact]
    public void throws_on_a_structurally_broken_body()
    {
        // The caller turns this into status:'error' / "That feed could not be read."
        Assert.ThrowsAny<Exception>(() => Parse("this is not a calendar at all"));
    }

    [Fact]
    public void throws_when_the_timezone_is_missing_for_an_all_day_event()
    {
        // An import runs with no device present, so guessing UTC for a user in Cairo
        // would move every reminder two hours and nothing in the UI would reveal it.
        // The per-event catch means the FEED survives with zero occurrences; the route
        // is what refuses up front with timezone_required.
        var events = Parse(
            Feed(new[] { "UID:allday", "DTSTART;VALUE=DATE:20260903", "SUMMARY:Term starts" }),
            timezone: "Not/AZone");

        Assert.Empty(events);
        Assert.Throws<TimezoneRequiredException>(
            () => ImportedTimeResolver.ResolveDateOnly("2026-09-03", "Not/AZone"));
    }

    // ---- reader-level behaviour -------------------------------------------

    [Fact]
    public void unfolds_a_wrapped_line()
    {
        // Publishers wrap at 75 octets. A reader that ignores folding truncates every
        // long summary in the feed.
        var body = string.Join("\r\n", new[]
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "BEGIN:VEVENT",
            "UID:folded",
            "DTSTART:20260903T090000Z",
            "SUMMARY:Parents evening for the",
            "  upper school",
            "END:VEVENT",
            "END:VCALENDAR",
        });

        var events = IcsEventParser.Parse(body, Context(UserTz, WideFrom, WideTo));

        Assert.Equal("Parents evening for the upper school", events[0].Summary);
    }

    [Fact]
    public void unescapes_text_values()
    {
        var events = Parse(Feed(new[]
        {
            "UID:esc",
            "DTSTART:20260903T090000Z",
            @"SUMMARY:Dentist\, then school",
            @"LOCATION:12 High St\; Flat 2",
        }));

        Assert.Equal("Dentist, then school", events[0].Summary);
        Assert.Equal("12 High St; Flat 2", events[0].Location);
    }

    [Fact]
    public void does_not_split_a_quoted_parameter_on_its_colon()
    {
        // A naive IndexOf(':') would cut inside the quoted TZID and lose the value.
        var property = IcsTextParser.Parse(string.Join("\r\n", new[]
        {
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "UID:q",
            "DTSTART;TZID=\"Weird:Zone\":20260903T090000",
            "END:VEVENT",
            "END:VCALENDAR",
        })).Subcomponents("vevent").First().FirstProperty("dtstart")!;

        Assert.Equal("Weird:Zone", property.Parameter("tzid"));
        Assert.Equal("20260903T090000", property.Value);
    }

    // ---- helpers -----------------------------------------------------------

    private static IReadOnlyList<IcsOccurrence> Parse(
        string body,
        string timezone = UserTz,
        DateTime? from = null,
        DateTime? to = null) =>
        IcsEventParser.Parse(body, Context(timezone, from ?? WideFrom, to ?? WideTo));

    private static ParseIcsContext Context(string timezone, DateTime from, DateTime to) =>
        new(new IcsTimeContext(timezone, "09:00"), from, to);

    private static string Feed(params string[][] veventLines)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN" };

        foreach (var vevent in veventLines)
        {
            lines.Add("BEGIN:VEVENT");
            lines.AddRange(vevent);
            lines.Add("END:VEVENT");
        }

        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines);
    }

    /// <summary>The 3-digit JS ISO form, so expectations read like the reference's.</summary>
    private static string Iso(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
