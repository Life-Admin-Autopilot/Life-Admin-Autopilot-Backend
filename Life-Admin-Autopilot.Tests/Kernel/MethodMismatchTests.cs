using System.Net;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// Express has no 405 — a path that matches a route under a different method falls
/// through its router to a 404. ASP.NET answers 405 instead.
///
/// <para>Every expectation here was measured against the live Node server before it
/// was written. If one fails, the .NET server has diverged, not the test.</para>
/// </summary>
public sealed class MethodMismatchTests : IClassFixture<KernelWebApplicationFactory>
{
    private readonly KernelWebApplicationFactory _factory;

    public MethodMismatchTests(KernelWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task wrong_method_on_a_known_path_is_404_not_405(string method)
    {
        // Arrange — /health exists, but only for GET.
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(new HttpMethod(method), "/health");

        // Act
        var response = await client.SendAsync(request);

        // Assert — verified live: `curl -X PUT :4200/health` answers 404.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task the_404_carries_no_allow_header()
    {
        // Arrange — ASP.NET's 405 sets Allow; Express's 404 has none.
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Put, "/health");

        // Act
        var response = await client.SendAsync(request);

        // Assert — Allow is a CONTENT header in HttpClient's model; asking
        // response.Headers for it throws rather than returning false.
        Assert.Empty(response.Content.Headers.Allow);
    }

    [Fact]
    public async Task a_path_with_no_route_at_all_is_still_404()
    {
        // Arrange — this never produced a 405, so the rewrite must not change it.
        var client = _factory.CreateApiClient();

        // Act
        var response = await client.GetAsync("/nonexistent");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task the_404_body_is_not_the_json_error_envelope()
    {
        // Arrange — Express's finalhandler emits HTML here, never {error:{...}}. We
        // deliberately do NOT reproduce the HTML (it interpolates the request path,
        // which is reflected XSS), but a JSON envelope would be a different kind of
        // wrong: it would tell a client this is a handled application error.
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Put, "/health");

        // Act
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.DoesNotContain("\"error\"", body);
    }

    [Fact]
    public async Task options_on_a_known_path_is_not_rewritten()
    {
        // Arrange — OPTIONS is Express's automatic responder, handled in CORS. With no
        // Origin the CORS layer short-circuits at 204 before routing ever runs, so this
        // asserts the mismatch rewrite keeps its hands off OPTIONS entirely.
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/health");

        // Act
        var response = await client.SendAsync(request);

        // Assert — verified live: 204, not 404 and not 405.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task a_disallowed_origin_preflight_still_gets_expresss_auto_options_200()
    {
        // Arrange — a non-allowlisted origin is NOT short-circuited by CORS, so the
        // request reaches routing and produces a 405 that CORS rewrites to 200 with
        // Allow. This is the one case where a 405 legitimately becomes something other
        // than 404, and it must survive the new middleware.
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "https://evil.test");

        // Act
        var response = await client.SendAsync(request);

        // Assert — verified live: 200 with `Allow: GET,HEAD` and no ACAO.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("GET", string.Join(",", response.Content.Headers.Allow));
        Assert.Contains("HEAD", string.Join(",", response.Content.Headers.Allow));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
