namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <param name="Uid">The feed's UID for the underlying event.</param>
/// <param name="ExternalId">
/// Stable identity for THIS occurrence, used as <c>Task.externalId</c>.
///
/// <para>
/// Built from the occurrence's original WALL CLOCK rather than the resolved UTC
/// instant, so a user changing their timezone re-files existing matters instead of
/// duplicating every one of them.
/// </para>
/// </param>
public sealed record IcsOccurrence(
    string Uid,
    string ExternalId,
    string Summary,
    string? Description,
    string? Location,
    IcsTimeResolution When,
    bool IsRecurring);

/// <param name="From">Ignore occurrences before this instant.</param>
/// <param name="To">Ignore occurrences after this instant.</param>
public readonly record struct ParseIcsContext(IcsTimeContext Time, DateTime From, DateTime To);

/// <summary>
/// Port of <c>server/src/modules/integrations/ics/parseIcsEvents.ts</c> — walk a
/// parsed VCALENDAR and produce the occurrences Kitto would file.
///
/// <para>
/// Feeds ship RRULEs, not instances — "every Tuesday during term" is one VEVENT,
/// not forty. Three things about the expansion are load-bearing:
/// </para>
///
/// <list type="number">
///   <item>
///     Every occurrence is converted from its OWN wall clock. Converting the first
///     and adding seven days repeatedly drifts by an hour the moment the series
///     crosses a DST boundary, which a weekly school event does twice a year.
///   </item>
///   <item>
///     Expansion is HARD-CAPPED. An RRULE with no UNTIL and no COUNT is unbounded
///     and perfectly legal, so an unguarded iterator is an infinite loop that a
///     stranger's URL can trigger on our server.
///   </item>
///   <item>
///     RECURRENCE-ID overrides are honoured. A feed that moves one instance emits a
///     second VEVENT with the same UID and a RECURRENCE-ID naming the slot it
///     replaces; expanding the master blindly would emit both the original slot and
///     the override — two matters for one event.
///   </item>
/// </list>
/// </summary>
public static class IcsEventParser
{
    /// <summary>Per-series ceiling. A daily event over a two-year window is ~730.</summary>
    public const int MaxOccurrencesPerSeries = 400;

    /// <summary>Whole-feed ceiling, so a feed of many series cannot blow up either.</summary>
    public const int MaxOccurrencesTotal = 2000;

    /// <summary>Iterator safety valve, independent of how many occurrences are kept.</summary>
    public const int MaxIterationsPerSeries = 5000;

    /// <summary>
    /// Parse a feed body into the occurrences inside <c>[from, to]</c>.
    ///
    /// <para>
    /// Never throws for feed-CONTENT reasons: a single broken VEVENT is skipped, not
    /// escalated. A feed is third-party input and one bad row must not stop a parent
    /// getting the other thirty-nine term dates. A structurally unparseable body
    /// does throw — the caller reports that as a feed-level error.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IcsOccurrence> Parse(string body, ParseIcsContext context)
    {
        var calendar = IcsTextParser.Parse(body);
        var vevents = calendar.Subcomponents("vevent").ToList();

        // First pass: find the override instances, which carry RECURRENCE-ID. They
        // are emitted as standalone occurrences and their slots are masked out of the
        // master's expansion.
        var overrides = new List<(IcsComponent Event, string Slot)>();
        var masters = new List<IcsComponent>();
        var skipByUid = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var vevent in vevents)
        {
            var recurrenceId = vevent.FirstProperty("recurrence-id");
            if (recurrenceId is null)
            {
                masters.Add(vevent);
                continue;
            }

            var read = IcsTimeResolver.ReadValue(recurrenceId);
            if (read is null)
            {
                continue;
            }

            var slot = read.Value.Wall.Stamp;
            overrides.Add((vevent, slot));

            var uid = ReadText(vevent, "uid");
            if (uid is null)
            {
                continue;
            }

            if (!skipByUid.TryGetValue(uid, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                skipByUid[uid] = set;
            }

            set.Add(slot);
        }

        var output = new List<IcsOccurrence>();
        var empty = new HashSet<string>(StringComparer.Ordinal);

        // Overrides first, so their slots are already masked when the masters expand.
        foreach (var (vevent, slot) in overrides)
        {
            if (output.Count >= MaxOccurrencesTotal)
            {
                break;
            }

            try
            {
                output.AddRange(ExpandSeries(vevent, context, empty, MaxOccurrencesTotal - output.Count, slot));
            }
            catch (Exception)
            {
                // Skip this event, keep the feed.
            }
        }

        foreach (var vevent in masters)
        {
            if (output.Count >= MaxOccurrencesTotal)
            {
                break;
            }

            var uid = ReadText(vevent, "uid");
            var skip = uid is not null && skipByUid.TryGetValue(uid, out var set) ? set : empty;

            try
            {
                output.AddRange(ExpandSeries(vevent, context, skip, MaxOccurrencesTotal - output.Count));
            }
            catch (Exception)
            {
                // Skip this event, keep the feed.
            }
        }

        return output.Count > MaxOccurrencesTotal ? output.Take(MaxOccurrencesTotal).ToList() : output;
    }

    /// <param name="skipStamps">
    /// RECURRENCE-ID slots an override VEVENT has already claimed, so the master does
    /// not also emit them.
    /// </param>
    /// <param name="overrideSlot">
    /// Set when this VEVENT is itself a RECURRENCE-ID override. Carries the stamp of
    /// the slot it replaces, which becomes its <c>externalId</c> — so a moved
    /// instance keeps the identity of the slot it came from and updates in place
    /// across polls instead of arriving as a second matter. Without this, every
    /// override on a series would fall back to the bare uid and collide with its
    /// siblings on the unique index.
    /// </param>
    private static List<IcsOccurrence> ExpandSeries(
        IcsComponent vevent,
        ParseIcsContext context,
        IReadOnlySet<string> skipStamps,
        int budget,
        string? overrideSlot = null)
    {
        var results = new List<IcsOccurrence>();

        var dtstart = vevent.FirstProperty("dtstart");
        if (dtstart is null)
        {
            return results;
        }

        var uid = ReadText(vevent, "uid");
        if (uid is null)
        {
            return results;
        }

        var read = IcsTimeResolver.ReadValue(dtstart);
        if (read is null)
        {
            return results;
        }

        var summary = ReadText(vevent, "summary") ?? "(untitled)";
        var description = ReadText(vevent, "description");
        var location = ReadText(vevent, "location");
        var rawLine = dtstart.RawLine;

        // Classify ONCE — every occurrence in a series inherits the master's zone —
        // then resolve each occurrence's own wall clock against it.
        var spec = IcsTimeResolver.ClassifyZone(dtstart, read.Value.IsDate, read.Value.IsUtc);
        var start = read.Value.Wall;

        IcsOccurrence Emit(IcsWallClock wall, bool recurring)
        {
            var when = IcsTimeResolver.ResolveWallClock(wall, spec, context.Time, rawLine);
            return new IcsOccurrence(
                uid,
                recurring ? $"{uid}::{wall.Stamp}" : uid,
                summary,
                description,
                location,
                when,
                recurring);
        }

        bool InWindow(IcsOccurrence occurrence) =>
            occurrence.When.DueAt >= context.From && occurrence.When.DueAt <= context.To;

        if (overrideSlot is not null)
        {
            var occurrence = Emit(start, true) with { ExternalId = $"{uid}::{overrideSlot}" };
            if (InWindow(occurrence))
            {
                results.Add(occurrence);
            }

            return results;
        }

        var rruleProperty = vevent.FirstProperty("rrule");
        var rule = rruleProperty is null ? null : IcsRecurrence.Parse(rruleProperty.Value);

        if (rule is null)
        {
            var occurrence = Emit(start, false);
            if (InWindow(occurrence))
            {
                results.Add(occurrence);
            }

            return results;
        }

        var excluded = ReadDateList(vevent, "exdate");
        var extra = ReadDateList(vevent, "rdate");
        var ceiling = Math.Min(MaxOccurrencesPerSeries, budget);
        var iterations = 0;

        foreach (var wall in IcsRecurrence.Expand(start, rule, MaxIterationsPerSeries))
        {
            if (results.Count >= ceiling || iterations >= MaxIterationsPerSeries)
            {
                break;
            }

            iterations += 1;

            var occurrence = Emit(wall, true);
            var at = occurrence.When.DueAt;

            // Past the window's end — later occurrences only get later, so stop.
            if (at > context.To)
            {
                break;
            }

            if (at < context.From || skipStamps.Contains(wall.Stamp) || excluded.Contains(wall.Stamp))
            {
                continue;
            }

            results.Add(occurrence);
        }

        // RDATEs are explicit extra instances. Folded in after expansion and
        // de-duplicated, so a date listed by both the rule and an RDATE is one matter.
        if (extra.Count > 0)
        {
            var seen = results.Select(o => o.ExternalId).ToHashSet(StringComparer.Ordinal);

            foreach (var stamp in extra)
            {
                if (results.Count >= ceiling || excluded.Contains(stamp) || skipStamps.Contains(stamp))
                {
                    continue;
                }

                var wall = ParseStamp(stamp);
                if (wall is null)
                {
                    continue;
                }

                var occurrence = Emit(wall.Value, true);
                if (!seen.Add(occurrence.ExternalId) || !InWindow(occurrence))
                {
                    continue;
                }

                results.Add(occurrence);
            }

            results.Sort((a, b) => a.When.DueAt.CompareTo(b.When.DueAt));
        }

        return results;
    }

    private static string? ReadText(IcsComponent vevent, string name)
    {
        var property = vevent.FirstProperty(name);
        if (property is null)
        {
            return null;
        }

        var trimmed = IcsProperty.UnescapeText(property.Value).Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    /// <summary>EXDATE / RDATE, as occurrence stamps. Both may list several values per line.</summary>
    private static HashSet<string> ReadDateList(IcsComponent vevent, string name)
    {
        var stamps = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in vevent.AllProperties(name))
        {
            foreach (var value in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var single = property with { Value = value.Trim() };
                var read = IcsTimeResolver.ReadValue(single);
                if (read is not null)
                {
                    stamps.Add(read.Value.Wall.Stamp);
                }
            }
        }

        return stamps;
    }

    private static IcsWallClock? ParseStamp(string stamp)
    {
        if (stamp.Length != 13 || stamp[8] != 'T')
        {
            return null;
        }

        return int.TryParse(stamp[..4], out var year)
            && int.TryParse(stamp.Substring(4, 2), out var month)
            && int.TryParse(stamp.Substring(6, 2), out var day)
            && int.TryParse(stamp.Substring(9, 2), out var hours)
            && int.TryParse(stamp.Substring(11, 2), out var minutes)
                ? new IcsWallClock(year, month, day, hours, minutes)
                : null;
    }
}
