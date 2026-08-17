using Life_Admin_Autopilot.BLL.Kernel.Reminders;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The ranking key that decides which reminder the user sees first — Phase 1 of
/// <c>docs/smart-reminder-conflict-spec.md</c>.
///
/// <para>
/// Every case here is arithmetic with no I/O, which is the point: this ordering
/// lives inside a Mongo-dependent worker tick, and an ordering that can only be
/// checked against a live database is an ordering nobody checks.
/// </para>
///
/// <para>
/// <b>None of this may change a schedule.</b> The lead-time table stays the sole
/// authority on when a reminder fires; these tests only pin how loud it is.
/// </para>
/// </summary>
public sealed class ReminderUrgencyTests
{
    private static readonly DateTime Due = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

    // ---- pressure: the deadline half -----------------------------------------

    [Fact]
    public void reads_zero_at_the_moment_the_heads_up_fires_and_one_at_the_deadline()
    {
        // The two ends of the window the lead-time table defines. A 'home' matter is
        // given 5 days of warning, so those 5 days are the whole scale.
        var task = Matter("Tidy the loft", domain: "home");

        Assert.Equal(0, ReminderUrgency.Pressure(task, Due.AddDays(-5)));
        Assert.Equal(1, ReminderUrgency.Pressure(task, Due));
    }

    [Fact]
    public void rises_linearly_across_the_window()
    {
        var task = Matter("Tidy the loft", domain: "home");

        Assert.Equal(0.5, ReminderUrgency.Pressure(task, Due.AddDays(-2.5)));
        Assert.Equal(0.8, ReminderUrgency.Pressure(task, Due.AddDays(-1)), precision: 10);
    }

    [Fact]
    public void measures_against_the_matters_own_window_not_a_fixed_span()
    {
        // The whole reason `task_domain` is in the spec's formula. Ninety days out is
        // half way through a passport's 180-day warning and not remotely started on a
        // 5-day one — the SAME instant, opposite readings.
        var passport = Matter("Renew passport", domain: "home");
        var loft = Matter("Tidy the loft", domain: "home");

        Assert.Equal(0.5, ReminderUrgency.Pressure(passport, Due.AddDays(-90)));
        Assert.Equal(0, ReminderUrgency.Pressure(loft, Due.AddDays(-90)));
    }

    [Fact]
    public void clamps_an_overdue_reminder_at_one_rather_than_letting_it_run_away()
    {
        // A reminder the worker was late to fire belongs at the top of the list, not
        // above every conceivable future one — an unbounded score would make a single
        // missed tick outrank everything for good.
        var task = Matter("Tidy the loft", domain: "home");

        Assert.Equal(1, ReminderUrgency.Pressure(task, Due.AddDays(30)));
    }

    [Fact]
    public void scores_no_pressure_at_all_when_there_is_no_deadline_to_press_against()
    {
        var task = Dateless("Someday", domain: "home");

        Assert.Equal(0, ReminderUrgency.Pressure(task, Due));
    }

    // ---- score: priority + pressure -------------------------------------------

    [Fact]
    public void puts_priority_in_the_integer_part_and_pressure_in_the_fraction()
    {
        // 'high' is rank 2, half way through the window is 0.5 — the score reads
        // directly as "a high matter, half way to its deadline".
        var task = Matter("Tidy the loft", domain: "home");

        Assert.Equal(2.5, ReminderUrgency.Score(task, "high", Due.AddDays(-2.5)));
    }

    [Theory]
    [InlineData("low", 1.0)]
    [InlineData("normal", 2.0)]
    [InlineData("high", 3.0)]
    [InlineData("urgent", 4.0)]
    public void tops_out_at_the_priority_rank_plus_one_at_the_deadline(string priority, double expected)
    {
        Assert.Equal(expected, ReminderUrgency.Score(Matter("Tidy the loft", "home"), priority, Due));
    }

    [Fact]
    public void bottoms_out_at_zero_for_a_low_matter_whose_window_has_only_just_opened()
    {
        Assert.Equal(0, ReminderUrgency.Score(Matter("Tidy the loft", "home"), "low", Due.AddDays(-5)));
    }

    [Fact]
    public void never_exceeds_the_documented_ceiling()
    {
        // Anything reading this as a normalised value needs the bound to hold even
        // for an overdue urgent matter, where pressure clamps rather than growing.
        var score = ReminderUrgency.Score(Matter("Tidy the loft", "home"), "urgent", Due.AddYears(1));

        Assert.Equal(ReminderUrgency.MaxScore, score);
    }

    [Fact]
    public void lets_a_stated_priority_beat_a_merely_inferred_deadline_pressure()
    {
        // The user's own word outranks our arithmetic: an urgent matter whose window
        // has only just opened still sits above a low one that is due right now.
        var justOpened = ReminderUrgency.Score(Matter("Tidy the loft", "home"), "urgent", Due.AddDays(-5));
        var dueNow = ReminderUrgency.Score(Matter("Tidy the loft", "home"), "low", Due);

        Assert.True(justOpened > dueNow, $"{justOpened} should outrank {dueNow}");
    }

    [Fact]
    public void ties_exactly_at_a_priority_boundary()
    {
        // Documented, not accidental: a low matter at its deadline and a normal one
        // at the start of its window are both 1.0. DeliveryOrder breaks it on time,
        // which is why the tie is allowed to exist rather than fudged away.
        var lowAtDeadline = ReminderUrgency.Score(Matter("Tidy the loft", "home"), "low", Due);
        var normalAtWindowStart = ReminderUrgency.Score(Matter("Tidy the loft", "home"), "normal", Due.AddDays(-5));

        Assert.Equal(lowAtDeadline, normalAtWindowStart);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bogus")]
    [InlineData("")]
    public void falls_back_to_normal_for_a_priority_outside_the_vocabulary(string? priority)
    {
        // Same fallback TaskVocabulary.RankFor already applies, reused rather than
        // re-implemented — two copies of that table would drift. Scored at the start
        // of the window so pressure is 0 and the rank is the whole score.
        Assert.Equal(1.0, ReminderUrgency.Score(Matter("Tidy the loft", "home"), priority, Due.AddDays(-5)));
    }

    [Fact]
    public void ranks_a_dateless_matter_on_its_stated_priority_alone()
    {
        var task = Dateless("Someday", domain: "home");

        Assert.Equal(3.0, ReminderUrgency.Score(task, "urgent", Due));
        Assert.Equal(0.0, ReminderUrgency.Score(task, "low", Due));
    }

    [Fact]
    public void rounds_to_three_decimals_so_the_value_serialises_identically_every_run()
    {
        // A third of a 5-day window is a repeating fraction; the DTO carries this
        // number to a client, so it must not depend on floating-point luck.
        var score = ReminderUrgency.Score(Matter("Tidy the loft", "home"), "normal", Due.AddDays(-5.0 / 3.0));

        Assert.Equal(Math.Round(score, 3), score);
    }

    // ---- delivery order --------------------------------------------------------

    [Fact]
    public void hands_back_the_least_urgent_first_because_the_last_row_written_is_the_one_on_top()
    {
        // The inversion that carries the whole feature. Both surfaces show newest
        // first, so ascending urgency is what puts the urgent matter at the top.
        var items = new[] { ("calm", 1.0), ("loudest", 3.5), ("middling", 2.0) };

        var ordered = ReminderUrgency.DeliveryOrder(items, i => i.Item2, _ => Due);

        Assert.Equal(new[] { "calm", "middling", "loudest" }, ordered.Select(i => i.Item1));
    }

    [Fact]
    public void breaks_a_tie_on_time_so_the_sooner_deadline_takes_the_higher_slot()
    {
        // Written LAST means shown FIRST, so the earliest instant must come last.
        // Without this the order would fall back to whatever Mongo returned, which is
        // the arbitrariness this class exists to remove.
        var items = new[] { ("later", Due.AddDays(2)), ("sooner", Due), ("middle", Due.AddDays(1)) };

        var ordered = ReminderUrgency.DeliveryOrder(items, _ => 2.0, i => i.Item2);

        Assert.Equal(new[] { "later", "middle", "sooner" }, ordered.Select(i => i.Item1));
    }

    [Fact]
    public void ranks_by_score_before_time_not_the_other_way_round()
    {
        // A sooner but calmer reminder must not displace an urgent one.
        var items = new[] { ("urgent later", 4.0, Due.AddDays(9)), ("calm sooner", 0.5, Due) };

        var ordered = ReminderUrgency.DeliveryOrder(items, i => i.Item2, i => i.Item3);

        Assert.Equal(new[] { "calm sooner", "urgent later" }, ordered.Select(i => i.Item1));
    }

    // ---- helpers ---------------------------------------------------------------

    private static ReminderTaskShape Matter(string title, string domain) =>
        new(title, domain, "reminder", Due);

    /// <summary>A matter carrying no deadline at all — not one defaulted to <see cref="Due"/>.</summary>
    private static ReminderTaskShape Dateless(string title, string domain) =>
        new(title, domain, "reminder", null);
}
