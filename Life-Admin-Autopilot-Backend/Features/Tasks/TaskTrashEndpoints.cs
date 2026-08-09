using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// Trash: list, empty, and restore-one.
///
/// <para>None of these apply the i18n overlay — the raw <c>toJSON</c> goes out,
/// <c>i18n</c> included.</para>
/// </summary>
internal static class TaskTrashEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me/tasks/trash", async (
            HttpContext ctx,
            TaskRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var tasks = await repository.FindTrashedAsync(user.Id, ct).ConfigureAwait(false);
            return Results.Ok(new { tasks = tasks.Select(t => t.ToDto()).ToList() });
        }).RequireAuthorization();

        endpoints.MapDelete("/me/tasks/trash", async (
            HttpContext ctx,
            TaskRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            // The one genuinely irreversible operation in the app. Reachable only
            // from an explicit "Empty trash" in the Trash view, never from a filter.
            var purged = await repository.PurgeTrashAsync(user.Id, ct).ConfigureAwait(false);
            return Results.Ok(new { purged });
        }).RequireAuthorization();

        endpoints.MapPost("/me/tasks/{id}/restore", async (
            string id,
            HttpContext ctx,
            TaskRepository repository,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();
            var taskId = TaskEndpoints.TaskId(id);

            var task = await repository.RestoreAsync(user.Id, taskId, ct).ConfigureAwait(false)
                ?? throw TaskEndpoints.TaskNotFound();

            return Results.Ok(new { task = task.ToDto() });
        }).RequireAuthorization();
    }
}
