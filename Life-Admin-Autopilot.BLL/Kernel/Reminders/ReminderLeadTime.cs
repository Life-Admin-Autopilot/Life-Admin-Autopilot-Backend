using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.BLL.Kernel.Reminders;

/// <summary>Minimal shape the lead-time table needs. Avoids dragging a whole TaskDocument in.</summary>
public readonly record struct ReminderTaskShape(string Title, string Domain, string Kind, DateTime? DueAt);

public readonly record struct PlannedReminder(DateTime At, string Kind);

/// <summary>
/// Port of <c>server/src/modules/reminders/leadTime.ts</c> — how far AHEAD of a
/// deadline to nudge, by task type.
///
/// <para>
/// A passport renewal deserves months of warning, a bill days, an appointment a
/// day. <b>Keyword match wins over the domain default</b>, and the table is
/// ordered — the first regex that matches the title wins, so the long-lead
/// entries must stay above the short-lead ones.
/// </para>
///
/// <para>This is the deterministic FLOOR. The AI planner may refine it, but the
/// floor stands whenever AI is off, over quota or failing.</para>
/// </summary>
public static class ReminderLeadTime
{
    private const long DayMs = 86_400_000;

    /// <summary>
    /// Names for the rows of the keyword table, so a SECOND table can key off the
    /// same match instead of restating the regexes.
    ///
    /// <para>
    /// <see cref="ReminderDuration"/> answers a different question about the same
    /// eight kinds of matter — how long one takes to DO, rather than how much
    /// warning it deserves — and two copies of these patterns would drift the first
    /// time either was edited.
    /// </para>
    /// </summary>
    public enum MatterKeyword
    {
        Passport,
        Licence,
        Registration,
        Insurance,
        Tax,
        Subscription,
        Bill,
        Appointment,
    }

    private static readonly (Regex Pattern, MatterKeyword Key, int Days)[] KeywordLeadDays =
    {
        (new Regex("passport", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Passport, 180),
        (new Regex(@"driv(ing|er).?licen[cs]e|\blicen[cs]e\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Licence, 60),
        (new Regex(@"registration|\brego\b|\bmot\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Registration, 45),
        (new Regex("insurance|policy|warrant", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Insurance, 30),
        (new Regex(@"\btax(es)?\b|\bvat\b|filing", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Tax, 14),
        (new Regex("subscription|membership|renew|expir", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Subscription, 21),
        (new Regex(@"\bbill\b|payment|invoice|\brent\b|mortgage|utilit|fees?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Bill, 5),
        (new Regex(@"appointment|\bappt\b|doctor|dentist|\bvet\b|meeting|call\b|interview", RegexOptions.IgnoreCase | RegexOptions.Compiled), MatterKeyword.Appointment, 1),
    };

    /// <summary>
    /// Which row of the table this title hits, or <c>null</c> for none. Ordered —
    /// the FIRST match wins, and the order is deliberately not sorted by lead time.
    /// </summary>
    public static MatterKeyword? MatchKeyword(string title)
    {
        foreach (var (pattern, key, _) in KeywordLeadDays)
        {
            if (pattern.IsMatch(title))
            {
                return key;
            }
        }

        return null;
    }

    private static readonly IReadOnlyDictionary<string, int> DomainDefaultLeadDays = new Dictionary<string, int>
    {
        ["health"] = 3,
        ["home"] = 5,
        ["car"] = 14,
        ["finance"] = 5,
        ["family"] = 3,
        ["pets"] = 3,
    };

    public static int ComputeLeadDays(ReminderTaskShape task)
    {
        foreach (var (pattern, _, days) in KeywordLeadDays)
        {
            if (pattern.IsMatch(task.Title))
            {
                return days;
            }
        }

        return DomainDefaultLeadDays.TryGetValue(task.Domain, out var fallback) ? fallback : 3;
    }

    /// <summary>
    /// Deterministic schedule for a reminder task: a smart lead-time nudge (only
    /// when it is still in the future AND meaningfully before the final one) plus a
    /// nudge at the LAST MOMENT IT IS STILL POSSIBLE TO START. List items and
    /// dateless tasks get nothing. A past-due reminder gets only the final entry,
    /// which fires on the next worker tick. Returned sorted ascending.
    /// </summary>
    /// <param name="durationMinutes">
    /// How long the matter takes to do, from <see cref="ReminderDuration"/>.
    ///
    /// <para>
    /// <b>This is what makes the schedule window-aware</b>
    /// (<c>docs/smart-reminder-conflict-spec.md</c> §3.2). The final nudge used to
    /// land exactly ON the deadline, which is the one moment at which a reminder can
    /// no longer be acted on: telling someone at 17:00 that a four-hour job was due
    /// at 17:00 is an accusation, not a reminder. Firing it a duration earlier
    /// leaves exactly enough room to do the thing.
    /// </para>
    ///
    /// <para>
    /// Pass <c>0</c> for the pre-window behaviour. Deliberately NOT defaulted — a
    /// caller that has not thought about duration should have to say so.
    /// </para>
    /// </param>
    public static IReadOnlyList<PlannedReminder> ComputeRules(
        ReminderTaskShape task,
        DateTime now,
        int durationMinutes)
    {
        if (task.Kind != "reminder" || task.DueAt is null)
        {
            return Array.Empty<PlannedReminder>();
        }

        var due = task.DueAt.Value;
        var startAt = LastMomentToStart(due, durationMinutes);
        var result = new List<PlannedReminder>();

        var leadAt = due.AddTicks(-ComputeLeadDays(task) * DayMs * TimeSpan.TicksPerMillisecond);

        // Measured against the entry that actually gets scheduled, not against the
        // deadline — otherwise a long-duration matter could be given a heads-up that
        // lands after the nudge it is supposed to precede. Unreachable while the
        // shortest lead is a day and the longest duration four hours, but the guard
        // should mean what it says.
        if (leadAt > now && (startAt - leadAt).TotalMilliseconds > 60_000)
        {
            result.Add(new PlannedReminder(leadAt, "lead"));
        }

        // Kind stays 'due'. It is the vocabulary the device, the notification copy
        // and every stored row already speak, and the entry still means "this is the
        // one about the deadline" — it has only stopped arriving too late to use.
        result.Add(new PlannedReminder(startAt, "due"));
        return result.OrderBy(r => r.At).ToList();
    }

    /// <summary>
    /// The deadline, less the time the matter needs. Shared with the AI refinement
    /// path so both agree on where the last nudge belongs.
    /// </summary>
    public static DateTime LastMomentToStart(DateTime due, int durationMinutes) =>
        due.AddMinutes(-Math.Max(0, durationMinutes));
}
