using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// The patch a resolved question applies to its task. Every member is optional and
/// a <c>null</c> means "absent from the patch" — never "clear the field".
/// </summary>
/// <param name="Kind">
/// Only ever <c>"reminder"</c>, and only when <paramref name="DueAt"/> is present.
/// A confirmed date is the whole point of asking, so a task whose reminder was
/// WITHHELD on an uncertain high-stakes guess starts firing once the user confirms.
/// </param>
/// <param name="Amount">
/// What the matter costs, when the answer settled that. Only the custom branch
/// produces it — a tapped option never carries money — and it is already a
/// normalised <c>MoneyDocument</c>, stamped <c>source: "user"</c> because a figure
/// typed into an answer is the user's own, not the model's guess.
/// </param>
public readonly record struct ClarificationTaskPatch(
    string? Title = null,
    string? Notes = null,
    DateTime? DueAt = null,
    string? Kind = null,
    string? Domain = null,
    string? Priority = null,
    MoneyDocument? Amount = null)
{
    public bool IsEmpty =>
        Title is null && Notes is null && DueAt is null && Kind is null
        && Domain is null && Priority is null && Amount is null;
}

/// <summary>
/// The slice of <c>runTool({name: 'updateTask'})</c> that
/// <c>POST /me/clarifications/{id}/resolve</c> can actually reach —
/// <c>server/src/modules/ai/toolRunner.ts</c> <c>runUpdate</c>.
///
/// <para>
/// <b>Deliberately not <c>TaskWriteService.PatchAsync</c>.</b> That is the port of
/// <c>PATCH /me/tasks/{id}</c>, which increments <c>rescheduleCount</c> when a due
/// date moves back (<c>routes/me.tasks.ts:657</c>). <c>runUpdate</c> is the ONLY
/// other task-update path in Node and it does no such thing — routing resolve
/// through the PATCH service would silently inflate the slip counter that drives
/// "what's slipping" and the reminder-fatigue nudge.
/// </para>
///
/// <para>
/// <b>Deliberately narrow.</b> <c>runUpdate</c> also handles tags, status/completedAt
/// and the guarded estimate write. None of those is reachable from this route: the
/// option branch produces only title/notes/dueAt, and the custom branch
/// (CustomAnswerInterpreter) adds domain/priority and the AMOUNT. The full tool
/// runner belongs to the AI slice; speculating the other fields here would ship
/// untested code and a second copy for that slice to reconcile.
/// </para>
///
/// <para>
/// <b>Amount is here because the agent now asks for it.</b> A money matter with no
/// figure raises a 'detail' question, and the whole point of asking is that the
/// answer lands on the matter — without this field the question was a dead end: the
/// user typed 730, the question closed, and the matter was still worth nothing.
/// </para>
/// </summary>
public sealed class ClarificationTaskUpdater
{
    private readonly TaskRepository _tasks;
    private readonly ReminderPlanner _reminders;

    public ClarificationTaskUpdater(TaskRepository tasks, ReminderPlanner reminders)
    {
        _tasks = tasks;
        _reminders = reminders;
    }

    /// <summary>
    /// <c>validateToolArgs('updateTask', args)</c> for the fields this route can
    /// produce, then <c>runUpdate</c>.
    /// </summary>
    public async Task<TaskDocument> RunUpdateAsync(
        ObjectId userId,
        ObjectId taskId,
        ClarificationTaskPatch patch,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(patch);

        var set = new BsonDocument();

        if (validated.Title is not null)
        {
            set["title"] = validated.Title;
        }

        if (validated.Kind is not null)
        {
            set["kind"] = validated.Kind;
        }

        if (validated.DueAt.HasValue)
        {
            // Node passes this through `normalizeLocalIso(iso, tz)`. The value always
            // originates as `Date#toISOString()`, which carries an explicit `Z`, so
            // that call takes its `hasOffset` branch and returns the SAME instant —
            // picking an option can never shift the date. There is no naive-ISO path
            // into this route; the branch that could produce one is the AI branch,
            // which 503s here.
            set["dueAt"] = validated.DueAt.Value;
        }

        if (validated.Domain is not null)
        {
            set["domain"] = validated.Domain;
        }

        if (validated.Priority is not null)
        {
            set["priority"] = validated.Priority;
        }

        if (validated.Notes is not null)
        {
            // `runUpdate` would $unset an empty string, but the option branch only
            // ever puts a TRUTHY `notes` in the patch, so the clearing branch is
            // unreachable from this route.
            set["notes"] = validated.Notes;
        }

        if (validated.Amount is { } money)
        {
            // Written as the embedded shape the document stores, not through the
            // PATCH route's reader: this path deliberately bypasses
            // TaskWriteService (see the type remarks) and must not start
            // incrementing rescheduleCount on the way past.
            set["amount"] = new BsonDocument
            {
                ["amountMinor"] = money.AmountMinor,
                ["currency"] = money.Currency,
                ["source"] = money.Source,
                ["direction"] = money.Direction,
            };
        }

        TaskDocument? task;
        if (set.ElementCount > 0)
        {
            // Mongoose's `timestamps: true` stamps `updatedAt` into the `$set` of a
            // findOneAndUpdate. The .NET driver does not, so it goes in by hand.
            set["updatedAt"] = now;

            task = await _tasks
                .UpdateLiveAsync(userId, taskId, new BsonDocument("$set", set), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // An empty mutation is a driver error, and there is nothing to write:
            // read the row back instead. Reached when the picked option carries only
            // a label and the draft has no dueAt. `updatedAt` is NOT bumped.
            task = await _tasks.FindLiveAsync(userId, taskId, cancellationToken).ConfigureAwait(false);
        }

        if (task is null)
        {
            throw TaskGone();
        }

        // dueAt moved, or the task just BECAME a reminder (a withheld date being
        // confirmed) → regenerate the schedule from the deadline that now stands.
        if ((validated.DueAt.HasValue || validated.Kind == "reminder")
            && task.Kind == "reminder"
            && task.DueAt.HasValue)
        {
            await _reminders.SetRulesRemindersAsync(task, now, cancellationToken).ConfigureAwait(false);
        }

        return task;
    }

    /// <summary>
    /// <c>updateTaskArgs</c> for the reachable fields. A failure is
    /// <c>AppError(400, 'invalid_tool_args', JSON.stringify(flatten()))</c> — note the
    /// flattened detail object is the MESSAGE, not <c>details</c>, which is what
    /// <c>validateToolArgs</c> does.
    ///
    /// <para>
    /// Only a pathological stored option trips this (a whitespace-only title, a
    /// title past 240 or notes past 2000 — the Clarification schema constrains
    /// neither). zod reports every issue at once, so the checks accumulate.
    /// </para>
    /// </summary>
    private static ClarificationTaskPatch Validate(ClarificationTaskPatch patch)
    {
        var issues = new List<ValidationIssue>();

        // `z.string().trim().min(1).max(240)` — trim FIRST, so the length checks see
        // the trimmed value and the trimmed value is what gets written.
        string? title = null;
        if (patch.Title is not null)
        {
            title = patch.Title.Trim();
            if (title.Length < 1)
            {
                issues.Add(ValidationIssue.At("title", ZodMessages.TooShort(1)));
            }
            else if (title.Length > 240)
            {
                issues.Add(ValidationIssue.At("title", ZodMessages.TooLong(240)));
            }
        }

        // `z.string().max(2000)` — no trim, no minimum.
        if (patch.Notes is { Length: > 2000 })
        {
            issues.Add(ValidationIssue.At("notes", ZodMessages.TooLong(2000)));
        }

        if (issues.Count > 0)
        {
            throw new AppException(
                400,
                "invalid_tool_args",
                System.Text.Json.JsonSerializer.Serialize(ValidationDetails.AsFlattened(issues)));
        }

        return patch with { Title = title };
    }

    /// <summary>
    /// <c>runUpdate</c>'s 404. Only reachable as a race — the route loaded the task
    /// moments earlier — but ported so the window behaves identically.
    /// </summary>
    public static AppException TaskGone() =>
        AppException.NotFound(
            "task_not_found",
            "That matter could not be found — it may already have been removed.");
}
