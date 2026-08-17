using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Notifications;

/// <summary>
/// The flatten step of <c>GET /me/reminders/upcoming</c>.
///
/// <para>
/// Three of its four stages are silent when wrong — the entry re-filter, the
/// re-sort that replaces the database ordering, and the second application of the
/// cap — and none of them changes a status code, so they are pinned here rather
/// than left to the endpoint test.
/// </para>
/// </summary>
public sealed class UpcomingReminderProjectionTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Horizon = Now.AddDays(30);

    [Fact]
    public void builds_the_deterministic_id_from_the_task_and_epoch_milliseconds()
    {
        // The re-sync contract: iOS keys pending notifications by identifier, so the
        // same (task, instant) must always produce the same string.
        var taskId = ObjectId.Parse("6a78bbfdaa461ae1dc64a10e");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1786556848864).UtcDateTime;

        var reminder = Assert.Single(Project(Task(taskId, "Renew passport", dueAt: null, Entry(at))));

        Assert.Equal("6a78bbfdaa461ae1dc64a10e:1786556848864", reminder.Id);
        Assert.Equal("6a78bbfdaa461ae1dc64a10e", reminder.TaskId);
    }

    [Fact]
    public void drops_the_other_entries_of_a_task_that_qualified_on_one()
    {
        // The $elemMatch selects the DOCUMENT, so without the re-filter a task with
        // one due reminder would leak its fired and far-future ones too.
        var task = Task(
            ObjectId.GenerateNewId(),
            "Alpha",
            dueAt: Now.AddDays(20),
            Entry(Now.AddDays(5)),
            Entry(Now.AddDays(1), firedAt: Now),
            Entry(Now.AddDays(40)),
            Entry(Now.AddHours(-1)));

        var reminder = Assert.Single(Project(task));

        Assert.Equal(Now.AddDays(5), reminder.At);
    }

    [Fact]
    public void excludes_an_entry_exactly_at_now_and_includes_one_exactly_at_the_horizon()
    {
        // The bound is `> now` and `<= horizon` — asymmetric, and it comes straight
        // from the Node predicate.
        var task = Task(ObjectId.GenerateNewId(), "Edges", dueAt: null, Entry(Now), Entry(Horizon));

        var reminder = Assert.Single(Project(task));

        Assert.Equal(Horizon, reminder.At);
    }

    [Fact]
    public void sorts_ascending_by_at_across_tasks_replacing_the_due_at_ordering()
    {
        // The query sorts by dueAt; the flatten re-sorts by `at`. Bravo is due first
        // but reminds last, so a projection that kept the query order would fail.
        var alpha = Task(ObjectId.GenerateNewId(), "Alpha", Now.AddDays(20), Entry(Now.AddDays(5)));
        var bravo = Task(ObjectId.GenerateNewId(), "Bravo", Now.AddDays(2), Entry(Now.AddDays(10)));
        var charlie = Task(ObjectId.GenerateNewId(), "Charlie", null, Entry(Now.AddDays(2)));

        var titles = UpcomingReminderProjection
            .Project(new[] { charlie, alpha, bravo }, Now, Horizon)
            .Select(r => r.Title);

        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, titles);
    }

    [Fact]
    public void caps_the_flattened_list_at_sixty_even_when_one_task_supplies_them_all()
    {
        // The task query's .limit(60) cannot do this on its own: one task can carry
        // any number of entries, which is why the cap is applied twice.
        var entries = Enumerable.Range(1, 80).Select(i => Entry(Now.AddHours(i))).ToArray();

        Assert.Equal(60, Project(Task(ObjectId.GenerateNewId(), "Many", null, entries)).Count);
    }

    [Fact]
    public void keeps_the_soonest_sixty_not_an_arbitrary_sixty()
    {
        // The cap runs AFTER the sort, so the device gets the entries it will need
        // first and the next sync tops the list up.
        var entries = Enumerable.Range(1, 80).Select(i => Entry(Now.AddHours(81 - i))).ToArray();

        var projected = Project(Task(ObjectId.GenerateNewId(), "Many", null, entries));

        Assert.Equal(Now.AddHours(1), projected[0].At);
        Assert.Equal(Now.AddHours(60), projected[^1].At);
    }

    [Fact]
    public void carries_the_parent_tasks_due_at_including_a_null_one()
    {
        var withDue = Task(ObjectId.GenerateNewId(), "Dated", Now.AddDays(9), Entry(Now.AddDays(1)));
        var without = Task(ObjectId.GenerateNewId(), "Undated", null, Entry(Now.AddDays(2)));

        var projected = UpcomingReminderProjection.Project(new[] { withDue, without }, Now, Horizon);

        Assert.Equal(Now.AddDays(9), projected[0].DueAt);
        Assert.Null(projected[1].DueAt);
    }

    [Fact]
    public void carries_the_reminder_kind_verbatim()
    {
        var task = Task(ObjectId.GenerateNewId(), "Kinds", null, Entry(Now.AddDays(1), kind: "ai"));

        Assert.Equal("ai", Assert.Single(Project(task)).Kind);
    }

    [Fact]
    public void carries_an_urgency_score_scored_at_the_reminders_own_instant()
    {
        // Not at `now`: every entry here fires in the FUTURE, and what a client needs
        // is how pressing each one will be when it lands. A 'home' matter is given a
        // 5-day window, so a heads-up 4 days ahead of the deadline is one fifth of
        // the way through it — 0.2 — on top of 'normal' rank 1.
        var task = Task(ObjectId.GenerateNewId(), "Tidy the loft", Now.AddDays(5), Entry(Now.AddDays(1)));

        Assert.Equal(1.2, Assert.Single(Project(task)).UrgencyScore);
    }

    [Fact]
    public void scores_a_higher_priority_matter_above_an_identical_lower_priority_one()
    {
        var urgent = Task(ObjectId.GenerateNewId(), "Tidy the loft", Now.AddDays(5), Entry(Now.AddDays(1)));
        urgent.Priority = "urgent";
        var calm = Task(ObjectId.GenerateNewId(), "Tidy the loft", Now.AddDays(5), Entry(Now.AddDays(1)));
        calm.Priority = "low";

        var projected = UpcomingReminderProjection.Project(new[] { urgent, calm }, Now, Horizon);

        Assert.True(projected[0].UrgencyScore > projected[1].UrgencyScore);
    }

    [Fact]
    public void still_orders_and_caps_by_time_rather_than_by_urgency()
    {
        // Phase 1 deliberately does NOT let urgency decide the device's schedule. The
        // phone schedules against a clock, and on a busy account re-ranking the cap
        // would silently drop a near-term reminder for a louder distant one — on iOS
        // this list is the ONLY delivery path when push is unavailable.
        var soonAndCalm = Task(ObjectId.GenerateNewId(), "Tidy the loft", Now.AddDays(5), Entry(Now.AddDays(1)));
        soonAndCalm.Priority = "low";
        var laterAndLoud = Task(ObjectId.GenerateNewId(), "Tidy the loft", Now.AddDays(9), Entry(Now.AddDays(8)));
        laterAndLoud.Priority = "urgent";

        var projected = UpcomingReminderProjection.Project(new[] { laterAndLoud, soonAndCalm }, Now, Horizon);

        Assert.Equal(Now.AddDays(1), projected[0].At);
        Assert.True(projected[0].UrgencyScore < projected[1].UrgencyScore);
    }

    // ---- helpers -----------------------------------------------------------

    private static IReadOnlyList<UpcomingReminderDto> Project(TaskDocument task) =>
        UpcomingReminderProjection.Project(new[] { task }, Now, Horizon);

    private static TaskDocument Task(
        ObjectId id,
        string title,
        DateTime? dueAt,
        params ReminderEntryDocument[] reminders) =>
        new()
        {
            Id = id,
            UserId = ObjectId.GenerateNewId(),
            Title = title,
            Domain = "home",
            Kind = "reminder",
            DueAt = dueAt,
            Reminders = reminders.ToList(),
        };

    private static ReminderEntryDocument Entry(DateTime at, DateTime? firedAt = null, string kind = "lead") =>
        new() { At = at, FiredAt = firedAt, Kind = kind };
}
