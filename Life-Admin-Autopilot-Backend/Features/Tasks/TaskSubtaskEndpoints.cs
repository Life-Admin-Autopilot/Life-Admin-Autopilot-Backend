using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot_Backend.Features.Tasks.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// The three subtask mutations.
///
/// <para>
/// <b>All three return the WHOLE parent task</b>, not the subtask — the client
/// re-renders the row from one payload. And all three go through
/// <c>task.save()</c>, which is why all three are the endpoints that 500 forever
/// on a reminder whose <c>dueAt</c> was cleared. See
/// <c>TaskWriteService.EnforceReminderHasDue</c>.
/// </para>
/// </summary>
internal static class TaskSubtaskEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/me/tasks/{id}/subtasks", async (
            string id,
            HttpContext ctx,
            TaskWriteService writes,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var taskId = TaskEndpoints.TaskId(id);

            var body = await KernelBody
                .ReadAsync<AddSubtaskBody>(ctx, KernelBodyOptions.Strict_("Invalid subtask payload."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            var text = f.TrimmedString(
                body.Text,
                "text",
                min: 1,
                max: TaskVocabulary.MaxSubtaskText,
                required: true);

            f.ThrowIfInvalid("invalid_body", "Invalid subtask payload.");

            var task = await writes.AddSubtaskAsync(user.Id, taskId, text!, cancellationToken: ct).ConfigureAwait(false);
            return Results.Created((string?)null, new { task = task.ToDto() });
        }).RequireAuthorization();

        endpoints.MapPatch("/me/tasks/{id}/subtasks/{subId}", async (
            string id,
            string subId,
            HttpContext ctx,
            TaskWriteService writes,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            // Node checks BOTH ids up front and reports task_not_found for either —
            // a malformed subtask id never reaches the subtask lookup.
            var taskId = TaskEndpoints.TaskId(id);
            if (!ObjectId.TryParse(subId, out var subtaskId))
            {
                throw TaskEndpoints.TaskNotFound();
            }

            var body = await KernelBody
                .ReadAsync<UpdateSubtaskBody>(ctx, KernelBodyOptions.Strict_("Invalid subtask update."), ct)
                .ConfigureAwait(false);

            var f = new BodyFields();
            var text = f.TrimmedString(
                body.Text,
                "text",
                min: 1,
                max: TaskVocabulary.MaxSubtaskText,
                required: false);
            var done = f.Bool(body.Done, "done");

            // The schema's `.refine(...)` — an empty patch is a form-level issue,
            // not a field one.
            if (BodyFields.IsAbsent(body.Text) && BodyFields.IsAbsent(body.Done))
            {
                f.AddFormIssue("must include text or done");
            }

            f.ThrowIfInvalid("invalid_body", "Invalid subtask update.");

            var task = await writes
                .UpdateSubtaskAsync(user.Id, taskId, subtaskId, text, done, cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Ok(new { task = task.ToDto() });
        }).RequireAuthorization();

        endpoints.MapDelete("/me/tasks/{id}/subtasks/{subId}", async (
            string id,
            string subId,
            HttpContext ctx,
            TaskWriteService writes,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var taskId = TaskEndpoints.TaskId(id);
            if (!ObjectId.TryParse(subId, out var subtaskId))
            {
                throw TaskEndpoints.TaskNotFound();
            }

            var task = await writes
                .DeleteSubtaskAsync(user.Id, taskId, subtaskId, cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Ok(new { task = task.ToDto() });
        }).RequireAuthorization();
    }
}
