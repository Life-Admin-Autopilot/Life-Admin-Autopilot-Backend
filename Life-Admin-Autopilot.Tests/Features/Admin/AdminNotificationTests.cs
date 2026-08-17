using System.Net;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Push.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// Sending a customer a message.
///
/// <para>
/// The behaviour under test is the one design claim that matters here:
/// <b>the in-app notification row is the message, and push is a doorbell.</b>
/// Every path below exists to prove the row survives whatever push does.
/// </para>
/// </summary>
[Collection("admin-serial")]
public sealed class AdminNotificationTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminNotificationTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Push.Reset();
    }

    private IMongoCollection<BsonDocument> Notifications =>
        _factory.Database().GetCollection<BsonDocument>("notifications");

    private IMongoCollection<BsonDocument> Devices =>
        _factory.Database().GetCollection<BsonDocument>("deviceTokens");

    private IMongoCollection<BsonDocument> Audit =>
        _factory.Database().GetCollection<BsonDocument>("adminauditevents");

    private async Task<ObjectId> FreshCustomerAsync()
    {
        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "users", "notifications", "deviceTokens", "adminauditevents");
        return await AdminTestData.SeedUserAsync(db, "recipient@test.local");
    }

    private static object Message(string? reason = "Support follow-up on their scan") => new
    {
        title = "Your scan finished",
        body = "We re-ran the extraction on your policy.",
        reason,
    };

    // ---- the core claim ----------------------------------------------------

    /// <summary>
    /// <b>No devices at all still produces the message.</b>
    ///
    /// <para>
    /// This is the case an obvious implementation gets wrong: treating push as the
    /// message drops it entirely for a customer who has never granted notification
    /// permission — which is exactly the customer least reachable another way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task writes_the_in_app_row_even_when_the_customer_has_no_devices()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal(0, json.GetProperty("devicesTargeted").GetInt32());
        Assert.True(json.GetProperty("inAppCreated").GetBoolean());

        var stored = await Notifications.Find(new BsonDocument("userId", userId)).ToListAsync();
        var row = Assert.Single(stored);

        Assert.Equal(AdminNotificationService.AnnouncementKind, row["kind"].AsString);
        Assert.Equal("Your scan finished", row["title"].AsString);
        Assert.Equal("We re-ran the extraction on your policy.", row["body"].AsString);
    }

    /// <summary>
    /// <b>Every device failing still produces the message.</b> The row is written
    /// before push is attempted, so a total push outage costs delivery speed and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task writes_the_in_app_row_even_when_every_push_fails()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "token-a");
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "token-b", "Android");

        _factory.Push.Responder = _ => AdminTestData.Transient();

        var response = await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        var json = await AdminTestData.ReadAsync(response);

        Assert.Equal(2, json.GetProperty("devicesTargeted").GetInt32());
        Assert.Equal(0, json.GetProperty("delivered").GetInt32());
        Assert.Equal(2, json.GetProperty("failed").GetInt32());
        Assert.True(json.GetProperty("inAppCreated").GetBoolean());

        Assert.Equal(1, await Notifications.CountDocumentsAsync(new BsonDocument("userId", userId)));
    }

    /// <summary>Every registered device is rung, and the receipt is per device.</summary>
    [Fact]
    public async Task rings_every_registered_device_and_reports_each_one()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "ios-token-good");
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "android-token-bad", "Android");

        // One phone works, the other does not — the case a single green tick hides.
        _factory.Push.Responder = request =>
            request.DeviceToken == "ios-token-good"
                ? Result<PushNotificationResult>.Success(new PushNotificationResult { MessageId = "ok" })
                : AdminTestData.Transient();

        var response = await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        var json = await AdminTestData.ReadAsync(response);

        Assert.Equal(2, json.GetProperty("devicesTargeted").GetInt32());
        Assert.Equal(1, json.GetProperty("delivered").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());

        var devices = json.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.GetProperty("delivered").GetBoolean());
        Assert.Contains(devices, d => !d.GetProperty("delivered").GetBoolean());

        // The token is masked in the receipt — enough to tell two phones apart and
        // no more. A console does not need the credential itself.
        Assert.All(devices, d =>
            Assert.DoesNotContain("ios-token-good", d.GetProperty("token").GetString()!));
    }

    /// <summary>
    /// A token FCM has permanently retired is deactivated, so the same dead device
    /// does not fail on every future send and make the delivery numbers look
    /// broken forever.
    /// </summary>
    [Fact]
    public async Task deactivates_a_permanently_dead_token()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "retired-token");

        _factory.Push.Responder = _ => AdminTestData.Dead(PushErrorCodes.TokenInvalid);

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        var device = await Devices.Find(new BsonDocument("Token", "retired-token")).FirstAsync();
        Assert.False(device["IsActive"].AsBoolean);
    }

    /// <summary>
    /// A transient failure must NOT deactivate the device — the phone is fine, the
    /// provider had a moment. Deactivating here would quietly unsubscribe working
    /// devices during any outage.
    /// </summary>
    [Fact]
    public async Task leaves_a_device_active_after_a_transient_failure()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "healthy-token");

        _factory.Push.Responder = _ => AdminTestData.Transient();

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        var device = await Devices.Find(new BsonDocument("Token", "healthy-token")).FirstAsync();
        Assert.True(device["IsActive"].AsBoolean);
    }

    /// <summary>Inactive devices are not rung at all.</summary>
    [Fact]
    public async Task does_not_ring_a_deactivated_device()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "active-token");
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "retired-token");
        await Devices.UpdateOneAsync(
            new BsonDocument("Token", "retired-token"),
            new BsonDocument("$set", new BsonDocument("IsActive", false)));

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        Assert.Single(_factory.Push.Sent);
        Assert.Equal("active-token", _factory.Push.Sent[0].DeviceToken);
    }

    /// <summary>The push payload carries the kind, so the app can route the tap.</summary>
    [Fact]
    public async Task the_push_payload_carries_the_announcement_kind()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        await AdminTestData.SeedDeviceAsync(_factory.Database(), userId, "routing-token");

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/notify", AdminTestData.Json(Message()));

        var sent = Assert.Single(_factory.Push.Sent);
        Assert.Equal("Your scan finished", sent.Title);
        Assert.Equal(AdminNotificationService.AnnouncementKind, sent.Data!["kind"]);
    }

    // ---- validation --------------------------------------------------------

    [Fact]
    public async Task refuses_a_message_with_no_reason()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/notify",
            AdminTestData.Json(Message(reason: "")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal("reason_required", json.GetProperty("error").GetProperty("code").GetString());

        // And nothing was sent. A refused action must leave no trace of having
        // half-happened.
        Assert.Empty(_factory.Push.Sent);
        Assert.Equal(0, await Notifications.CountDocumentsAsync(new BsonDocument("userId", userId)));
    }

    [Theory]
    [InlineData("", "a body")]
    [InlineData("a title", "")]
    [InlineData("   ", "a body")]
    public async Task refuses_an_empty_title_or_body(string title, string body)
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/notify",
            AdminTestData.Json(new { title, body, reason = "Testing validation" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Notifications.CountDocumentsAsync(new BsonDocument("userId", userId)));
    }

    [Fact]
    public async Task refuses_an_over_long_title()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/notify",
            AdminTestData.Json(new
            {
                title = new string('x', AdminMessage.MaxTitle + 1),
                body = "fine",
                reason = "Testing the ceiling",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task refuses_to_message_a_customer_that_does_not_exist()
    {
        if (!_factory.MongoIsUp()) return;

        await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{ObjectId.GenerateNewId()}/notify",
            AdminTestData.Json(Message()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.Push.Sent);
    }

    // ---- audit -------------------------------------------------------------

    /// <summary>
    /// The audit row is written BEFORE the push leaves. A notification cannot be
    /// recalled, so the record of who sent what has to exist first.
    /// </summary>
    [Fact]
    public async Task records_who_sent_what_and_why()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/notify",
            AdminTestData.Json(Message(reason: "Their scan silently failed twice")));

        var entries = await Audit
            .Find(new BsonDocument("action", AdminAuditAction.CustomerNotified))
            .ToListAsync();

        var entry = Assert.Single(entries);

        Assert.Equal("Their scan silently failed twice", entry["reason"].AsString);
        Assert.Equal(userId.ToString(), entry["targetUserId"].AsString);
        Assert.Equal("recipient@test.local", entry["targetEmail"].AsString);

        // The message itself is in the audit row, so the log answers "what did they
        // actually send?" without a second lookup into the customer's data.
        var details = entry["details"].AsBsonDocument;
        Assert.Equal("Your scan finished", details["title"].AsString);
    }
}

/// <summary>
/// These suites share one database and clear it between tests, so they must not
/// interleave. xUnit runs collections in parallel by default, which would make
/// them flaky in a way that looks like a product bug.
/// </summary>
[CollectionDefinition("admin-serial", DisableParallelization = true)]
public sealed class AdminSerialCollection;
