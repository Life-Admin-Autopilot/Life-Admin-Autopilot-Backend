using System.Globalization;
using System.Text;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Grounding;

/// <summary>
/// Port of the <c>=== MY TASKS ===</c> block in
/// <c>server/src/modules/ai/contextBuilder.ts</c>: the user's existing open matters,
/// rendered with their REAL ids so the agent can reference one instead of creating a
/// second copy of it.
///
/// <para>
/// <b>The cap is the whole reason this is a separate type.</b> Node caps the block at
/// <see cref="TaskCap"/> rows and so does this. The seeded demo account holds 142 open
/// matters; rendering all of them is ~22KB of prompt on every single turn, which
/// crowds out the system prompt and degrades the answer without producing an error
/// anywhere. Twenty rows is ~3KB — measured against the same account.
/// </para>
///
/// <para>
/// <b>A capped list is not a census, and the flow's input block says so.</b> The
/// hazard a truncated list introduces is the model confidently answering "you have
/// nothing like that" from a 20-row window over 142 matters. The prompt text that
/// ships alongside this block tells the agent the list is capped and to reach for
/// <c>queryTasks</c> when it needs certainty.
/// </para>
///
/// <para>
/// Every literal below — the separators, the subtask markers, the 240-character notes
/// head and its ellipsis — is asserted against the block Node itself produced for the
/// seeded account, not authored from the TypeScript by eye.
/// </para>
/// </summary>
public static class TaskGrounding
{
    /// <summary><c>TASK_CAP</c>. Read from the reference, not chosen here.</summary>
    public const int TaskCap = 20;

    /// <summary>How much of a matter's notes reaches the prompt before the ellipsis.</summary>
    public const int NotesHead = 240;

    /// <summary>What the block says when the user has nothing open.</summary>
    public const string NoTasks = "(no open tasks)";

    /// <summary>The two statuses Node considers "still live" for prompt purposes.</summary>
    public static readonly IReadOnlyList<string> PromptStatuses = new[] { "open", "snoozed" };

    /// <summary>
    /// Render the block body. Callers supply an ALREADY-CAPPED, already-sorted list —
    /// the cap belongs in the query, so a 142-matter account never materialises 142
    /// documents to throw 122 away.
    ///
    /// <para>
    /// <paramref name="timezone"/> is the caller's IANA zone, used only to render
    /// <c>dueAt</c>. Absent or unrecognised falls back to UTC, matching
    /// <see cref="DateGrounding"/>'s own fallback so the two blocks never disagree
    /// about what hour it is.
    /// </para>
    /// </summary>
    public static string BuildTaskBlock(IReadOnlyList<TaskDocument> tasks, string? timezone = null)
    {
        if (tasks.Count == 0)
        {
            return NoTasks;
        }

        var block = new StringBuilder();

        foreach (var task in tasks)
        {
            if (block.Length > 0)
            {
                block.Append('\n');
            }

            AppendTask(block, task, timezone);
        }

        return block.ToString();
    }

    /// <summary>
    /// <c>dueAt</c> in the user's own zone with an explicit offset —
    /// <c>2026-08-28T12:00:00+03:00</c> — rather than the stored UTC instant.
    ///
    /// <para>
    /// <b>Why this changed.</b> Every <c>dueAt</c> the agent could see, here and in tool
    /// results, was a <c>Z</c> instant, while <c>CURRENT DATE</c> beside it carried the
    /// user's offset. Nothing in the flow's prompt tells the agent to convert one into
    /// the other before reading an hour back to the user. It usually manages anyway —
    /// verified live: given three matters at <c>09:00Z</c>, <c>14:00Z</c> and
    /// <c>18:30Z</c> for a Cairo user it answered 12:00 PM, 5:00 PM and 9:30 PM, all
    /// correct — but "usually manages" is not a guarantee, and the one transcript where
    /// it read a time back perfectly turned out to be reading it off its OWN earlier
    /// sentence in the same thread, not off the data. A pre-existing matter has no such
    /// sentence to lean on.
    /// </para>
    ///
    /// <para>
    /// Rendering the offset removes the conversion from the model's job entirely. It is
    /// the same reasoning <see cref="DateGrounding.FormatNow"/> already records for the
    /// clock: hand the agent a bare instant and it invents an offset rather than
    /// complaining.
    /// </para>
    ///
    /// <para>
    /// <b>Deliberate divergence from the Node reference</b>, which prints
    /// <c>toISOString()</c>. Recorded in <c>docs/DIVERGENCES.md</c>.
    /// </para>
    /// </summary>
    public static string FormatDue(DateTime dueAt, string? timezone)
    {
        var utc = DateTime.SpecifyKind(dueAt, DateTimeKind.Utc);

        if (!ImportedTimeResolver.IsValidTimeZone(timezone))
        {
            // Same fallback as DateGrounding: self-consistent UTC, offset still
            // explicit, never a bare instant.
            return new DateTimeOffset(utc)
                .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone!);

        return TimeZoneInfo
            .ConvertTime(new DateTimeOffset(utc), zone)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static void AppendTask(StringBuilder block, TaskDocument task, string? timezone)
    {
        block.Append("[task:").Append(task.Id).Append("] ").Append(task.Title);

        if (task.DueAt is { } dueAt)
        {
            block.Append(" — due ").Append(FormatDue(dueAt, timezone));
        }

        block.Append(" — ").Append(task.Domain);

        if (!string.IsNullOrEmpty(task.Kind))
        {
            block.Append(" — ").Append(task.Kind);
        }

        block.Append(" — ").Append(task.Status);

        // 'normal' is the default and carries no information; Node omits it.
        if (!string.IsNullOrEmpty(task.Priority) && task.Priority != "normal")
        {
            block.Append(" — ").Append(task.Priority);
        }

        if (task.Tags is { Count: > 0 })
        {
            block.Append(" — tags: ").Append(string.Join(", ", task.Tags));
        }

        foreach (var subtask in task.Subtasks ?? [])
        {
            block
                .Append("\n    ")
                .Append(subtask.Done ? "[x]" : "[ ]")
                .Append(" <subtask:")
                .Append(subtask.Id)
                .Append("> ")
                .Append(subtask.Text);
        }

        if (!string.IsNullOrEmpty(task.Notes))
        {
            var head = task.Notes.Length > NotesHead ? task.Notes[..NotesHead] : task.Notes;
            block.Append("\n    notes: ").Append(head);

            if (task.Notes.Length > NotesHead)
            {
                block.Append('…');
            }
        }
    }
}
