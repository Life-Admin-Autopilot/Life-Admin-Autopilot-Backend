using Life_Admin_Autopilot.BLL.Features.Digest;
using Life_Admin_Autopilot.DAL.Features.Digest;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Digest;

/// <summary>
/// The digest's pure halves. None of these touch a database, so they are the
/// suite's real floor for this slice — they run everywhere, always.
/// </summary>
public sealed class NeutralHeadlineTests
{
    private static DailyDigestCountsDocument Counts(
        int dueToday = 0, int completedToday = 0, int openTotal = 0, int needsInput = 0) => new()
    {
        DueToday = dueToday,
        CompletedToday = completedToday,
        OpenTotal = openTotal,
        NeedsInput = needsInput,
    };

    [Fact]
    public void leads_with_what_is_due_today()
    {
        Assert.Equal("1 matter due today.", NeutralHeadline.For(Counts(dueToday: 1)));
        Assert.Equal("2 matters due today.", NeutralHeadline.For(Counts(dueToday: 2)));
    }

    [Fact]
    public void falls_back_to_what_was_closed()
    {
        Assert.Equal(
            "Nothing due today. 1 matter closed.",
            NeutralHeadline.For(Counts(completedToday: 1)));
        Assert.Equal(
            "Nothing due today. 3 matters closed.",
            NeutralHeadline.For(Counts(completedToday: 3)));
    }

    /// <summary>The one branch whose plural swaps a verb, not just a noun.</summary>
    [Fact]
    public void then_to_the_questions_waiting()
    {
        Assert.Equal(
            "Nothing due today. 1 question is waiting on you.",
            NeutralHeadline.For(Counts(needsInput: 1)));
        Assert.Equal(
            "Nothing due today. 2 questions are waiting on you.",
            NeutralHeadline.For(Counts(needsInput: 2)));
    }

    [Fact]
    public void distinguishes_an_empty_account_from_a_clear_day()
    {
        Assert.Equal("Nothing on today.", NeutralHeadline.For(Counts()));
        Assert.Equal("Nothing due today.", NeutralHeadline.For(Counts(openTotal: 4)));
    }

    /// <summary>
    /// First match wins, and the order is load-bearing: a day with closures AND
    /// open questions leads with the closures.
    /// </summary>
    [Fact]
    public void closures_outrank_open_questions()
    {
        Assert.Equal(
            "Nothing due today. 1 matter closed.",
            NeutralHeadline.For(Counts(completedToday: 1, needsInput: 5)));
    }

    /// <summary>
    /// It reports the day; it does not grade the person. Nothing in the ladder
    /// counts what the user failed to do.
    /// </summary>
    [Fact]
    public void never_mentions_what_was_missed()
    {
        var overdue = Counts(openTotal: 40, needsInput: 0);
        Assert.DoesNotContain("overdue", NeutralHeadline.For(overdue), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("late", NeutralHeadline.For(overdue), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DigestDuplicatesTests
{
    private static DuplicateCandidate C(string id, string title) => new(id, title);

    [Fact]
    public void bins_titles_on_trim_lowercase_and_collapsed_whitespace()
    {
        var found = DigestDuplicates.Find(new[]
        {
            C("a", "Call the vet"),
            C("b", "  call   the VET  "),
            C("c", "Renew passport"),
        });

        var only = Assert.Single(found);

        // The FIRST member's ORIGINAL title, not the normalised bin key.
        Assert.Equal("Call the vet", only.Title);
        Assert.Equal(2, only.Count);
        Assert.Equal(new[] { "a", "b" }, only.TaskIds);
    }

    [Fact]
    public void ignores_titles_that_appear_once()
    {
        Assert.Empty(DigestDuplicates.Find(new[] { C("a", "One"), C("b", "Two") }));
    }

    /// <summary>
    /// V8's <c>Array#sort</c> is stable, so equal-sized bins keep insertion order
    /// and successive rebuilds agree. <c>List.Sort</c> is an unstable introsort and
    /// would shuffle them — this is the case that catches a regression to it.
    /// </summary>
    [Fact]
    public void keeps_insertion_order_between_equally_sized_bins()
    {
        var found = DigestDuplicates.Find(new[]
        {
            C("a1", "Alpha"), C("a2", "Alpha"),
            C("b1", "Bravo"), C("b2", "Bravo"),
            C("c1", "Delta"), C("c2", "Delta"),
        });

        Assert.Equal(new[] { "Alpha", "Bravo", "Delta" }, found.Select(d => d.Title));
    }

    [Fact]
    public void sorts_bigger_bins_first_and_keeps_at_most_five()
    {
        var rows = new List<DuplicateCandidate>();
        for (var bin = 0; bin < 7; bin++)
        {
            for (var i = 0; i <= bin; i++)
            {
                rows.Add(C($"{bin}-{i}", $"Title {bin}"));
            }
        }

        var found = DigestDuplicates.Find(rows);

        Assert.Equal(5, found.Count);
        Assert.Equal(new[] { 7, 6, 5, 4, 3 }, found.Select(d => d.Count));
    }

    /// <summary>
    /// The final guard tests the REPORTED title — <c>group[0].title</c>, raw and
    /// untrimmed — not the normalised bin key. So a bin of blank titles is dropped
    /// only when its FIRST member's title is literally empty. Verified against Node:
    /// <c>[' ', '']</c> comes back as a duplicate titled <c>'   '</c>, and
    /// <c>['', '   ']</c> is dropped. Reproduced rather than tidied, because the two
    /// orderings really do behave differently.
    /// </summary>
    [Fact]
    public void drops_a_blank_bin_only_when_its_first_member_is_literally_empty()
    {
        var kept = DigestDuplicates.Find(new[] { C("a", "   "), C("b", "") });
        Assert.Equal("   ", Assert.Single(kept).Title);

        Assert.Empty(DigestDuplicates.Find(new[] { C("a", ""), C("b", "   ") }));
    }

    /// <summary>
    /// ECMAScript counts U+FEFF as whitespace and .NET does not. Two titles
    /// differing only by a pasted BOM are the same matter on Node, and must be here.
    /// </summary>
    [Fact]
    public void treats_a_stray_byte_order_mark_as_whitespace_the_way_javascript_does()
    {
        var found = DigestDuplicates.Find(new[]
        {
            C("a", "Pay rent"),
            C("b", "\uFEFFPay\uFEFFrent\uFEFF"),
        });

        Assert.Equal(2, Assert.Single(found).Count);
    }
}

public sealed class DigestThemesTests
{
    private static DailyDigestThemeDocument T(string label, params string[] ids) => new()
    {
        Label = label,
        Count = ids.Length,
        TaskIds = ids.ToList(),
    };

    private static IReadOnlySet<string> Pool(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void drops_ids_that_are_no_longer_in_the_pool()
    {
        var kept = DigestThemes.KeepReal(new[] { T("Pets", "a", "b", "gone") }, Pool("a", "b"));

        var only = Assert.Single(kept);
        Assert.Equal(new[] { "a", "b" }, only.TaskIds);

        // The count is DERIVED from what survived, never the stored one.
        Assert.Equal(2, only.Count);
    }

    [Fact]
    public void drops_a_theme_left_with_nothing()
    {
        Assert.Empty(DigestThemes.KeepReal(new[] { T("Ghosts", "gone") }, Pool("a")));
    }

    [Fact]
    public void gives_a_contested_id_to_the_first_theme_that_claims_it()
    {
        var kept = DigestThemes.KeepReal(new[] { T("First", "a"), T("Second", "a") }, Pool("a"));

        Assert.Equal("First", Assert.Single(kept).Label);
    }

    [Fact]
    public void drops_a_blank_label()
    {
        Assert.Empty(DigestThemes.KeepReal(new[] { T("   ", "a") }, Pool("a")));
    }

    /// <summary>
    /// A real ordering quirk of the Node original, verified live against the
    /// reference server: ids are added to the seen-set BEFORE the empty-label filter
    /// runs, so a theme discarded for a blank label still BURNS the ids it claimed.
    /// A later theme naming the same id then comes out empty and is dropped too.
    /// Ported as-is — "fixing" it would change which themes the dashboard shows.
    /// </summary>
    [Fact]
    public void a_blank_labelled_theme_still_burns_the_ids_it_claimed()
    {
        var kept = DigestThemes.KeepReal(new[] { T("  ", "a"), T("Travel", "a") }, Pool("a"));

        Assert.Empty(kept);
    }

    /// <summary>
    /// The seen-set is consulted as it stood BEFORE this theme, so an id repeated
    /// inside ONE theme's own list survives twice.
    /// </summary>
    [Fact]
    public void does_not_dedupe_within_a_single_theme()
    {
        var kept = DigestThemes.KeepReal(new[] { T("Pets", "a", "a") }, Pool("a"));

        Assert.Equal(new[] { "a", "a" }, Assert.Single(kept).TaskIds);
    }
}

public sealed class DigestEstimateTests
{
    private static BsonDocument Row(BsonValue? estimate) =>
        estimate is null ? new BsonDocument() : new BsonDocument("estimate", estimate);

    [Fact]
    public void sums_a_well_formed_estimate()
    {
        Assert.Equal((15d, 30d), DailyDigestComputer.ReadEstimate(
            Row(new BsonDocument { ["minMinutes"] = 15, ["maxMinutes"] = 30 })));
    }

    /// <summary>
    /// Every one of these contributes ZERO rather than a guess. A fabricated number
    /// in a digest whose whole premise is that it has none is worse than a low total.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unusable))]
    public void contributes_nothing_for_an_unusable_estimate(BsonValue? estimate)
    {
        Assert.Equal((0d, 0d), DailyDigestComputer.ReadEstimate(Row(estimate)));
    }

    public static TheoryData<BsonValue?> Unusable() => new()
    {
        null,
        BsonNull.Value,
        new BsonDocument(),
        new BsonDocument { ["minMinutes"] = 15 },
        new BsonDocument { ["maxMinutes"] = 30 },
        new BsonDocument { ["minMinutes"] = -5, ["maxMinutes"] = 30 },
        new BsonDocument { ["minMinutes"] = 15, ["maxMinutes"] = "thirty" },
        new BsonString("15-30"),
    };

    /// <summary>A max below min would render as a backwards range.</summary>
    [Fact]
    public void clamps_a_max_that_is_below_its_min()
    {
        Assert.Equal((30d, 30d), DailyDigestComputer.ReadEstimate(
            Row(new BsonDocument { ["minMinutes"] = 30, ["maxMinutes"] = 10 })));
    }
}

public sealed class DigestClockTests
{
    private static readonly DateTime Noon = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void renders_the_local_calendar_date_in_the_callers_zone()
    {
        Assert.Equal("2026-08-10", DigestClock.LocalDateKey(Noon, "UTC"));
        Assert.Equal("2026-08-10", DigestClock.LocalDateKey(Noon, "Africa/Cairo"));
    }

    /// <summary>
    /// The assertion that timezone handling is real. At 10:00 UTC the same instant
    /// falls on three different calendar dates: Kiritimati (UTC+14) is already on the
    /// 11th, UTC is on the 10th, and Midway (UTC-11) is still on the 9th. Cross-checked
    /// against <c>Intl.DateTimeFormat('en-CA')</c> on the reference runtime.
    /// </summary>
    [Fact]
    public void crosses_the_date_line_correctly()
    {
        var at = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal("2026-08-11", DigestClock.LocalDateKey(at, "Pacific/Kiritimati"));
        Assert.Equal("2026-08-10", DigestClock.LocalDateKey(at, "UTC"));
        Assert.Equal("2026-08-09", DigestClock.LocalDateKey(at, "Pacific/Midway"));
    }

    [Fact]
    public void accepts_a_real_zone_and_rejects_a_typo()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        Assert.Equal("Africa/Cairo", DigestClock.SafeTimezone("Africa/Cairo", logger));
        Assert.Null(DigestClock.SafeTimezone("Not/AZone", logger));
        Assert.Null(DigestClock.SafeTimezone("", logger));
        Assert.Null(DigestClock.SafeTimezone(null, logger));

        // NOT trimmed — Node passes the raw string to Intl, which rejects it, and the
        // request then falls back to UTC instead of quietly meaning Cairo.
        Assert.Null(DigestClock.SafeTimezone(" Africa/Cairo ", logger));
    }
}
