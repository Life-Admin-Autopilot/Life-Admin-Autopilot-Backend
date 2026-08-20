using System.Globalization;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Grounding;

/// <summary>
/// Port of the two date functions in
/// <c>server/src/modules/ai/contextBuilder.ts</c> — <c>formatNow()</c> and
/// <c>buildDateReference()</c>.
///
/// <para>
/// <b>Why a lookup table instead of trusting the model.</b> Node hands every
/// grounded turn an explicit weekday→date table for the next 14 days plus literal
/// anchors for the phrases people actually say. The comment above the anchor code in
/// the reference says why: <i>"Literal anchors for common relative phrases the model
/// otherwise guesses."</i> Given only "today is 2026-08-11", a model asked for "next
/// Tuesday" does weekday arithmetic in its head and lands a day out — and a reminder
/// one day out is indistinguishable from a reminder that works until it doesn't fire.
/// The table converts arithmetic into lookup.
/// </para>
///
/// <para>
/// <b>Every string here is asserted against Node's own output</b>, captured by
/// running the reference functions under a frozen clock across a grid of instants and
/// zones (see <c>AiDateGroundingTests</c>). Nothing in this file was derived from my
/// own calendar arithmetic, because that is exactly the error being defended against.
/// </para>
///
/// <para>
/// <b>The one deliberate difference: an absent or unusable timezone means UTC.</b>
/// Node's no-timezone branch prints the instant with <c>Date#toISOString()</c> (a
/// <c>Z</c> timestamp) but takes the weekday — and the whole 14-day table — from the
/// SERVER's local zone, so a laptop in Cairo and a container in UTC produce different
/// tables for the same request. That output is not reproducible and is internally
/// inconsistent (a UTC instant labelled with Cairo's weekday). Here an absent,
/// unknown or malformed zone is treated as <c>UTC</c>, which makes the fallback
/// self-consistent and byte-identical to Node's own <c>timezone: 'UTC'</c> output.
/// Recorded in <c>docs/DIVERGENCES.md</c>.
/// </para>
/// </summary>
public static class DateGrounding
{
    /// <summary>Rows in the table. Node's loop bound, not a tunable.</summary>
    public const int ReferenceDays = 14;

    public const string ReferenceHeader = "=== DATE REFERENCE (local) ===";

    public const string AnchorHeader = "--- phrase anchors (resolve these literally) ---";

    private const string IsoDate = "yyyy-MM-dd";

    /// <summary>
    /// <c>formatNow()</c> — the local wall clock with an explicit offset and the
    /// weekday: <c>2026-08-11T14:23:45+03:00 (Tuesday)</c>.
    ///
    /// <para>
    /// <b>The offset is load-bearing and its absence fails silently.</b> Measured on
    /// the live stack with <c>Africa/Cairo</c>: hand the agent a bare <c>yyyy-MM-dd</c>
    /// and it invents <c>+00:00</c> rather than complaining, putting every derived
    /// <c>dueAt</c> three hours early with no error anywhere.
    /// </para>
    /// </summary>
    public static string FormatNow(DateTimeOffset now, string? timezone)
    {
        var local = ToLocal(now, timezone);

        return string.Concat(
            local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            " (",
            Weekday(local.DateTime),
            ")");
    }

    /// <summary>
    /// The caller's offset from UTC at <paramref name="now"/>, as <c>+03:00</c> /
    /// <c>-05:00</c>.
    ///
    /// <para>
    /// The same offset <see cref="FormatNow"/> prints, from the same conversion, so the
    /// clock the agent reads and the offset its <c>due_on</c> filter computes with
    /// cannot drift apart. An absent or unusable zone is <c>+00:00</c>, matching this
    /// type's UTC fallback.
    /// </para>
    /// </summary>
    public static string UtcOffset(DateTimeOffset now, string? timezone) =>
        ToLocal(now, timezone).ToString("zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>buildDateReference()</c> — the 14-day weekday→date table plus the phrase
    /// anchors, anchored to the local calendar date in <paramref name="timezone"/>.
    ///
    /// <para>
    /// <b>Iterated at UTC noon, exactly as Node does.</b> The table is a list of
    /// calendar dates, not instants; walking it at noon keeps every step clear of a
    /// DST boundary, so a spring-forward day cannot silently produce two rows for the
    /// same date or skip one.
    /// </para>
    ///
    /// <para>
    /// <b>"this weekend" scans FORWARD for a Saturday, so on a Sunday it means next
    /// Saturday</b> — six days out, not the day before. That is the reference's
    /// behaviour (confirmed against Node with the clock frozen to a Sunday) and it is
    /// reproduced rather than corrected: the two servers must resolve the phrase to
    /// the same date, and the reference is the definition.
    /// </para>
    /// </summary>
    public static string BuildDateReference(DateTimeOffset now, string? timezone)
    {
        var today = ToLocal(now, timezone).Date;

        // Date.UTC(y, m - 1, d + i, 12) — day arithmetic that rolls over months and
        // years on its own. AddDays reproduces the overflow.
        var anchor = new DateTime(today.Year, today.Month, today.Day, 12, 0, 0, DateTimeKind.Utc);

        var lines = new List<string>(ReferenceDays + 4) { ReferenceHeader };

        for (var i = 0; i < ReferenceDays; i++)
        {
            var day = anchor.AddDays(i);
            var tag = i switch
            {
                0 => "(today)",
                1 => "(tomorrow)",
                _ => $"(in {i} days)",
            };

            lines.Add($"{Weekday(day)} {day.ToString(IsoDate, CultureInfo.InvariantCulture)} {tag}");
        }

        var saturday = anchor;
        for (var i = 0; i < 7; i++)
        {
            var candidate = anchor.AddDays(i);
            if (candidate.DayOfWeek == DayOfWeek.Saturday)
            {
                saturday = candidate;
                break;
            }
        }

        var sunday = saturday.AddDays(1);

        // Date.UTC(y, m, 0, 12): day 0 of next month is the last day of this one.
        var endOfMonth = new DateTime(today.Year, today.Month, 1, 12, 0, 0, DateTimeKind.Utc)
            .AddMonths(1)
            .AddDays(-1);

        lines.Add(AnchorHeader);

        // One row per weekday, resolving BOTH phrasings. See WeekdayAnchors for why
        // the 14-day table above is not enough on its own.
        foreach (var weekday in WeekdayAnchors(anchor))
        {
            lines.Add(weekday);
        }

        lines.Add($"this weekend = Sat {Iso(saturday)} & Sun {Iso(sunday)}");
        lines.Add($"end of this month = {Iso(endOfMonth)}");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// <c>next &lt;weekday&gt;</c> and <c>this &lt;weekday&gt;</c> for all seven days,
    /// resolved to literal dates.
    ///
    /// <para>
    /// <b>The table above hands the model two of every weekday and labels neither.</b>
    /// On Thursday 2026-08-20 it contains <c>Friday 2026-08-21 (tomorrow)</c> and
    /// <c>Friday 2026-08-28 (in 8 days)</c>, and "next Friday" appears nowhere — so the
    /// instruction to "resolve phrases by FINDING them here" has nothing to find, and
    /// the model falls back to the weekday arithmetic the very next line forbids.
    /// </para>
    ///
    /// <para>
    /// <b>Measured, not theorised.</b> Two runs of the identical Arabic prompt
    /// "ذكرني يوم الاثنين بموعد الدكتور" on Monday 2026-08-17 resolved to 2026-08-24 in
    /// one and 2026-08-17 in the other — same input, same day, same table, different
    /// answer. That is the signature of an under-specified phrase, and no deterministic
    /// defect can produce it. Both transcripts are in the seeded <c>aiconversations</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Both phrasings are emitted because they collide.</b> English speakers split
    /// on whether "next Friday" means the coming one or the one after; the product rule
    /// is the SOONEST UPCOMING occurrence, and stating "this Friday" alongside it makes
    /// that rule visible rather than implied. A weekday that IS today resolves to today
    /// for "this" and to the same day for "next": the prompt separately forbids emitting
    /// a past instant, and today is not past.
    /// </para>
    /// </summary>
    private static IEnumerable<string> WeekdayAnchors(DateTime anchor)
    {
        for (var step = 0; step < 7; step++)
        {
            var day = anchor.AddDays(step);
            var name = Weekday(day);

            yield return $"next {name} = {Iso(day)} (this {name} = {Iso(day)}; the soonest upcoming {name})";
        }
    }

    /// <summary>
    /// The caller's wall clock. An absent, unknown or malformed zone is UTC — see the
    /// type remarks for why this does not follow Node's system-zone branch.
    /// </summary>
    private static DateTimeOffset ToLocal(DateTimeOffset now, string? timezone) =>
        ImportedTimeResolver.IsValidTimeZone(timezone)
            ? TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timezone!))
            : now.ToUniversalTime();

    /// <summary>
    /// <c>Intl.DateTimeFormat('en-US', { weekday: 'long' })</c>. Invariant culture, so
    /// a server running under a Dutch or Arabic locale still emits "Saturday" — the
    /// agent is told to resolve English weekday words against this table.
    /// </summary>
    private static string Weekday(DateTime day) => day.ToString("dddd", CultureInfo.InvariantCulture);

    private static string Iso(DateTime day) => day.ToString(IsoDate, CultureInfo.InvariantCulture);
}
