using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Activity;
using Microsoft.AspNetCore.Mvc;

namespace Life_Admin_Autopilot_Backend.Features.Admin;

/// <summary>
/// The live activity feed.
///
/// <para>
/// Server-sent events rather than a websocket: the traffic is one-directional,
/// SSE reconnects on its own, and it is a plain HTTP response so it inherits the
/// console's auth and proxying without a second story for either.
/// </para>
/// </summary>
public static class AdminActivityEndpoints
{
    /// <summary>
    /// Written as an SSE COMMENT, which every parser ignores. It exists to outlive
    /// proxy idle timeouts — nginx defaults to 60 s — on a feed that is silent
    /// most of the time.
    /// </summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions FrameJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapAdminActivityEndpoints(this IEndpointRouteBuilder console)
    {
        // The backfill, as an ordinary JSON read. Useful on its own for a client
        // that would rather poll than hold a connection open.
        console.MapGet("/activity/recent", (
            [FromQuery] int? limit,
            IAdminActivityBus bus) =>
            Results.Ok(bus.Recent(limit ?? 25)));

        console.MapGet("/activity/stream", async (
            HttpContext context,
            IAdminActivityBus bus,
            CancellationToken cancellationToken) =>
        {
            var response = context.Response;

            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache, no-transform";

            // Without this nginx buffers the whole stream and the feed appears to
            // work in development and never update in production.
            response.Headers["X-Accel-Buffering"] = "no";

            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Subscribe BEFORE writing the backfill. The other order has a hole: an
            // event published between reading the backlog and subscribing belongs to
            // neither, and vanishes.
            var reader = bus.Subscribe(cancellationToken);

            foreach (var activity in bus.Recent(25))
            {
                await WriteAsync(response, activity, cancellationToken).ConfigureAwait(false);
            }

            var heartbeat = Task.CompletedTask;
            var lastSequence = 0L;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for either an event or the heartbeat interval, whichever
                    // comes first, so a quiet feed still writes often enough to stay
                    // open and a busy one is never delayed by the timer.
                    var next = reader.WaitToReadAsync(cancellationToken).AsTask();
                    heartbeat = Task.Delay(Heartbeat, cancellationToken);

                    var winner = await Task.WhenAny(next, heartbeat).ConfigureAwait(false);

                    if (winner == heartbeat)
                    {
                        await WriteRawAsync(
                            response,
                            $": ping {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n\n",
                            cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    if (!await next.ConfigureAwait(false))
                    {
                        break;
                    }

                    while (reader.TryRead(out var activity))
                    {
                        // The backfill and the live stream overlap by design (see the
                        // subscribe-first note above), so a duplicate is expected
                        // rather than exceptional. Dropping it here keeps the client
                        // from having to.
                        if (activity.Sequence <= lastSequence)
                        {
                            continue;
                        }

                        lastSequence = activity.Sequence;
                        await WriteAsync(response, activity, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The console navigated away or closed the tab. Not a failure.
            }
        });

        return console;
    }

    private static Task WriteAsync(
        HttpResponse response,
        AdminActivityEvent activity,
        CancellationToken cancellationToken) =>
        WriteRawAsync(
            response,
            $"data: {JsonSerializer.Serialize(activity, FrameJson)}\n\n",
            cancellationToken);

    /// <summary>
    /// One frame, one write, then flush. A frame split across two writes can be
    /// delivered as two chunks and a reader would parse half a JSON object.
    /// </summary>
    private static async Task WriteRawAsync(
        HttpResponse response,
        string frame,
        CancellationToken cancellationToken)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
