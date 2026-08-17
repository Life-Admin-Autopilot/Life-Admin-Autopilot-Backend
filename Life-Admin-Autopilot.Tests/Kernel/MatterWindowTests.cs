using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// What a matter OCCUPIES, and when two of them collide — the arithmetic behind
/// Phase 3 of <c>docs/smart-reminder-conflict-spec.md</c>.
///
/// <para>
/// <b>The rule this replaces had no test at any level</b>, in a service with four
/// callers. It flagged two matters whose <c>dueAt</c> fell within a fixed two hours
/// of each other, whatever they were — so the cases below are written to show the
/// old rule failing in BOTH directions, not merely to confirm the new one.
/// </para>
/// </summary>
public sealed class MatterWindowTests
{
    private static readonly DateTime Noon = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    // ---- the span a matter occupies --------------------------------------------

    [Fact]
    public void runs_from_the_last_moment_it_can_be_started_to_its_deadline()
    {
        // The same interval Phase 2 schedules the final nudge against — one notion of
        // "how long this takes", used by both, so a reminder and a conflict can never
        // disagree about it.
        var span = MatterWindow.For("File tax return", "finance", null, Noon);

        Assert.Equal(Noon.AddMinutes(-120), span.Start);
        Assert.Equal(Noon, span.End);
        Assert.Equal(TimeSpan.FromMinutes(120), span.Length);
    }

    [Fact]
    public void prefers_the_matters_own_estimate_when_it_has_one()
    {
        var estimate = new TaskEstimateDocument { MinMinutes = 15, MaxMinutes = 45, Source = "user" };

        Assert.Equal(Noon.AddMinutes(-45), MatterWindow.For("File tax return", "finance", estimate, Noon).Start);
    }

    [Fact]
    public void has_no_span_for_a_matter_with_no_deadline()
    {
        // A dateless matter cannot collide with anything — there is no "when".
        Assert.Null(MatterWindow.For(new TaskDocument { Title = "Someday", Domain = "home" }));
    }

    [Fact]
    public void reads_a_span_off_a_saved_matter()
    {
        var task = new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            Title = "Dentist appointment",
            Domain = "health",
            DueAt = Noon,
        };

        Assert.Equal(Noon.AddMinutes(-60), MatterWindow.For(task)!.Value.Start);
    }

    // ---- overlap ---------------------------------------------------------------

    [Fact]
    public void flags_two_long_matters_the_old_fixed_radius_could_not_see()
    {
        // THE case the old rule missed. Two four-hour jobs three hours apart overlap
        // for a full hour, and their deadlines are more than two hours apart, so the
        // old radius reported them as fine.
        var first = Span(Noon, 240);
        var second = Span(Noon.AddHours(3), 240);

        Assert.True(MatterWindow.Overlap(first, second));
        Assert.True((second.End - first.End).Duration() > TimeSpan.FromHours(2));
    }

    [Fact]
    public void clears_two_quick_errands_the_old_fixed_radius_flagged()
    {
        // THE case the old rule got wrong the other way. Two ten-minute bills ninety
        // minutes apart share no time at all, and were reported as a clash purely
        // because their deadlines sat inside two hours.
        var first = Span(Noon, 10);
        var second = Span(Noon.AddMinutes(90), 10);

        Assert.False(MatterWindow.Overlap(first, second));
        Assert.True((second.End - first.End).Duration() < TimeSpan.FromHours(2));
    }

    [Fact]
    public void flags_a_genuine_intersection()
    {
        Assert.True(MatterWindow.Overlap(Span(Noon, 120), Span(Noon.AddHours(1), 120)));
    }

    [Fact]
    public void flags_one_matter_wholly_inside_another()
    {
        // Containment is the case a naive endpoint comparison misses.
        var long_ = Span(Noon.AddHours(4), 240);
        var short_ = Span(Noon.AddHours(2), 15);

        Assert.True(MatterWindow.Overlap(long_, short_));
        Assert.True(MatterWindow.Overlap(short_, long_));
    }

    [Fact]
    public void is_symmetric()
    {
        var a = Span(Noon, 60);
        var b = Span(Noon.AddMinutes(30), 60);

        Assert.Equal(MatterWindow.Overlap(a, b), MatterWindow.Overlap(b, a));
    }

    // ---- the buffer ------------------------------------------------------------

    [Fact]
    public void flags_two_matters_that_leave_less_breathing_room_than_the_buffer()
    {
        // Ends at 12:00, the next starts at 12:10. They do not intersect, but ten
        // minutes is not enough to travel, find the right document, or draw breath.
        var first = Span(Noon, 120);
        var second = Span(Noon.AddMinutes(25), 15);

        Assert.False(MatterWindow.Overlap(first, second, TimeSpan.Zero));
        Assert.True(MatterWindow.Overlap(first, second));
    }

    [Fact]
    public void clears_two_matters_separated_by_more_than_the_buffer()
    {
        var first = Span(Noon, 120);
        var second = Span(Noon.AddMinutes(31), 15);

        Assert.False(MatterWindow.Overlap(first, second));
    }

    [Fact]
    public void demands_the_gap_between_the_two_not_that_gap_from_each()
    {
        // Padding both spans would silently require thirty minutes while the constant
        // says fifteen. Exactly the buffer apart is the boundary, and it clears.
        // Ends at 12:00; the next runs 12:15-12:30. Exactly the buffer apart, and it
        // clears — padding both spans would demand thirty and flag this.
        var first = Span(Noon, 60);
        var second = Span(Noon.AddMinutes(30), 15);

        Assert.Equal(TimeSpan.FromMinutes(15), second.Start - first.End);
        Assert.False(MatterWindow.Overlap(first, second));
    }

    [Fact]
    public void treats_back_to_back_matters_as_a_clash()
    {
        // Touching intervals read as "fine" under a pure overlap test, and are not.
        var first = Span(Noon, 60);
        var second = Span(Noon.AddMinutes(30), 30);

        Assert.Equal(first.End, second.Start);
        Assert.False(MatterWindow.Overlap(first, second, TimeSpan.Zero));
        Assert.True(MatterWindow.Overlap(first, second));
    }

    [Fact]
    public void keeps_the_buffer_at_a_value_a_person_could_predict()
    {
        // Deliberately fixed rather than scaled to the longer matter: a boundary that
        // moves per pair cannot be explained to a user in one sentence.
        Assert.Equal(TimeSpan.FromMinutes(15), MatterWindow.Buffer);
    }

    // ---- helpers ---------------------------------------------------------------

    private static MatterWindow.Span Span(DateTime due, int minutes) =>
        new(due.AddMinutes(-minutes), due);
}
