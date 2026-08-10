using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.Notifications;

/// <summary>
/// Ports <c>server/src/routes/me.reminders.ts</c>.
///
/// <para>
/// <b>This route is the delivery mechanism on iOS, not a convenience.</b>
/// Reminders are planned server-side, but nothing could deliver them: the server
/// only ever wrote a Notification row, which is invisible unless the app is
/// already open, and on a free Apple developer team there is no APNs to wake it
/// with. So the schedule is handed to the device and iOS fires each entry
/// locally — no push certificate, no paid membership, works with the app closed.
/// </para>
///
/// <para>
/// Everything about the shape follows from that: the 30-day horizon covers a
/// plausible gap between visits, the 60-entry cap sits under the 64 pending
/// notifications iOS allows before it silently drops the rest, and the id is
/// deterministic so a re-sync REPLACES each pending entry rather than stacking a
/// duplicate beside it.
/// </para>
/// </summary>
public static class ReminderEndpoints
{
    public static IEndpointRouteBuilder MapReminderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/reminders/upcoming — no rate limiter, no query schema.
        endpoints.MapGet("/me/reminders/upcoming", async (
            HttpContext context,
            ReminderTaskRepository tasks,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var now = DateTime.UtcNow;
            var horizon = now.AddDays(ReminderTaskRepository.HorizonDays);

            var candidates = await tasks.FindWithUpcomingRemindersAsync(caller.Id, now, horizon, cancellationToken);

            return Results.Ok(new UpcomingRemindersResponse
            {
                Reminders = UpcomingReminderProjection.Project(candidates, now, horizon),
            });
        })
        .RequireAuthorization();

        return endpoints;
    }
}
