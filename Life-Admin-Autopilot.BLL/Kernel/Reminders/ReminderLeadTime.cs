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

    private static readonly (Regex Pattern, int Days)[] KeywordLeadDays =
    {
        (new Regex("passport", RegexOptions.IgnoreCase | RegexOptions.Compiled), 180),
        (new Regex(@"driv(ing|er).?licen[cs]e|\blicen[cs]e\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 60),
        (new Regex(@"registration|\brego\b|\bmot\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 45),
        (new Regex("insurance|policy|warrant", RegexOptions.IgnoreCase | RegexOptions.Compiled), 30),
        (new Regex(@"\btax(es)?\b|\bvat\b|filing", RegexOptions.IgnoreCase | RegexOptions.Compiled), 14),
        (new Regex("subscription|membership|renew|expir", RegexOptions.IgnoreCase | RegexOptions.Compiled), 21),
        (new Regex(@"\bbill\b|payment|invoice|\brent\b|mortgage|utilit|fees?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 5),
        (new Regex(@"appointment|\bappt\b|doctor|dentist|\bvet\b|meeting|call\b|interview", RegexOptions.IgnoreCase | RegexOptions.Compiled), 1),
    };

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
        foreach (var (pattern, days) in KeywordLeadDays)
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
    /// when it is still in the future AND meaningfully before due) plus an at-due
    /// nudge. List items and dateless tasks get nothing. A past-due reminder gets
    /// only the <c>due</c> entry, which fires on the next worker tick. Returned
    /// sorted ascending.
    /// </summary>
    public static IReadOnlyList<PlannedReminder> ComputeRules(ReminderTaskShape task, DateTime now)
    {
        if (task.Kind != "reminder" || task.DueAt is null)
        {
            return Array.Empty<PlannedReminder>();
        }

        var due = task.DueAt.Value;
        var result = new List<PlannedReminder>();

        var leadAt = due.AddTicks(-ComputeLeadDays(task) * DayMs * TimeSpan.TicksPerMillisecond);
        if (leadAt > now && (due - leadAt).TotalMilliseconds > 60_000)
        {
            result.Add(new PlannedReminder(leadAt, "lead"));
        }

        result.Add(new PlannedReminder(due, "due"));
        return result.OrderBy(r => r.At).ToList();
    }
}
