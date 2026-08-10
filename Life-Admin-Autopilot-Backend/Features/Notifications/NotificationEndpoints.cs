using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Features.Notifications;

/// <summary>
/// Ports <c>server/src/routes/me.notifications.ts</c> — the in-app feed and the
/// mark-read write.
///
/// <para>
/// Neither route is rate limited and neither has a query schema: unknown query
/// parameters are ignored rather than rejected (verified live —
/// <c>GET /me/notifications?bogus=1</c> answers 200). That is the opposite of the
/// Matters routes, so <c>QueryReader</c> is deliberately absent here.
/// </para>
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/notifications — feed + unread count, issued as one round trip each.
        endpoints.MapGet("/me/notifications", async (
            HttpContext context,
            NotificationRepository notifications,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // Node runs these under Promise.all. Sequential here is the same two
            // queries against the same snapshot-free collection — the count is taken
            // independently of the page in both servers, so a row arriving between
            // them can already make the two disagree. Not a difference worth adding
            // concurrency for.
            var feed = await notifications.FindFeedAsync(caller.Id, cancellationToken);
            var unreadCount = await notifications.CountUnreadAsync(caller.Id, cancellationToken);

            return Results.Ok(new NotificationFeedResponse
            {
                Notifications = feed.Select(n => n.ToDto()).ToList(),
                UnreadCount = unreadCount,
            });
        })
        .RequireAuthorization();

        // POST /me/notifications/read — the one .parse() route in the API.
        endpoints.MapPost("/me/notifications/read", async (
            HttpContext context,
            NotificationRepository notifications,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var ids = NotificationsReadRequest.Parse(await ReadBodyRootAsync(context, cancellationToken));

            await notifications.MarkReadAsync(caller.Id, ids, DateTime.UtcNow, cancellationToken);

            // Recounted after the write rather than derived from the update result —
            // the client renders this number on the bell badge, and `matchedCount`
            // would be wrong for any row that was already read.
            var unreadCount = await notifications.CountUnreadAsync(caller.Id, cancellationToken);

            return Results.Ok(new NotificationsReadResponse { UnreadCount = unreadCount });
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// The body as a raw JSON root, with express's semantics.
    ///
    /// <para>
    /// Deliberately NOT <c>KernelBody.ReadAsync&lt;T&gt;</c>: that helper renders
    /// every failure through the <c>{formErrors, fieldErrors}</c> mapper, which is
    /// the wrong one of the three shapes for this route, and it answers 400 for a
    /// top-level primitive where express answers 500. It shares the same
    /// <c>ReadBytesAsync</c> ceiling, so the 256kb → 500 rule still holds.
    /// </para>
    ///
    /// <para>
    /// The empty-body and primitive cases are both express's, verified live:
    /// no body at all is <c>req.body = {}</c> and answers 200, while
    /// <c>42</c>, <c>"x"</c> and <c>null</c> are rejected by body-parser's strict
    /// mode as a SyntaxError and fall through <c>errorHandler.ts</c> to a
    /// <b>500</b> — not a 400.
    /// </para>
    /// </summary>
    private static async Task<JsonElement> ReadBodyRootAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var bytes = await KernelBody.ReadBytesAsync(context.Request, KernelJson.MaxJsonBodyBytes, cancellationToken);

        if (bytes.Length == 0)
        {
            return EmptyObject;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new BodyReadException("malformed JSON body", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                // express.json()'s `strict: true` accepts only objects and arrays.
                throw new BodyReadException("body-parser strict mode: root must be an object or array");
            }

            // JsonDocument owns its buffer, so hand back a detached copy.
            return root.Clone();
        }
    }

    /// <summary>Node's <c>req.body ?? {}</c>, as a reusable root element.</summary>
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
}
