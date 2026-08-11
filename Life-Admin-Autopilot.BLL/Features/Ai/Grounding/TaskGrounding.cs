using System.Text;
using Life_Admin_Autopilot.BLL.Kernel.Json;
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
    /// </summary>
    public static string BuildTaskBlock(IReadOnlyList<TaskDocument> tasks)
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

            AppendTask(block, task);
        }

        return block.ToString();
    }

    private static void AppendTask(StringBuilder block, TaskDocument task)
    {
        block.Append("[task:").Append(task.Id).Append("] ").Append(task.Title);

        if (task.DueAt is { } dueAt)
        {
            // Date#toISOString() — three fractional digits and a literal Z. The
            // parity converter owns that format; re-typing it here is how the two
            // eventually disagree.
            block.Append(" — due ").Append(JsIsoDateTimeConverter.ToIso(dueAt));
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
