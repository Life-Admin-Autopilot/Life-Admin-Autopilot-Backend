using System.Net;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// The account-state actions, and the audit trail they are required to leave.
/// </summary>
[Collection("admin-serial")]
public sealed class AdminCustomerActionTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminCustomerActionTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Push.Reset();
    }

    private IMongoCollection<BsonDocument> Users =>
        _factory.Database().GetCollection<BsonDocument>("users");

    private IMongoCollection<BsonDocument> Audit =>
        _factory.Database().GetCollection<BsonDocument>("adminauditevents");

    private async Task<ObjectId> FreshCustomerAsync()
    {
        var db = _factory.Database();
        await AdminTestData.ClearAsync(
            db, "users", "adminauditevents", "refreshtokens",
            "aiusagecounters", "documentscanusagecounters", "translationusagecounters");

        return await AdminTestData.SeedUserAsync(db, "target@test.local");
    }

    private static object Reason(string text = "Investigating a support ticket") => new { reason = text };

    // ---- suspend / restore -------------------------------------------------

    [Fact]
    public async Task suspending_stamps_the_account_and_records_the_reason()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/suspend",
            AdminTestData.Json(Reason("Abusing the free tier")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await Users.Find(new BsonDocument("_id", userId)).FirstAsync();
        Assert.True(user.Contains("suspendedAt"));
        Assert.Equal("Abusing the free tier", user["suspendedReason"].AsString);
    }

    /// <summary>
    /// Restore clears BOTH fields. Leaving a stale reason behind would make the
    /// customer detail page show a suspension banner on a live account.
    /// </summary>
    [Fact]
    public async Task restoring_clears_the_suspension_completely()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        var client = _factory.AdminClient();

        await client.PostAsync($"/admin/customers/{userId}/suspend", AdminTestData.Json(Reason()));
        await client.PostAsync($"/admin/customers/{userId}/restore", AdminTestData.Json(Reason("Resolved after review")));

        var user = await Users.Find(new BsonDocument("_id", userId)).FirstAsync();

        // IgnoreIfNull means a cleared field is ABSENT, not null — so `Contains` is
        // the correct assertion and `!= BsonNull` would silently pass either way.
        Assert.False(user.Contains("suspendedAt"));
        Assert.False(user.Contains("suspendedReason"));
    }

    /// <summary>
    /// Suspension revokes refresh tokens, so a live session dies within one
    /// access-token lifetime instead of running until it expires on its own.
    /// </summary>
    [Fact]
    public async Task suspending_revokes_the_customers_sessions()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        var sessions = _factory.Database().GetCollection<BsonDocument>("refreshtokens");

        await sessions.InsertOneAsync(new BsonDocument
        {
            ["userId"] = userId,
            ["tokenHash"] = "hash-of-a-live-session",
            ["expiresAt"] = DateTime.UtcNow.AddDays(30),
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
        });

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/suspend", AdminTestData.Json(Reason()));

        var token = await sessions.Find(new BsonDocument("userId", userId)).FirstAsync();
        Assert.True(token.Contains("revokedAt"), "the refresh token should have been revoked");
    }

    // ---- the reason gate ---------------------------------------------------

    [Theory]
    [InlineData("suspend")]
    [InlineData("restore")]
    [InlineData("reset-quota")]
    [InlineData("revoke-sessions")]
    public async Task every_action_refuses_without_a_reason(string action)
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/{action}",
            AdminTestData.Json(new { reason = "no" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal("reason_required", json.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// A refused action must leave nothing behind — no state change AND no audit
    /// row. An audit log full of attempts that never happened is noise.
    /// </summary>
    [Fact]
    public async Task a_refused_action_changes_nothing()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/suspend",
            AdminTestData.Json(new { reason = "x" }));

        var user = await Users.Find(new BsonDocument("_id", userId)).FirstAsync();
        Assert.False(user.Contains("suspendedAt"));

        Assert.Equal(0, await Audit.CountDocumentsAsync(
            new BsonDocument("action", AdminAuditAction.CustomerSuspended)));
    }

    // ---- quotas ------------------------------------------------------------

    [Fact]
    public async Task resetting_quotas_clears_every_counter_bucket()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        var db = _factory.Database();

        foreach (var collection in new[]
                 {
                     "aiusagecounters", "documentscanusagecounters", "translationusagecounters",
                 })
        {
            await db.GetCollection<BsonDocument>(collection).InsertOneAsync(new BsonDocument
            {
                ["userId"] = userId,
                ["kind"] = "message",
                ["period"] = "2026-08-17",
                ["count"] = 30,
            });
        }

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/reset-quota", AdminTestData.Json(Reason()));

        foreach (var collection in new[]
                 {
                     "aiusagecounters", "documentscanusagecounters", "translationusagecounters",
                 })
        {
            Assert.Equal(
                0,
                await db.GetCollection<BsonDocument>(collection)
                    .CountDocumentsAsync(new BsonDocument("userId", userId)));
        }
    }

    /// <summary>
    /// One customer's reset must not touch anyone else's counters. Obvious, and
    /// exactly the kind of filter that gets dropped in a refactor.
    /// </summary>
    [Fact]
    public async Task resetting_quotas_leaves_other_customers_alone()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        var bystander = await AdminTestData.SeedUserAsync(_factory.Database(), "bystander@test.local");
        var counters = _factory.Database().GetCollection<BsonDocument>("aiusagecounters");

        await counters.InsertOneAsync(new BsonDocument
        {
            ["userId"] = bystander, ["kind"] = "message", ["period"] = "2026-08-17", ["count"] = 12,
        });

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/reset-quota", AdminTestData.Json(Reason()));

        Assert.Equal(1, await counters.CountDocumentsAsync(new BsonDocument("userId", bystander)));
    }

    // ---- tier --------------------------------------------------------------

    [Fact]
    public async Task granting_a_tier_writes_it_and_says_the_quota_caveat_out_loud()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/tier",
            AdminTestData.Json(new { reason = "Comped for a support failure", tier = "pro", days = 30 }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await Users.Find(new BsonDocument("_id", userId)).FirstAsync();
        Assert.Equal("pro", user["subscription"]["tier"].AsString);

        // ResolveTier() still returns "free" unconditionally, so the console must
        // SAY the grant does not change what the customer can actually do. Silence
        // here would be a lie the operator only discovers from a support ticket.
        var json = await AdminTestData.ReadAsync(response);
        Assert.Contains("resolveTier", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task refuses_a_tier_outside_the_vocabulary()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/customers/{userId}/tier",
            AdminTestData.Json(new { reason = "Testing validation", tier = "platinum" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- audit -------------------------------------------------------------

    [Fact]
    public async Task each_action_writes_exactly_one_audit_row()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        var client = _factory.AdminClient();

        await client.PostAsync($"/admin/customers/{userId}/suspend", AdminTestData.Json(Reason("Suspending for the first check")));
        await client.PostAsync($"/admin/customers/{userId}/restore", AdminTestData.Json(Reason("Restoring for the second check")));
        await client.PostAsync($"/admin/customers/{userId}/reset-quota", AdminTestData.Json(Reason("Clearing counters for the third check")));

        Assert.Equal(1, await Audit.CountDocumentsAsync(new BsonDocument("action", AdminAuditAction.CustomerSuspended)));
        Assert.Equal(1, await Audit.CountDocumentsAsync(new BsonDocument("action", AdminAuditAction.CustomerRestored)));
        Assert.Equal(1, await Audit.CountDocumentsAsync(new BsonDocument("action", AdminAuditAction.CustomerQuotaReset)));
    }

    /// <summary>
    /// The audit row denormalises the target's email, so it stays readable after
    /// the customer deletes their account — which is exactly when someone reads it.
    /// </summary>
    [Fact]
    public async Task the_audit_row_survives_the_customer()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        await _factory.AdminClient()
            .PostAsync($"/admin/customers/{userId}/suspend", AdminTestData.Json(Reason()));

        await Users.DeleteOneAsync(new BsonDocument("_id", userId));

        var entry = await Audit
            .Find(new BsonDocument("action", AdminAuditAction.CustomerSuspended))
            .FirstAsync();

        Assert.Equal("target@test.local", entry["targetEmail"].AsString);
        Assert.Equal("admin@test.local", entry["actorEmail"].AsString);
    }

    /// <summary>Opening a customer is recorded — the only way to answer "who looked?"</summary>
    [Fact]
    public async Task viewing_a_customer_is_recorded()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();

        await _factory.AdminClient().GetAsync($"/admin/customers/{userId}");

        Assert.Equal(
            1,
            await Audit.CountDocumentsAsync(new BsonDocument("action", AdminAuditAction.CustomerViewed)));
    }

    /// <summary>The audit read is newest-first, so the page shows what just happened.</summary>
    [Fact]
    public async Task the_audit_feed_is_newest_first()
    {
        if (!_factory.MongoIsUp()) return;

        var userId = await FreshCustomerAsync();
        var client = _factory.AdminClient();

        await client.PostAsync($"/admin/customers/{userId}/suspend", AdminTestData.Json(Reason("First action in the ordering check")));
        await Task.Delay(15);
        await client.PostAsync($"/admin/customers/{userId}/restore", AdminTestData.Json(Reason("Second action in the ordering check")));

        var json = await AdminTestData.ReadAsync(await client.GetAsync("/admin/audit?action=customer."));
        var rows = json.GetProperty("rows").EnumerateArray().ToList();

        Assert.Equal("Second action in the ordering check", rows[0].GetProperty("reason").GetString());
    }
}
