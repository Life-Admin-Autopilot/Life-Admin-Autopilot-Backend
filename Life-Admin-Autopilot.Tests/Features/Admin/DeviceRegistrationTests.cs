using System.Net;
using System.Net.Http.Headers;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// <c>POST /api/devices/register</c> — the endpoint the Capacitor client calls on
/// every cold start to become reachable by push.
///
/// <para>
/// <b>Why this lives in the admin suite.</b> It was found while building the
/// console's "send a message to a customer" feature, which kept reporting zero
/// targeted devices. The cause was not in the console: no device could ever
/// register, so no customer was ever reachable by push at all, and nothing
/// surfaced it — the client fires this and ignores the result.
/// </para>
/// </summary>
[Collection("admin-serial")]
public sealed class DeviceRegistrationTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public DeviceRegistrationTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CustomerClient(ObjectId userId)
    {
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(userId.ToString(), "device-owner@test.local"));

        return client;
    }

    /// <summary>
    /// The exact payload <c>lib/notifications/registerPushDevice.ts</c> posts, with
    /// the platform as the enum's NAME — which the client's own comment states the
    /// binder accepts.
    /// </summary>
    [Theory]
    [InlineData("Ios")]
    [InlineData("Android")]
    public async Task accepts_the_payload_the_app_actually_sends(string platform)
    {
        if (!_factory.MongoIsUp()) return;

        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "deviceTokens");
        var userId = ObjectId.GenerateNewId();

        var response = await CustomerClient(userId).PostAsync(
            "/api/devices/register",
            AdminTestData.Json(new
            {
                token = $"fcm-token-for-{platform}",
                platform,
                deviceModel = "iPhone 16",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await db.GetCollection<BsonDocument>("deviceTokens")
            .Find(new BsonDocument("UserId", userId.ToString()))
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(platform, stored["Platform"].AsString);
        Assert.True(stored["IsActive"].AsBoolean);
    }

    /// <summary>
    /// Capacitor reports lowercase platform names. The client maps them today, but
    /// a binder that only accepts one casing is a trap the next caller falls into —
    /// and the cost of the trap is silent, permanent push failure.
    /// </summary>
    [Theory]
    [InlineData("ios")]
    [InlineData("android")]
    public async Task accepts_the_lowercase_names_capacitor_reports(string platform)
    {
        if (!_factory.MongoIsUp()) return;

        await AdminTestData.ClearAsync(_factory.Database(), "deviceTokens");

        var response = await CustomerClient(ObjectId.GenerateNewId()).PostAsync(
            "/api/devices/register",
            AdminTestData.Json(new { token = "fcm-token-lowercase", platform }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <b>A body that cannot bind is a 400, never a 500.</b>
    ///
    /// <para>
    /// The original handler dereferenced the request without a null check, so any
    /// unbindable body produced <c>500 internal_error</c> — which tells the client
    /// nothing and reads as a server fault rather than a bad request.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("""{"token":"t","platform":"Windows"}""")]
    [InlineData("""{"token":"t","platform":42}""")]
    [InlineData("""{"token":"t"}""")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task a_body_that_cannot_bind_is_a_client_error(string body)
    {
        if (!_factory.MongoIsUp()) return;

        var response = await CustomerClient(ObjectId.GenerateNewId()).PostAsync(
            "/api/devices/register",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.True(
            (int)response.StatusCode is >= 400 and < 500,
            $"expected a 4xx for body {body}, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task an_empty_token_is_refused()
    {
        if (!_factory.MongoIsUp()) return;

        var response = await CustomerClient(ObjectId.GenerateNewId()).PostAsync(
            "/api/devices/register",
            AdminTestData.Json(new { token = "   ", platform = "Ios" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Re-registration is normal — FCM rotates tokens and the client posts on every
    /// start. The same token must refresh its row rather than duplicate it.
    /// </summary>
    [Fact]
    public async Task registering_the_same_token_twice_does_not_duplicate_it()
    {
        if (!_factory.MongoIsUp()) return;

        var db = _factory.Database();
        await AdminTestData.ClearAsync(db, "deviceTokens");
        var userId = ObjectId.GenerateNewId();
        var client = CustomerClient(userId);

        var payload = new { token = "a-rotating-token", platform = "Ios", deviceModel = "iPhone 16" };

        await client.PostAsync("/api/devices/register", AdminTestData.Json(payload));
        await client.PostAsync("/api/devices/register", AdminTestData.Json(payload));

        var count = await db.GetCollection<BsonDocument>("deviceTokens")
            .CountDocumentsAsync(new BsonDocument("Token", "a-rotating-token"));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task registering_requires_a_signed_in_customer()
    {
        var response = await _factory.CreateApiClient().PostAsync(
            "/api/devices/register",
            AdminTestData.Json(new { token = "t", platform = "Ios" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
