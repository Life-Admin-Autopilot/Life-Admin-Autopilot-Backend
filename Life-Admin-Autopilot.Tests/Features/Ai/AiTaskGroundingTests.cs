using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <see cref="TaskGrounding"/> against <b>Node's own output</b>.
///
/// <para>
/// The expected lines below are lifted verbatim from the <c>=== MY TASKS ===</c>
/// block that <c>server/src/modules/ai/contextBuilder.ts</c> produced for the seeded
/// demo account, read out of the isolated parity Mongo. The documents here are the
/// same rows, field for field — so this asserts the renderer against the reference
/// rather than against a re-reading of the TypeScript.
/// </para>
///
/// <para>
/// The separators look decorative and are not. <c>[task:&lt;id&gt;]</c> and
/// <c>&lt;subtask:&lt;id&gt;&gt;</c> are how the agent is told to source a verbatim id
/// for <c>updateTask</c> / <c>completeTask</c> / <c>toggleSubtask</c>; an id it cannot
/// copy is an id it invents.
/// </para>
/// </summary>
public sealed class AiTaskGroundingTests
{
    [Fact]
    public void renders_an_undated_matter_exactly_as_node_does()
    {
        var task = new TaskDocument
        {
            Id = ObjectId.Parse("6a7a64a0f9aa48566160715d"),
            Title = "Fix the blocked drain in the kitchen",
            Domain = "home",
            Kind = "list",
            Status = "open",
            Priority = "normal",
            Tags = ["repair"],
        };

        // 'normal' is absent from the line: it is the default and carries nothing.
        Assert.Equal(
            "[task:6a7a64a0f9aa48566160715d] Fix the blocked drain in the kitchen — home — list — open — tags: repair",
            TaskGrounding.BuildTaskBlock([task]));
    }

    [Fact]
    public void renders_subtasks_and_notes_exactly_as_node_does()
    {
        var task = new TaskDocument
        {
            Id = ObjectId.Parse("6a7a64a0f9aa48566160715a"),
            Title = "Order a replacement mattress protector",
            Domain = "home",
            Kind = "list",
            Status = "open",
            Priority = "urgent",
            Tags = ["order"],
            Notes = "The reference number is in the photo on my phone.",
            Subtasks =
            [
                new SubtaskDocument
                {
                    Id = ObjectId.Parse("6a7a64a1f9aa485661607c5d"), Text = "Book the slot", Done = true,
                },
                new SubtaskDocument
                {
                    Id = ObjectId.Parse("6a7a64a1f9aa485661607c5e"),
                    Text = "Set a reminder the day before",
                    Done = true,
                },
                new SubtaskDocument
                {
                    Id = ObjectId.Parse("6a7a64a1f9aa485661607c5f"), Text = "Go", Done = false,
                },
            ],
        };

        Assert.Equal(
            """
            [task:6a7a64a0f9aa48566160715a] Order a replacement mattress protector — home — list — open — urgent — tags: order
                [x] <subtask:6a7a64a1f9aa485661607c5d> Book the slot
                [x] <subtask:6a7a64a1f9aa485661607c5e> Set a reminder the day before
                [ ] <subtask:6a7a64a1f9aa485661607c5f> Go
                notes: The reference number is in the photo on my phone.
            """,
            TaskGrounding.BuildTaskBlock([task]));
    }

    /// <summary>
    /// A due date reaches the agent in the USER'S zone with an explicit offset, not as
    /// the stored UTC instant.
    ///
    /// <para>
    /// <b>A DELIBERATE divergence from Node</b>, which prints <c>toISOString()</c>.
    /// Recorded in <c>docs/DIVERGENCES.md</c>. Every <c>dueAt</c> the agent could see was
    /// a <c>Z</c> instant while <c>CURRENT DATE</c> beside it carried the user's offset,
    /// and no prompt rule converts one to the other before an hour is read back. The
    /// agent usually manages the conversion anyway — but the transcript where it read a
    /// time back perfectly turned out to be reading its OWN earlier sentence in the same
    /// thread, not the data, and a pre-existing matter has no such sentence.
    /// </para>
    /// </summary>
    [Fact]
    public void renders_a_due_date_in_the_users_zone_with_an_explicit_offset()
    {
        var task = new TaskDocument
        {
            Id = ObjectId.Parse("6a7a64a0f9aa48566160711a"),
            Title = "Renew Nour’s passport",
            Domain = "family",
            Kind = "reminder",
            Status = "open",
            Priority = "urgent",
            Tags = ["passport", "documents"],
            DueAt = new DateTime(2026, 6, 23, 5, 0, 0, DateTimeKind.Utc),
        };

        Assert.Equal(
            "[task:6a7a64a0f9aa48566160711a] Renew Nour’s passport — due 2026-06-23T08:00:00+03:00 — family — reminder — open — urgent — tags: passport, documents",
            TaskGrounding.BuildTaskBlock([task], "Africa/Cairo"));

        // No zone is UTC — still offset-bearing, never a bare instant, matching
        // DateGrounding's own fallback so the clock and the matters agree.
        Assert.Contains(
            "— due 2026-06-23T05:00:00+00:00 —",
            TaskGrounding.BuildTaskBlock([task]));
    }

    [Fact]
    public void joins_several_matters_with_a_single_newline()
    {
        var block = TaskGrounding.BuildTaskBlock([Minimal("a"), Minimal("b")]);

        Assert.Equal(2, block.Split('\n').Length);
    }

    [Fact]
    public void an_empty_backlog_says_so_rather_than_going_blank()
    {
        // The agent must be able to tell "nothing open" from "no block was sent". An
        // empty string is indistinguishable from the wiring being broken, which is
        // the failure this whole port exists to remove.
        Assert.Equal("(no open tasks)", TaskGrounding.BuildTaskBlock([]));
        Assert.Equal(TaskGrounding.NoTasks, TaskGrounding.BuildTaskBlock([]));
    }

    [Theory]
    [InlineData(240, false)]
    [InlineData(241, true)]
    public void notes_are_cut_at_240_characters_and_marked_when_cut(int length, bool expectEllipsis)
    {
        // notes.slice(0, 240) followed by '…' only when the original was longer.
        // The boundary is exact in the reference, so it is exact here.
        var task = Minimal("x");
        task.Notes = new string('n', length);

        var line = TaskGrounding.BuildTaskBlock([task]).Split('\n')[1];

        Assert.Equal(
            "    notes: " + new string('n', Math.Min(length, TaskGrounding.NotesHead)) + (expectEllipsis ? "…" : string.Empty),
            line);
    }

    [Fact]
    public void the_cap_is_the_reference_cap()
    {
        // Read from Node's TASK_CAP, not chosen here. The seeded demo account holds
        // 142 open matters; uncapped, the block is ~22KB of prompt on every turn.
        Assert.Equal(20, TaskGrounding.TaskCap);
        Assert.Equal(["open", "snoozed"], TaskGrounding.PromptStatuses);
    }

    private static TaskDocument Minimal(string title) => new()
    {
        Id = ObjectId.GenerateNewId(),
        Title = title,
        Domain = "home",
        Kind = "list",
        Status = "open",
        Priority = "normal",
    };
}
