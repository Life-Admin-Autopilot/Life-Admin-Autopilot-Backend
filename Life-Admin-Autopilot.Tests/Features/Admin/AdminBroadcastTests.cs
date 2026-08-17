using System.Net;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// Broadcast — the highest-blast-radius action the console has.
///
/// <para>
/// The thing under test is not "does it send". It is whether the number shown to
/// the person pressing the button is the number of people who will actually
/// receive it. A broadcast cannot be recalled, so a count that under-reports is
/// worse than a failure.
/// </para>
/// </summary>
[Collection("admin-serial")]
public sealed class AdminBroadcastTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminBroadcastTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Push.Reset();
    }

    private async Task SeedManyAsync(int count)
    {
        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "users", "notifications", "deviceTokens", "adminauditevents");

        for (var i = 0; i < count; i++)
        {
            await AdminTestData.SeedUserAsync(db, $"person{i:D4}@test.local");
        }
    }

    private static object Body(string segment, string? reason = "Re-engagement campaign") => new
    {
        segment,
        title = "We missed you",
        body = "Your matters are still here whenever you want them.",
        reason,
    };

    /// <summary>
    /// <b>The count must be the true total, not a page.</b>
    ///
    /// <para>
    /// The customer search is paged at <see cref="AdminCustomerRepository.MaxTake"/>.
    /// If the broadcast reuses that path, a segment larger than one page reports
    /// the page size and silently reaches only that many people — the operator
    /// reads "220 recipients", presses send, and 4,000 customers never hear about
    /// it while the receipt claims success.
    /// </para>
    /// </summary>
    [Fact]
    public async Task the_preview_count_is_the_whole_segment_not_one_page()
    {
        if (!_factory.MongoIsUp()) return;

        var total = AdminCustomerRepository.MaxTake + 25;
        await SeedManyAsync(total);

        var response = await _factory.AdminClient()
            .GetAsync("/admin/ops/broadcast/preview?segment=all");

        var json = await AdminTestData.ReadAsync(response);

        Assert.Equal(total, json.GetProperty("recipients").GetInt32());
    }

    /// <summary>
    /// Above the cap the send is refused outright, and the message says how many
    /// matched so the operator can narrow it.
    /// </summary>
    [Fact]
    public async Task refuses_a_segment_larger_than_the_cap()
    {
        if (!_factory.MongoIsUp()) return;

        await SeedManyAsync(AdminNotificationService.MaxBroadcastRecipients + 5);

        var response = await _factory.AdminClient()
            .PostAsync("/admin/ops/broadcast", AdminTestData.Json(Body("all")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal("broadcast_too_large", json.GetProperty("error").GetProperty("code").GetString());

        // Nothing went out.
        Assert.Empty(_factory.Push.Sent);
        Assert.Equal(
            0,
            await _factory.Database()
                .GetCollection<BsonDocument>("notifications")
                .CountDocumentsAsync(new BsonDocument()));
    }

    /// <summary>Every recipient inside the cap gets a durable row.</summary>
    [Fact]
    public async Task reaches_every_recipient_in_the_segment()
    {
        if (!_factory.MongoIsUp()) return;

        await SeedManyAsync(12);

        var response = await _factory.AdminClient()
            .PostAsync("/admin/ops/broadcast", AdminTestData.Json(Body("all")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal(12, json.GetProperty("recipients").GetInt32());
        Assert.Equal(12, json.GetProperty("inAppCreated").GetInt32());

        var rows = await _factory.Database()
            .GetCollection<BsonDocument>("notifications")
            .CountDocumentsAsync(new BsonDocument("kind", AdminNotificationService.AnnouncementKind));

        Assert.Equal(12, rows);
    }

    /// <summary>
    /// One recipient blowing up must not abandon the rest. A broadcast that stops
    /// halfway is the worst outcome, because nobody can tell where it stopped.
    /// </summary>
    [Fact]
    public async Task one_failing_recipient_does_not_abort_the_broadcast()
    {
        if (!_factory.MongoIsUp()) return;

        await SeedManyAsync(6);

        var db = _factory.Database();
        var everyone = await db.GetCollection<BsonDocument>("users")
            .Find(new BsonDocument()).ToListAsync();

        // Give the third person a device whose push always throws.
        await AdminTestData.SeedDeviceAsync(db, everyone[2]["_id"].AsObjectId, "explodes");

        _factory.Push.Responder = request =>
            request.DeviceToken == "explodes"
                ? throw new InvalidOperationException("provider blew up")
                : AdminTestData.Transient();

        var response = await _factory.AdminClient()
            .PostAsync("/admin/ops/broadcast", AdminTestData.Json(Body("all")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ALL six got their durable row, including the one whose push threw.
        //
        // That is the design, not a leniency: DeliverAsync writes the notification
        // before it rings any device, so a provider that explodes costs the doorbell
        // and never the message. My first version of this test expected five —
        // the test was wrong, and the behaviour it doubted is the whole point.
        var rows = await db.GetCollection<BsonDocument>("notifications")
            .CountDocumentsAsync(new BsonDocument());

        Assert.Equal(6, rows);
    }

    [Fact]
    public async Task refuses_an_empty_segment()
    {
        if (!_factory.MongoIsUp()) return;

        await SeedManyAsync(3);

        // Nobody is suspended, so this segment matches no one.
        var response = await _factory.AdminClient()
            .PostAsync("/admin/ops/broadcast", AdminTestData.Json(Body("suspended")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal("empty_segment", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task refuses_a_broadcast_with_no_reason()
    {
        if (!_factory.MongoIsUp()) return;

        await SeedManyAsync(3);

        var response = await _factory.AdminClient()
            .PostAsync("/admin/ops/broadcast", AdminTestData.Json(Body("all", reason: "")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Push.Sent);
    }

    /// <summary>The audit row records the segment and the true recipient count.</summary>
    [Fact]
    public async Task records_the_segment_and_the_recipient_count()
    {
        if (!_factory.MongoIsUp()) return;

        await SeedManyAsync(7);

        await _factory.AdminClient()
            .PostAsync("/admin/ops/broadcast", AdminTestData.Json(Body("all", "Winter re-engagement")));

        var entry = await _factory.Database()
            .GetCollection<BsonDocument>("adminauditevents")
            .Find(new BsonDocument("action", AdminAuditAction.Broadcast))
            .FirstAsync();

        Assert.Equal("Winter re-engagement", entry["reason"].AsString);

        var details = entry["details"].AsBsonDocument;
        Assert.Equal("all", details["segment"].AsString);
        Assert.Equal(7, details["recipients"].ToInt32());
    }
}
