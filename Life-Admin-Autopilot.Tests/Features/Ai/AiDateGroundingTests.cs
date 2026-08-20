using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.DAL.Kernel.Time;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <see cref="DateGrounding"/> against <b>Node's own output</b>.
///
/// <para>
/// Every expected string below was produced by running
/// <c>server/src/modules/ai/contextBuilder.ts</c>'s <c>formatNow()</c> and
/// <c>buildDateReference()</c> under a frozen clock, one call per (instant, zone)
/// pair, and pasting the result verbatim. <b>None of it was computed by hand.</b>
/// Checking a date table against my own arithmetic would assert only that I made the
/// same mistake twice — and an off-by-one weekday is precisely the failure the table
/// exists to prevent.
/// </para>
///
/// <para>
/// The grid is chosen for the cases that break naive ports: a local day that has
/// rolled over while UTC has not (and the reverse), a half-hour and a +14:00 offset,
/// a window that walks off the end of February in both a common and a leap year, the
/// two DST transitions, and the weekend anchor evaluated on a Saturday and on a
/// Sunday.
/// </para>
/// </summary>
public sealed class AiDateGroundingTests
{
    public static IEnumerable<object[]> NodeReference()
    {
        // a plain Tuesday, +03:00
        yield return new object[]
        {
            "2026-08-11T11:23:45.000Z",
            "Africa/Cairo",
            "2026-08-11T14:23:45+03:00 (Tuesday)",
            """
            === DATE REFERENCE (local) ===
            Tuesday 2026-08-11 (today)
            Wednesday 2026-08-12 (tomorrow)
            Thursday 2026-08-13 (in 2 days)
            Friday 2026-08-14 (in 3 days)
            Saturday 2026-08-15 (in 4 days)
            Sunday 2026-08-16 (in 5 days)
            Monday 2026-08-17 (in 6 days)
            Tuesday 2026-08-18 (in 7 days)
            Wednesday 2026-08-19 (in 8 days)
            Thursday 2026-08-20 (in 9 days)
            Friday 2026-08-21 (in 10 days)
            Saturday 2026-08-22 (in 11 days)
            Sunday 2026-08-23 (in 12 days)
            Monday 2026-08-24 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-08-15 & Sun 2026-08-16
            end of this month = 2026-08-31
            """,
        };

        // the local day has already rolled over; UTC has not
        yield return new object[]
        {
            "2026-08-11T21:40:00.000Z",
            "Africa/Cairo",
            "2026-08-12T00:40:00+03:00 (Wednesday)",
            """
            === DATE REFERENCE (local) ===
            Wednesday 2026-08-12 (today)
            Thursday 2026-08-13 (tomorrow)
            Friday 2026-08-14 (in 2 days)
            Saturday 2026-08-15 (in 3 days)
            Sunday 2026-08-16 (in 4 days)
            Monday 2026-08-17 (in 5 days)
            Tuesday 2026-08-18 (in 6 days)
            Wednesday 2026-08-19 (in 7 days)
            Thursday 2026-08-20 (in 8 days)
            Friday 2026-08-21 (in 9 days)
            Saturday 2026-08-22 (in 10 days)
            Sunday 2026-08-23 (in 11 days)
            Monday 2026-08-24 (in 12 days)
            Tuesday 2026-08-25 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-08-15 & Sun 2026-08-16
            end of this month = 2026-08-31
            """,
        };

        // a half-hour offset
        yield return new object[]
        {
            "2026-08-11T11:23:45.000Z",
            "Asia/Kolkata",
            "2026-08-11T16:53:45+05:30 (Tuesday)",
            """
            === DATE REFERENCE (local) ===
            Tuesday 2026-08-11 (today)
            Wednesday 2026-08-12 (tomorrow)
            Thursday 2026-08-13 (in 2 days)
            Friday 2026-08-14 (in 3 days)
            Saturday 2026-08-15 (in 4 days)
            Sunday 2026-08-16 (in 5 days)
            Monday 2026-08-17 (in 6 days)
            Tuesday 2026-08-18 (in 7 days)
            Wednesday 2026-08-19 (in 8 days)
            Thursday 2026-08-20 (in 9 days)
            Friday 2026-08-21 (in 10 days)
            Saturday 2026-08-22 (in 11 days)
            Sunday 2026-08-23 (in 12 days)
            Monday 2026-08-24 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-08-15 & Sun 2026-08-16
            end of this month = 2026-08-31
            """,
        };

        // +14:00: the local year has turned, UTC has not
        yield return new object[]
        {
            "2026-12-31T22:10:00.000Z",
            "Pacific/Kiritimati",
            "2027-01-01T12:10:00+14:00 (Friday)",
            """
            === DATE REFERENCE (local) ===
            Friday 2027-01-01 (today)
            Saturday 2027-01-02 (tomorrow)
            Sunday 2027-01-03 (in 2 days)
            Monday 2027-01-04 (in 3 days)
            Tuesday 2027-01-05 (in 4 days)
            Wednesday 2027-01-06 (in 5 days)
            Thursday 2027-01-07 (in 6 days)
            Friday 2027-01-08 (in 7 days)
            Saturday 2027-01-09 (in 8 days)
            Sunday 2027-01-10 (in 9 days)
            Monday 2027-01-11 (in 10 days)
            Tuesday 2027-01-12 (in 11 days)
            Wednesday 2027-01-13 (in 12 days)
            Thursday 2027-01-14 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2027-01-02 & Sun 2027-01-03
            end of this month = 2027-01-31
            """,
        };

        // -09:30: still the old year, so end of month is 31 Dec
        yield return new object[]
        {
            "2026-12-31T22:10:00.000Z",
            "Pacific/Marquesas",
            "2026-12-31T12:40:00-09:30 (Thursday)",
            """
            === DATE REFERENCE (local) ===
            Thursday 2026-12-31 (today)
            Friday 2027-01-01 (tomorrow)
            Saturday 2027-01-02 (in 2 days)
            Sunday 2027-01-03 (in 3 days)
            Monday 2027-01-04 (in 4 days)
            Tuesday 2027-01-05 (in 5 days)
            Wednesday 2027-01-06 (in 6 days)
            Thursday 2027-01-07 (in 7 days)
            Friday 2027-01-08 (in 8 days)
            Saturday 2027-01-09 (in 9 days)
            Sunday 2027-01-10 (in 10 days)
            Monday 2027-01-11 (in 11 days)
            Tuesday 2027-01-12 (in 12 days)
            Wednesday 2027-01-13 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2027-01-02 & Sun 2027-01-03
            end of this month = 2026-12-31
            """,
        };

        // the 14-day window walks off the end of February
        yield return new object[]
        {
            "2026-02-25T06:00:00.000Z",
            "UTC",
            "2026-02-25T06:00:00+00:00 (Wednesday)",
            """
            === DATE REFERENCE (local) ===
            Wednesday 2026-02-25 (today)
            Thursday 2026-02-26 (tomorrow)
            Friday 2026-02-27 (in 2 days)
            Saturday 2026-02-28 (in 3 days)
            Sunday 2026-03-01 (in 4 days)
            Monday 2026-03-02 (in 5 days)
            Tuesday 2026-03-03 (in 6 days)
            Wednesday 2026-03-04 (in 7 days)
            Thursday 2026-03-05 (in 8 days)
            Friday 2026-03-06 (in 9 days)
            Saturday 2026-03-07 (in 10 days)
            Sunday 2026-03-08 (in 11 days)
            Monday 2026-03-09 (in 12 days)
            Tuesday 2026-03-10 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-02-28 & Sun 2026-03-01
            end of this month = 2026-02-28
            """,
        };

        // a leap February — the 29th is a real row
        yield return new object[]
        {
            "2028-02-20T06:00:00.000Z",
            "UTC",
            "2028-02-20T06:00:00+00:00 (Sunday)",
            """
            === DATE REFERENCE (local) ===
            Sunday 2028-02-20 (today)
            Monday 2028-02-21 (tomorrow)
            Tuesday 2028-02-22 (in 2 days)
            Wednesday 2028-02-23 (in 3 days)
            Thursday 2028-02-24 (in 4 days)
            Friday 2028-02-25 (in 5 days)
            Saturday 2028-02-26 (in 6 days)
            Sunday 2028-02-27 (in 7 days)
            Monday 2028-02-28 (in 8 days)
            Tuesday 2028-02-29 (in 9 days)
            Wednesday 2028-03-01 (in 10 days)
            Thursday 2028-03-02 (in 11 days)
            Friday 2028-03-03 (in 12 days)
            Saturday 2028-03-04 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2028-02-26 & Sun 2028-02-27
            end of this month = 2028-02-29
            """,
        };

        // today IS Saturday, so this weekend starts today
        yield return new object[]
        {
            "2026-08-15T09:00:00.000Z",
            "Africa/Cairo",
            "2026-08-15T12:00:00+03:00 (Saturday)",
            """
            === DATE REFERENCE (local) ===
            Saturday 2026-08-15 (today)
            Sunday 2026-08-16 (tomorrow)
            Monday 2026-08-17 (in 2 days)
            Tuesday 2026-08-18 (in 3 days)
            Wednesday 2026-08-19 (in 4 days)
            Thursday 2026-08-20 (in 5 days)
            Friday 2026-08-21 (in 6 days)
            Saturday 2026-08-22 (in 7 days)
            Sunday 2026-08-23 (in 8 days)
            Monday 2026-08-24 (in 9 days)
            Tuesday 2026-08-25 (in 10 days)
            Wednesday 2026-08-26 (in 11 days)
            Thursday 2026-08-27 (in 12 days)
            Friday 2026-08-28 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-08-15 & Sun 2026-08-16
            end of this month = 2026-08-31
            """,
        };

        // today is Sunday: the scan runs FORWARD, so this weekend is six days out
        yield return new object[]
        {
            "2026-08-16T09:00:00.000Z",
            "Africa/Cairo",
            "2026-08-16T12:00:00+03:00 (Sunday)",
            """
            === DATE REFERENCE (local) ===
            Sunday 2026-08-16 (today)
            Monday 2026-08-17 (tomorrow)
            Tuesday 2026-08-18 (in 2 days)
            Wednesday 2026-08-19 (in 3 days)
            Thursday 2026-08-20 (in 4 days)
            Friday 2026-08-21 (in 5 days)
            Saturday 2026-08-22 (in 6 days)
            Sunday 2026-08-23 (in 7 days)
            Monday 2026-08-24 (in 8 days)
            Tuesday 2026-08-25 (in 9 days)
            Wednesday 2026-08-26 (in 10 days)
            Thursday 2026-08-27 (in 11 days)
            Friday 2026-08-28 (in 12 days)
            Saturday 2026-08-29 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-08-22 & Sun 2026-08-23
            end of this month = 2026-08-31
            """,
        };

        // US spring-forward day
        yield return new object[]
        {
            "2026-03-08T10:30:00.000Z",
            "America/Los_Angeles",
            "2026-03-08T03:30:00-07:00 (Sunday)",
            """
            === DATE REFERENCE (local) ===
            Sunday 2026-03-08 (today)
            Monday 2026-03-09 (tomorrow)
            Tuesday 2026-03-10 (in 2 days)
            Wednesday 2026-03-11 (in 3 days)
            Thursday 2026-03-12 (in 4 days)
            Friday 2026-03-13 (in 5 days)
            Saturday 2026-03-14 (in 6 days)
            Sunday 2026-03-15 (in 7 days)
            Monday 2026-03-16 (in 8 days)
            Tuesday 2026-03-17 (in 9 days)
            Wednesday 2026-03-18 (in 10 days)
            Thursday 2026-03-19 (in 11 days)
            Friday 2026-03-20 (in 12 days)
            Saturday 2026-03-21 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-03-14 & Sun 2026-03-15
            end of this month = 2026-03-31
            """,
        };

        // EU fall-back day
        yield return new object[]
        {
            "2026-10-25T00:30:00.000Z",
            "Europe/Berlin",
            "2026-10-25T02:30:00+02:00 (Sunday)",
            """
            === DATE REFERENCE (local) ===
            Sunday 2026-10-25 (today)
            Monday 2026-10-26 (tomorrow)
            Tuesday 2026-10-27 (in 2 days)
            Wednesday 2026-10-28 (in 3 days)
            Thursday 2026-10-29 (in 4 days)
            Friday 2026-10-30 (in 5 days)
            Saturday 2026-10-31 (in 6 days)
            Sunday 2026-11-01 (in 7 days)
            Monday 2026-11-02 (in 8 days)
            Tuesday 2026-11-03 (in 9 days)
            Wednesday 2026-11-04 (in 10 days)
            Thursday 2026-11-05 (in 11 days)
            Friday 2026-11-06 (in 12 days)
            Saturday 2026-11-07 (in 13 days)
            --- phrase anchors (resolve these literally) ---
            this weekend = Sat 2026-10-31 & Sun 2026-11-01
            end of this month = 2026-10-31
            """,
        };
    }

    [Theory]
    [MemberData(nameof(NodeReference))]
    public void matches_node_formatNow(string instant, string timezone, string expected, string _)
    {
        Assert.Equal(expected, DateGrounding.FormatNow(Instant(instant), timezone));
    }

    /// <summary>
    /// Every line Node emits is still emitted, byte-for-byte and in order: the header,
    /// all 14 day rows, the anchor header, "this weekend" and "end of this month".
    ///
    /// <para>
    /// <b>Why this is a superset assertion rather than an equality one.</b> The block now
    /// carries seven additional weekday anchors Node never had — a DELIBERATE divergence
    /// recorded in <c>docs/DIVERGENCES.md</c>, because the 14-day table lists each
    /// weekday twice, labels neither "next", and left the model resolving
    /// "next &lt;weekday&gt;" by the arithmetic the prompt forbids. Measured
    /// non-deterministic across two runs of one prompt. Equality here would pin the
    /// defect; this pins everything about Node's output that was RIGHT.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(NodeReference))]
    public void matches_node_buildDateReference(string instant, string timezone, string _, string expected)
    {
        var actual = DateGrounding
            .BuildDateReference(Instant(instant), timezone)
            .Split('\n');

        var nodeLines = expected.Split('\n');

        // The table and its header are still the first 15 lines, unchanged.
        Assert.Equal(
            nodeLines.Take(1 + DateGrounding.ReferenceDays),
            actual.Take(1 + DateGrounding.ReferenceDays));

        // The anchor header still introduces the anchors, and Node's two are still the
        // last two lines — the weekday anchors are inserted between, not appended after.
        Assert.Equal(DateGrounding.AnchorHeader, actual[1 + DateGrounding.ReferenceDays]);
        Assert.Equal(nodeLines[^2], actual[^2]);
        Assert.Equal(nodeLines[^1], actual[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Factory")]
    public void an_unusable_timezone_is_the_product_default_not_the_servers_own(string? timezone)
    {
        // A DELIBERATE divergence, recorded in docs/DIVERGENCES.md. Node's
        // no-timezone branch prints a UTC instant but takes the weekday and the whole
        // 14-day table from the SERVER's local zone, so the same request answers
        // differently on a Cairo laptop and in a UTC container — and labels a UTC
        // timestamp with Cairo's weekday.
        //
        // This used to resolve to UTC, which fixed the inconsistency but kept the
        // wrong clock: the grounding block IS what the agent reads "now" from, so a
        // UTC fallback is what put every derived dueAt three hours early for this
        // product's users. It now resolves to the product default.
        var instant = Instant("2026-12-31T22:10:00.000Z");

        Assert.Equal(
            DateGrounding.FormatNow(instant, AppTimeZone.DefaultId),
            DateGrounding.FormatNow(instant, timezone));

        Assert.Equal(
            DateGrounding.BuildDateReference(instant, AppTimeZone.DefaultId),
            DateGrounding.BuildDateReference(instant, timezone));

        // Still offset-bearing, and no longer +00:00. A bare date is the v3 format
        // that made the agent invent +00:00 and put every dueAt out by the user's
        // whole offset; so was a fallback that PRINTED +00:00 to an Egyptian account.
        var expectedOffset = AppTimeZone.Default
            .GetUtcOffset(instant)
            .ToString(@"'+'hh\:mm");

        Assert.Contains(expectedOffset + " (", DateGrounding.FormatNow(instant, timezone));
    }

    [Fact]
    public void the_table_is_fourteen_rows_plus_seven_weekdays_and_two_anchors()
    {
        var lines = DateGrounding
            .BuildDateReference(Instant("2026-08-11T11:23:45.000Z"), "Africa/Cairo")
            .Split('\n');

        // header + 14 days + anchor header + 7 weekday anchors + 2 phrase anchors. The
        // count is asserted on its own because a truncated table degrades silently: the
        // model simply guesses the dates that fell off the end, which is the behaviour
        // without any table.
        Assert.Equal(1 + DateGrounding.ReferenceDays + 1 + 7 + 2, lines.Length);
        Assert.Equal(DateGrounding.ReferenceHeader, lines[0]);
        Assert.Equal(DateGrounding.AnchorHeader, lines[^10]);
    }

    /// <summary>
    /// 2026-08-11 is a Tuesday, so "next Wednesday" is TOMORROW and "next Monday" is six
    /// days out. Both are spelled out rather than left to the 14-day table, which lists
    /// each weekday twice and labels neither.
    ///
    /// <para>
    /// The bug: two runs of the identical Arabic prompt "ذكرني يوم الاثنين بموعد الدكتور"
    /// on Monday 2026-08-17 resolved to 2026-08-24 in one and 2026-08-17 in the other.
    /// Same input, same day, same table, different answer — the signature of a phrase the
    /// grounding never defined.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Wednesday", "2026-08-12")]
    [InlineData("Thursday", "2026-08-13")]
    [InlineData("Friday", "2026-08-14")]
    [InlineData("Saturday", "2026-08-15")]
    [InlineData("Sunday", "2026-08-16")]
    [InlineData("Monday", "2026-08-17")]
    [InlineData("Tuesday", "2026-08-11")]
    public void anchors_every_weekday_to_its_soonest_upcoming_date(string weekday, string expected)
    {
        var reference = DateGrounding.BuildDateReference(
            Instant("2026-08-11T11:23:45.000Z"), "Africa/Cairo");

        Assert.Contains($"next {weekday} = {expected} ", reference);
        Assert.Contains($"this {weekday} = {expected};", reference);
    }

    /// <summary>
    /// "next Tuesday" said ON a Tuesday resolves to that same Tuesday, not to the one
    /// after. The soonest upcoming occurrence of today's weekday IS today, and the
    /// prompt's separate rule forbids only a PAST instant.
    /// </summary>
    [Fact]
    public void the_current_weekday_anchors_to_today_not_next_week()
    {
        var reference = DateGrounding.BuildDateReference(
            Instant("2026-08-11T11:23:45.000Z"), "Africa/Cairo");

        Assert.Contains("next Tuesday = 2026-08-11 ", reference);
        Assert.DoesNotContain("next Tuesday = 2026-08-18", reference);
    }

    /// <summary>
    /// The offset the tools compute a local day with is the SAME one the clock prints.
    /// Two sources would drift, and the drift would land on <c>due_on</c> — the filter
    /// behind "what do I have on Friday".
    /// </summary>
    [Theory]
    [InlineData("Africa/Cairo", "+03:00")]
    [InlineData("Asia/Kolkata", "+05:30")]
    [InlineData("UTC", "+00:00")]

    // An unusable and an absent zone both resolve to the product default, which at
    // this instant — 2026-08-11, inside Egyptian DST — is +03:00. Both rows read
    // "+00:00" before the default existed.
    [InlineData("Not/AZone", "+03:00")]
    [InlineData(null, "+03:00")]
    public void the_utc_offset_matches_the_one_on_current_date(string? timezone, string expected)
    {
        var instant = Instant("2026-08-11T11:23:45.000Z");

        Assert.Equal(expected, DateGrounding.UtcOffset(instant, timezone));
        Assert.Contains(expected + " (", DateGrounding.FormatNow(instant, timezone));
    }

    private static DateTimeOffset Instant(string iso) =>
        DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
}
