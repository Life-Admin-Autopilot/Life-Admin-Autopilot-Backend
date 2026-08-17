using System.Net;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Activity;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>The bus itself — no HTTP, no database.</summary>
public sealed class AdminActivityBusTests
{
    [Fact]
    public void a_subscriber_receives_what_is_published_after_it_subscribes()
    {
        var bus = new AdminActivityBus();
        using var cts = new CancellationTokenSource();

        var reader = bus.Subscribe(cts.Token);
        bus.Publish(AdminActivityKind.Signup, "someone joined");

        Assert.True(reader.TryRead(out var activity));
        Assert.Equal(AdminActivityKind.Signup, activity.Kind);
        Assert.Equal("someone joined", activity.Summary);
    }

    /// <summary>Sequence numbers must be strictly increasing — the client dedupes on them.</summary>
    [Fact]
    public void sequence_numbers_increase_and_never_repeat()
    {
        var bus = new AdminActivityBus();
        using var cts = new CancellationTokenSource();
        var reader = bus.Subscribe(cts.Token);

        for (var i = 0; i < 20; i++)
        {
            bus.Publish(AdminActivityKind.AiTurn, $"turn {i}");
        }

        var seen = new List<long>();
        while (reader.TryRead(out var activity))
        {
            seen.Add(activity.Sequence);
        }

        Assert.Equal(20, seen.Count);
        Assert.Equal(seen.OrderBy(s => s), seen);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    /// <summary>Every subscriber gets its own copy — one reading does not consume another's.</summary>
    [Fact]
    public void two_subscribers_both_receive_every_event()
    {
        var bus = new AdminActivityBus();
        using var cts = new CancellationTokenSource();

        var first = bus.Subscribe(cts.Token);
        var second = bus.Subscribe(cts.Token);

        bus.Publish(AdminActivityKind.AiTurn, "one turn");

        Assert.True(first.TryRead(out _));
        Assert.True(second.TryRead(out _));
    }

    /// <summary>
    /// <b>A console that falls behind loses the OLDEST events, never the newest.</b>
    ///
    /// <para>
    /// The alternative — dropping writes once full — freezes the feed at the moment
    /// it fell behind, which looks exactly like the product going quiet. Losing the
    /// tail is honest; losing the head is misleading.
    /// </para>
    /// </summary>
    [Fact]
    public void a_slow_subscriber_loses_the_oldest_not_the_newest()
    {
        var bus = new AdminActivityBus();
        using var cts = new CancellationTokenSource();
        var reader = bus.Subscribe(cts.Token);

        var overflow = AdminActivityBus.SubscriberCapacity + 30;
        for (var i = 0; i < overflow; i++)
        {
            bus.Publish(AdminActivityKind.AiTurn, $"turn {i}");
        }

        var received = new List<AdminActivityEvent>();
        while (reader.TryRead(out var activity))
        {
            received.Add(activity);
        }

        Assert.Equal(AdminActivityBus.SubscriberCapacity, received.Count);

        // The LAST published event survived; the first did not.
        Assert.Equal($"turn {overflow - 1}", received[^1].Summary);
        Assert.DoesNotContain(received, a => a.Summary == "turn 0");
    }

    /// <summary>A cancelled subscription is removed, so the bus does not leak channels.</summary>
    [Fact]
    public void cancelling_a_subscription_removes_it()
    {
        var bus = new AdminActivityBus();
        var cts = new CancellationTokenSource();

        bus.Subscribe(cts.Token);
        Assert.Equal(1, bus.SubscriberCount);

        cts.Cancel();
        Assert.Equal(0, bus.SubscriberCount);

        // And publishing afterwards must not throw into the caller.
        bus.Publish(AdminActivityKind.AiTurn, "after everyone left");
    }

    /// <summary>
    /// The backlog is what a newly-connected console sees, so it must be bounded
    /// and must keep the most RECENT events.
    /// </summary>
    [Fact]
    public void the_backlog_is_bounded_and_keeps_the_newest()
    {
        var bus = new AdminActivityBus();

        for (var i = 0; i < AdminActivityBus.BacklogSize + 25; i++)
        {
            bus.Publish(AdminActivityKind.AiTurn, $"turn {i}");
        }

        var recent = bus.Recent(1000);

        Assert.Equal(AdminActivityBus.BacklogSize, recent.Count);
        Assert.Equal($"turn {AdminActivityBus.BacklogSize + 24}", recent[^1].Summary);

        // Oldest first, so a feed can append them in order.
        Assert.Equal(recent.OrderBy(r => r.Sequence), recent);
    }

    [Fact]
    public void recent_returns_at_most_what_was_asked_for()
    {
        var bus = new AdminActivityBus();

        for (var i = 0; i < 20; i++)
        {
            bus.Publish(AdminActivityKind.AiTurn, $"turn {i}");
        }

        Assert.Equal(5, bus.Recent(5).Count);
        Assert.Equal("turn 19", bus.Recent(5)[^1].Summary);
    }

    /// <summary>
    /// Publish is on the hot path of real requests. It must never throw, whatever
    /// it is handed.
    /// </summary>
    [Fact]
    public void publish_never_throws()
    {
        var bus = new AdminActivityBus();

        var recorded = Record.Exception(() =>
        {
            bus.Publish(string.Empty, string.Empty);
            bus.Publish(AdminActivityKind.AiTurn, new string('x', 100_000));
        });

        Assert.Null(recorded);
    }
}

/// <summary>The HTTP surface: the backfill read and the SSE stream.</summary>
[Collection("admin-serial")]
public sealed class AdminActivityStreamTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminActivityStreamTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task the_stream_requires_a_console_token()
    {
        var response = await _factory.CreateApiClient().GetAsync("/admin/activity/stream");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task the_backfill_requires_a_console_token()
    {
        var response = await _factory.CreateApiClient().GetAsync("/admin/activity/recent");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A real admin action lands on the feed. This is the end-to-end wire: the
    /// audit store publishes, the singleton bus holds it, the backfill reads it.
    /// </summary>
    [Fact]
    public async Task an_admin_action_appears_on_the_feed()
    {
        if (!_factory.MongoIsUp()) return;

        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "users", "adminauditevents");
        var userId = await AdminTestData.SeedUserAsync(db, "watched@test.local");

        var client = _factory.AdminClient();

        await client.PostAsync(
            $"/admin/customers/{userId}/suspend",
            AdminTestData.Json(new { reason = "Appears on the live feed" }));

        var json = await AdminTestData.ReadAsync(await client.GetAsync("/admin/activity/recent?limit=25"));
        var events = json.EnumerateArray().ToList();

        Assert.Contains(events, e =>
            e.GetProperty("kind").GetString() == AdminActivityKind.AdminAction
            && e.GetProperty("summary").GetString()!.Contains("customer suspended"));

        // The reason travels with it, so the feed says WHY without a second lookup.
        Assert.Contains(events, e =>
            e.TryGetProperty("detail", out var detail)
            && detail.GetString() == "Appears on the live feed");
    }

    /// <summary>
    /// The stream opens with SSE headers and immediately writes the backfill, so a
    /// console that connects to a quiet system still has something to render.
    /// </summary>
    [Fact]
    public async Task the_stream_opens_with_sse_headers_and_a_backfill()
    {
        if (!_factory.MongoIsUp()) return;

        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "users", "adminauditevents");
        var userId = await AdminTestData.SeedUserAsync(db, "streamed@test.local");
        var client = _factory.AdminClient();

        // Produce something for the backfill to carry.
        await client.PostAsync(
            $"/admin/customers/{userId}/suspend",
            AdminTestData.Json(new { reason = "Something for the backfill" }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync(
            "/admin/activity/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString() ?? string.Empty);

        // X-Accel-Buffering is the one that matters operationally: without it nginx
        // buffers the whole stream and the feed silently never updates in production.
        Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out var buffering));
        Assert.Equal("no", buffering.Single());

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var frame = await ReadOneFrameAsync(reader, cts.Token);

        Assert.StartsWith("data: ", frame);

        var payload = JsonDocument.Parse(frame["data: ".Length..]).RootElement;
        Assert.Equal(AdminActivityKind.AdminAction, payload.GetProperty("kind").GetString());
        Assert.True(payload.GetProperty("sequence").GetInt64() > 0);

        cts.Cancel();
    }

    /// <summary>
    /// An event published while the stream is open reaches it live — the whole
    /// point of the feature.
    /// </summary>
    [Fact]
    public async Task an_event_published_while_connected_arrives_live()
    {
        if (!_factory.MongoIsUp()) return;

        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "users", "adminauditevents");
        var userId = await AdminTestData.SeedUserAsync(db, "live@test.local");
        var client = _factory.AdminClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await client.GetAsync(
            "/admin/activity/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        await client.PostAsync(
            $"/admin/customers/{userId}/suspend",
            AdminTestData.Json(new { reason = "Published while connected" }));

        // Read PAST the backfill rather than assuming it is empty.
        //
        // The bus is a process-wide singleton and its backlog is in memory, so
        // clearing Mongo does not clear it — an earlier test's events are legitimately
        // replayed first. The first version of this test assumed an empty backlog and
        // failed on a correct product; the assumption was the bug.
        var arrived = await ReadUntilAsync(
            reader,
            payload => payload.TryGetProperty("detail", out var detail)
                && detail.GetString() == "Published while connected",
            cts.Token);

        Assert.True(arrived, "the live event never reached the open stream");

        cts.Cancel();
    }

    /// <summary>
    /// Reads frames until one satisfies <paramref name="predicate"/>, or the token
    /// trips. Bounded by the caller's timeout, so a feed that never delivers fails
    /// the test rather than hanging it.
    /// </summary>
    private static async Task<bool> ReadUntilAsync(
        StreamReader reader,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                return false;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = JsonDocument.Parse(line["data: ".Length..]).RootElement;
            if (predicate(payload))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads until a <c>data:</c> frame arrives, skipping heartbeat comments and
    /// the blank separator lines.
    /// </summary>
    private static async Task<string> ReadOneFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                throw new InvalidOperationException("the stream ended before a frame arrived");
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new OperationCanceledException("timed out waiting for a frame");
    }
}
