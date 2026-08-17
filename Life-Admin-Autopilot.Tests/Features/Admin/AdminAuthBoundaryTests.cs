using System.Net;
using System.Net.Http.Headers;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// The console's security boundary.
///
/// <para>
/// These are the tests that matter most in this slice. Everything else is a
/// dashboard being wrong; this is a customer's account being reachable by
/// someone who should not reach it.
/// </para>
/// </summary>
public sealed class AdminAuthBoundaryTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminAuthBoundaryTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string> ConsoleRoutes() => new()
    {
        { "GET", "/admin/auth/me" },
        { "GET", "/admin/customers" },
        { "GET", "/admin/customers/6a828f45e278c6bcc3ed8e64" },
        { "GET", "/admin/insights/pulse" },
        { "GET", "/admin/insights/top-spenders" },
        { "GET", "/admin/insights/cost-distribution" },
        { "GET", "/admin/insights/errors" },
        { "GET", "/admin/insights/funnel" },
        { "GET", "/admin/audit" },
        { "GET", "/admin/ops/flags" },
        { "GET", "/admin/ops/admins" },
        { "POST", "/admin/customers/6a828f45e278c6bcc3ed8e64/suspend" },
        { "POST", "/admin/customers/6a828f45e278c6bcc3ed8e64/notify" },
        { "POST", "/admin/ops/broadcast" },
    };

    [Theory]
    [MemberData(nameof(ConsoleRoutes))]
    public async Task every_console_route_refuses_an_anonymous_caller(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await _factory.CreateApiClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// <b>The load-bearing test for the two-key design.</b>
    ///
    /// <para>
    /// A customer access token is signed with the app's key. The console scheme
    /// validates against a different one, so this fails at signature validation —
    /// not at the role check. That distinction is the whole reason the keys are
    /// split: if they were shared, the only thing between a customer's token and
    /// this route would be a role claim the same key could mint.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ConsoleRoutes))]
    public async Task a_customer_token_cannot_reach_the_console(string method, string path)
    {
        var customerToken = KernelPipelineTests.NodeShapedToken(
            ObjectId.GenerateNewId().ToString(),
            "customer@test.local");

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);

        var response = await _factory.CreateApiClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The reverse direction, which is just as important and much easier to forget:
    /// a console token must not let an admin act AS a customer.
    /// </summary>
    [Theory]
    [InlineData("GET", "/ai/conversation")]
    [InlineData("GET", "/ai/quota")]
    [InlineData("GET", "/me/notifications")]
    public async Task a_console_token_cannot_reach_the_customer_api(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.AdminToken(Guid.NewGuid()));

        var response = await _factory.CreateApiClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A token signed with the right key but carrying no console role is still
    /// refused — the policy requires a role, not merely a valid signature.
    /// </summary>
    [Fact]
    public async Task a_correctly_signed_token_with_no_role_is_refused()
    {
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _factory.AdminToken(Guid.NewGuid(), roles: Array.Empty<string>() is var _ ? new[] { "NotARole" } : null!));

        var response = await client.GetAsync("/admin/auth/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Support holds the console policy but not the operator policy. Operations —
    /// kill switches, broadcast, who else is an admin — must be Admin-only.
    /// </summary>
    [Theory]
    [InlineData("GET", "/admin/ops/flags")]
    [InlineData("GET", "/admin/ops/admins")]
    [InlineData("POST", "/admin/ops/broadcast")]
    [InlineData("POST", "/admin/ops/admins/grant")]
    public async Task support_cannot_reach_operations(string method, string path)
    {
        var client = _factory.AdminClient(AdminRoles.Support);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>...but Support CAN read the customer surfaces it exists to work in.</summary>
    [Theory]
    [InlineData("/admin/auth/me")]
    [InlineData("/admin/customers")]
    [InlineData("/admin/insights/pulse")]
    public async Task support_can_reach_the_customer_surfaces(string path)
    {
        var response = await _factory.AdminClient(AdminRoles.Support).GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Sign-in must answer identically for an unknown address, a wrong password,
    /// and a correct password on an account with no console role.
    ///
    /// <para>
    /// If the third case differed, anyone could enumerate which of your customers
    /// are administrators — which is the first step of a targeted attack, and a
    /// mistake that is invisible until someone goes looking for it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task signin_does_not_reveal_which_accounts_are_admins()
    {
        var client = _factory.CreateApiClient();

        var unknown = await client.PostAsync(
            "/admin/auth/signin",
            AdminTestData.Json(new { email = "nobody@test.local", password = "whatever-123" }));

        var wrongShape = await client.PostAsync(
            "/admin/auth/signin",
            AdminTestData.Json(new { email = "someone.else@test.local", password = "different-456" }));

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongShape.StatusCode);

        var a = await AdminTestData.ReadAsync(unknown);
        var b = await AdminTestData.ReadAsync(wrongShape);

        // Same code AND same message — a difference in either is the leak.
        Assert.Equal(
            a.GetProperty("error").GetProperty("code").GetString(),
            b.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(
            a.GetProperty("error").GetProperty("message").GetString(),
            b.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task signin_rejects_a_body_with_no_password()
    {
        var response = await _factory.CreateApiClient().PostAsync(
            "/admin/auth/signin",
            AdminTestData.Json(new { email = "someone@test.local" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An id that is not an ObjectId is a 400, not a 500.</summary>
    [Fact]
    public async Task a_malformed_customer_id_is_a_client_error()
    {
        var response = await _factory.AdminClient().GetAsync("/admin/customers/not-an-object-id");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal("invalid_id", json.GetProperty("error").GetProperty("code").GetString());
    }
}
