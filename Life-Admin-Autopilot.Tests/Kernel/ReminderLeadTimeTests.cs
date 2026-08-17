using Life_Admin_Autopilot.BLL.Kernel.Reminders;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The lead-time table — how far ahead of a deadline each kind of matter is
/// nudged.
///
/// <para>
/// <b>This had no test at all before Phase 1.</b> The port carried the table over
/// from <c>server/src/modules/reminders/leadTime.ts</c> faithfully, but nothing
/// pinned it, and it is the deterministic FLOOR the whole reminder system falls
/// back to whenever AI refinement is off, over quota or failing. It is also now an
/// input to <see cref="ReminderUrgency"/>, which normalises deadline pressure
/// against the window this table returns — so a silent change here moves both when
/// a reminder fires and how loud it is.
/// </para>
///
/// <para>
/// <b>The table is ORDERED and the first match wins.</b> Several titles match more
/// than one row, so the overlap cases below are not padding — they are the reason
/// the ordering cannot be rearranged.
/// </para>
/// </summary>
public sealed class ReminderLeadTimeTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    // ---- keyword table --------------------------------------------------------

    [Theory]
    [InlineData("Renew passport", 180)]
    [InlineData("Renew driving licence", 60)]
    [InlineData("Driver license renewal", 60)]
    [InlineData("Car registration", 45)]
    [InlineData("Book the MOT", 45)]
    [InlineData("Car insurance", 30)]
    [InlineData("Cancel Netflix subscription", 21)]
    [InlineData("Gym membership", 21)]
    [InlineData("File tax return", 14)]
    [InlineData("Submit VAT", 14)]
    [InlineData("Pay electricity bill", 5)]
    [InlineData("Pay the rent", 5)]
    [InlineData("Mortgage payment", 5)]
    [InlineData("Dentist appointment", 1)]
    [InlineData("Job interview", 1)]
    public void gives_each_kind_of_matter_the_warning_its_stakes_deserve(string title, int expected)
    {
        // A passport renewal deserves months and an appointment a day — the single
        // judgement this table encodes.
        Assert.Equal(expected, ReminderLeadTime.ComputeLeadDays(Matter(title, "home")));
    }

    [Fact]
    public void matches_a_keyword_regardless_of_case()
    {
        Assert.Equal(180, ReminderLeadTime.ComputeLeadDays(Matter("RENEW PASSPORT", "home")));
    }

    [Theory]
    // 'Renew passport' matches both /passport/ (180) and /renew/ (21).
    [InlineData("Renew passport", 180)]
    // 'Renew car insurance' matches /insurance/ (30) and /renew/ (21).
    [InlineData("Renew car insurance", 30)]
    // 'Pay the insurance bill' matches /insurance/ (30) and /bill/ (5).
    [InlineData("Pay the insurance bill", 30)]
    // The exception that proves the rule is POSITION, not size: /tax/ sits at index 4
    // and /renew/ at index 5, so this resolves to the SHORTER lead of the two.
    [InlineData("Renew tax filing", 14)]
    public void resolves_an_overlapping_title_by_table_position_not_by_which_lead_is_longer(
        string title,
        int expected)
    {
        // Read the table as an ordered list of decisions, not a sorted one. It runs
        // 180, 60, 45, 30, 14, 21, 5, 1 — deliberately NOT monotonic — so rearranging
        // the rows silently changes what several real titles resolve to.
        Assert.Equal(expected, ReminderLeadTime.ComputeLeadDays(Matter(title, "home")));
    }

    // ---- domain fallback ------------------------------------------------------

    [Theory]
    [InlineData("health", 3)]
    [InlineData("home", 5)]
    [InlineData("car", 14)]
    [InlineData("finance", 5)]
    [InlineData("family", 3)]
    [InlineData("pets", 3)]
    public void falls_back_to_the_domain_when_no_keyword_matches(string domain, int expected)
    {
        Assert.Equal(expected, ReminderLeadTime.ComputeLeadDays(Matter("Sort the thing out", domain)));
    }

    [Fact]
    public void falls_back_to_three_days_for_a_domain_outside_the_vocabulary()
    {
        Assert.Equal(3, ReminderLeadTime.ComputeLeadDays(Matter("Sort the thing out", "not-a-domain")));
    }

    [Fact]
    public void lets_a_keyword_beat_the_domain_default()
    {
        // 'car' defaults to 14 days; a passport in the car domain is still 180.
        Assert.Equal(180, ReminderLeadTime.ComputeLeadDays(Matter("Renew passport", "car")));
    }

    // ---- the schedule ---------------------------------------------------------

    [Fact]
    public void plans_a_heads_up_and_an_at_deadline_nudge_for_a_dated_reminder()
    {
        var due = Now.AddDays(30);

        var planned = Plan(Matter("Pay electricity bill", "home", due), Now);

        Assert.Collection(
            planned,
            lead =>
            {
                Assert.Equal("lead", lead.Kind);
                Assert.Equal(due.AddDays(-5), lead.At);
            },
            at =>
            {
                Assert.Equal("due", at.Kind);
                Assert.Equal(due, at.At);
            });
    }

    [Fact]
    public void drops_the_heads_up_when_its_moment_has_already_passed()
    {
        // A bill due tomorrow cannot be warned about 5 days ago. Only the at-deadline
        // nudge survives — scheduling the lead in the past would fire it instantly.
        var planned = Plan(Matter("Pay electricity bill", "home", Now.AddDays(1)), Now);

        Assert.Equal("due", Assert.Single(planned).Kind);
    }

    [Fact]
    public void plans_only_the_deadline_nudge_for_a_matter_already_past_due()
    {
        // It fires on the next tick, which is how a reminder survives a worker outage
        // instead of being silently skipped.
        var due = Now.AddDays(-3);

        var only = Assert.Single(Plan(Matter("Pay electricity bill", "home", due), Now));

        Assert.Equal("due", only.Kind);
        Assert.Equal(due, only.At);
    }

    [Fact]
    public void drops_a_heads_up_that_would_land_exactly_now()
    {
        // The bound is strict (`leadAt > now`), so an appointment exactly one day out
        // gets the deadline nudge alone rather than a heads-up firing this instant.
        var planned = Plan(Matter("Dentist appointment", "health", Now.AddDays(1)), Now);

        Assert.Equal("due", Assert.Single(planned).Kind);
    }

    [Fact]
    public void keeps_a_heads_up_one_second_later_than_that()
    {
        // The other side of the same bound — without this the test above would still
        // pass if the heads-up had been removed altogether.
        var planned = Plan(Matter("Dentist appointment", "health", Now.AddDays(1).AddSeconds(1)), Now);

        Assert.Equal(2, planned.Count);
        Assert.Equal("lead", planned[0].Kind);
    }

    [Fact]
    public void plans_nothing_for_a_list_item()
    {
        // A list item is passive by design — it is the shape a high-cost guess is
        // filed as precisely so that a guessed date can never fire.
        var task = new ReminderTaskShape("Pay electricity bill", "home", "list", Now.AddDays(30));

        Assert.Empty(Plan(task, Now));
    }

    [Fact]
    public void plans_nothing_for_a_reminder_with_no_deadline()
    {
        Assert.Empty(Plan(Matter("Someday", "home", dueAt: null), Now));
    }

    [Fact]
    public void returns_the_schedule_in_chronological_order()
    {
        var planned = Plan(Matter("Renew passport", "home", Now.AddDays(365)), Now);

        Assert.Equal(planned.OrderBy(p => p.At).Select(p => p.At), planned.Select(p => p.At));
    }

    // ---- the window (spec Phase 2) ---------------------------------------------

    [Fact]
    public void fires_the_final_nudge_early_enough_to_actually_do_the_thing()
    {
        // The change Phase 2 exists for. Telling someone at 17:00 that a four-hour
        // job was due at 17:00 is an accusation, not a reminder.
        var due = Now.AddDays(30);

        var planned = ReminderLeadTime.ComputeRules(
            Matter("File tax return", "finance", due),
            Now,
            durationMinutes: 240);

        Assert.Equal(due.AddMinutes(-240), planned[^1].At);
        Assert.Equal("due", planned[^1].Kind);
    }

    [Fact]
    public void barely_moves_a_short_matter()
    {
        // The shift is proportional, so a two-minute job is still effectively an
        // at-deadline nudge and nothing about the existing behaviour has to change
        // for the matters that make up most of a list.
        var due = Now.AddDays(30);

        var planned = ReminderLeadTime.ComputeRules(
            Matter("Pay electricity bill", "home", due),
            Now,
            durationMinutes: 10);

        Assert.Equal(due.AddMinutes(-10), planned[^1].At);
    }

    [Fact]
    public void leaves_the_heads_up_exactly_where_it_was()
    {
        // The window moves the FINAL nudge only. The lead nudge is measured from the
        // deadline and must not drift with an estimate.
        var due = Now.AddDays(30);

        var withWindow = ReminderLeadTime.ComputeRules(Matter("File tax return", "finance", due), Now, 240);
        var without = ReminderLeadTime.ComputeRules(Matter("File tax return", "finance", due), Now, 0);

        Assert.Equal(without[0].At, withWindow[0].At);
        Assert.Equal(due.AddDays(-14), withWindow[0].At);
    }

    [Fact]
    public void keeps_the_schedule_in_order_when_the_window_is_wide()
    {
        var due = Now.AddDays(2);

        var planned = ReminderLeadTime.ComputeRules(
            Matter("Dentist appointment", "health", due),
            Now,
            durationMinutes: 240);

        Assert.Equal(planned.OrderBy(p => p.At).Select(p => p.At), planned.Select(p => p.At));
        Assert.True(planned[0].At < planned[^1].At);
    }

    [Fact]
    public void treats_a_negative_duration_as_no_window_rather_than_scheduling_after_the_deadline()
    {
        // Nothing should ever produce one, but a nudge placed AFTER the deadline is
        // the one outcome this feature must never create.
        var due = Now.AddDays(30);

        var planned = ReminderLeadTime.ComputeRules(Matter("Pay electricity bill", "home", due), Now, -120);

        Assert.Equal(due, planned[^1].At);
    }

    [Fact]
    public void still_fires_a_past_due_matter_on_the_next_tick_with_a_window()
    {
        // The shift must not turn an overdue reminder into a silent one.
        var due = Now.AddDays(-3);

        var only = Assert.Single(ReminderLeadTime.ComputeRules(Matter("File tax return", "finance", due), Now, 240));

        Assert.Equal(due.AddMinutes(-240), only.At);
        Assert.True(only.At < Now);
    }

    // ---- helpers ---------------------------------------------------------------

    private static ReminderTaskShape Matter(string title, string domain, DateTime? dueAt = null) =>
        new(title, domain, "reminder", dueAt);

    /// <summary>
    /// Plans with NO duration. These cases are about the lead-time half, so the
    /// window shift is held at zero to keep them reading as they did before it
    /// existed; the window itself is covered separately below.
    /// </summary>
    private static IReadOnlyList<PlannedReminder> Plan(ReminderTaskShape task, DateTime now) =>
        ReminderLeadTime.ComputeRules(task, now, durationMinutes: 0);
}
