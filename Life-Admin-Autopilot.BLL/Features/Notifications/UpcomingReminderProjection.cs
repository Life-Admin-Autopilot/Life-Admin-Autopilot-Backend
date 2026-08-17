using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Notifications;

/// <summary>
/// Flattens the tasks returned by the upcoming-reminders query into one entry per
/// (task, reminder).
///
/// <para>
/// Pure and separate from the repository because three of its four steps are
/// easy to get subtly wrong and none of them is visible in a status code: the
/// per-entry re-filter, the re-sort that REPLACES the database ordering, and the
/// cap that is applied a second time.
/// </para>
/// </summary>
public static class UpcomingReminderProjection
{
    /// <summary>
    /// <c>flatMap → filter → sort → slice</c>, in Node's order.
    /// </summary>
    /// <param name="tasks">Already filtered and <c>dueAt</c>-sorted by the query.</param>
    /// <param name="now">Exclusive lower bound — <c>r.at &gt; now</c>.</param>
    /// <param name="horizon">Inclusive upper bound — <c>r.at &lt;= horizon</c>.</param>
    public static IReadOnlyList<UpcomingReminderDto> Project(
        IEnumerable<TaskDocument> tasks,
        DateTime now,
        DateTime horizon)
    {
        var flattened = tasks.SelectMany(task => task.Reminders
            // The per-entry filter is re-applied AFTER flattening. Without it a task
            // that qualified on one reminder would leak its others — the $elemMatch
            // selects the DOCUMENT, not the entry.
            .Where(r => r.FiredAt is null && r.At > now && r.At <= horizon)
            .Select(r => new UpcomingReminderDto
            {
                Id = BuildId(task.Id.ToString(), r.At),
                TaskId = task.Id.ToString(),
                Title = task.Title,
                At = r.At,
                Kind = r.Kind,
                DueAt = task.DueAt,

                // Scored at the reminder's OWN instant, not at `now`: this entry is
                // going to fire in the future, and what matters is how pressing it
                // will be THEN. Carried so the device can rank a batch it receives
                // all at once; it does not affect the order or the cap below.
                UrgencyScore = ReminderUrgency.Score(
                    new ReminderTaskShape(task.Title, task.Domain, task.Kind, task.DueAt),
                    task.Priority,
                    r.At),
            }));

        return flattened
            // Node sorts on `a.at.localeCompare(b.at)` over the ISO strings. Every
            // value is `Date#toISOString()` — same length, same 3 fractional digits,
            // always `Z` — so ordinal string order and chronological order are the
            // same thing, and Mongo stores milliseconds so no finer tick can split
            // them. Both V8's sort and LINQ's OrderBy are stable, so ties keep the
            // dueAt ordering the query established.
            .OrderBy(r => r.At)
            // The cap applies TWICE: once as .limit(60) on the tasks, and again here
            // on the flattened list, because one task can contribute several entries.
            .Take(MaxReminders)
            .ToList();
    }

    /// <summary>Mirrors <c>ReminderTaskRepository.MaxReminders</c>; see that constant for the iOS rationale.</summary>
    private const int MaxReminders = 60;

    /// <summary>
    /// <c>`${taskId}:${at.getTime()}`</c> — epoch MILLISECONDS, matching
    /// JavaScript's <c>Date#getTime()</c>.
    /// </summary>
    public static string BuildId(string taskId, DateTime at) =>
        $"{taskId}:{new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds()}";
}
