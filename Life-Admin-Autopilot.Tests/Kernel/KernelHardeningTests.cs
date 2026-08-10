using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot_Backend.Kernel.Security;
using Xunit;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// Differentials for the three kernel defects found by slice b-auth, each one
/// pinned to a measurement taken from the live Node reference rather than to what
/// the .NET code happens to do.
///
/// <list type="number">
///   <item>
///     <b>Content-Type gating.</b> <c>express.json()</c> parses ONLY
///     <c>application/json</c>. Anything else leaves <c>req.body = {}</c> and the
///     route's own validators then report the fields as missing.
///   </item>
///   <item>
///     <b>helmet's twelve defaults on every response</b> — success, error and 429
///     alike — with the rate-limit headers surviving the error path.
///   </item>
/// </list>
///
/// <para>Defect 3 is a bind-address change in the docs; it has no code surface, and
/// is verified by running the harness against a dual-stack candidate.</para>
/// </summary>
public sealed class KernelHardeningTests : IClassFixture<KernelHardeningTests.Factory>
{
    /// <summary>Its own Mongo database, per the §12 rule on concurrent slices.</summary>
    public sealed class Factory : KernelWebApplicationFactory
    {
        public Factory() => With("MongoDbSettings:DatabaseName", "kitto_parity_dotnet_kh_tests");
    }

    private const string CredentialsPath = "/__kernel-probe/credentials";

    /// <summary>A payload that is valid on the reference — so the ONLY variable is the content type.</summary>
    private const string ValidPayload = """{"email":"probe@example.com","password":"password123"}""";

    private readonly Factory _factory;

    public KernelHardeningTests(Factory factory) => _factory = factory;

    // ---------------------------------------------------------------------
    // Defect 1 — bodies are parsed regardless of Content-Type
    // ---------------------------------------------------------------------

    /// <summary>
    /// The five cases from the bug report, measured on the reference with
    /// <c>POST /auth/signup</c> and an otherwise valid JSON payload:
    /// <c>application/json</c> → 201, and absent / <c>text/plain</c> /
    /// <c>application/vnd.api+json</c> / form-encoded → 400. The candidate answered
    /// 201 to all five.
    /// </summary>
    [Theory]
    [InlineData("application/json", HttpStatusCode.Created)]
    [InlineData(null, HttpStatusCode.BadRequest)]
    [InlineData("text/plain", HttpStatusCode.BadRequest)]
    [InlineData("application/vnd.api+json", HttpStatusCode.BadRequest)]
    [InlineData("application/x-www-form-urlencoded", HttpStatusCode.BadRequest)]
    public async Task content_type_decides_whether_the_body_is_parsed(string? contentType, HttpStatusCode expected)
    {
        var response = await PostRawAsync(CredentialsPath, ValidPayload, contentType);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// The parameter case, called out separately because getting it wrong breaks every
    /// ordinary browser client. <c>type-is</c> matches on the media type alone, so a
    /// charset parameter — in any casing, spaced or not — still parses.
    /// </summary>
    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json;charset=UTF-8")]
    [InlineData("APPLICATION/JSON")]
    public async Task json_with_parameters_or_odd_casing_still_parses(string contentType)
    {
        var response = await PostRawAsync(CredentialsPath, ValidPayload, contentType);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// A <c>+json</c> structured suffix is NOT <c>application/json</c>. body-parser's
    /// default pattern is the literal type; only an <c>application/*+json</c> pattern
    /// would cover the suffix form. Verified live on both spellings.
    /// </summary>
    [Theory]
    [InlineData("application/json-patch+json")]
    [InlineData("application/vnd.api+json")]
    [InlineData("garbage")]
    public async Task json_suffix_and_unparseable_types_are_not_json(string contentType)
    {
        var response = await PostRawAsync(CredentialsPath, ValidPayload, contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The rejection must come from the ROUTE'S OWN validators, not from a new error
    /// path invented in the binder — the whole point of leaving the body empty. This is
    /// the exact envelope the reference returns for
    /// <c>POST /auth/signup</c> with <c>Content-Type: text/plain</c>.
    /// </summary>
    [Fact]
    public async Task a_non_json_content_type_produces_nodes_exact_required_errors()
    {
        var response = await PostRawAsync(CredentialsPath, ValidPayload, "text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            """{"error":{"code":"validation_error","message":"Request validation failed","details":[{"path":"email","message":"Required"},{"path":"password","message":"Required"}]}}""",
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The gate has to run BEFORE the body is read, not after. body-parser skips a
    /// non-JSON request without touching the stream, so neither the 256kb ceiling nor a
    /// JSON syntax error can fire. Measured on the reference: the SAME body is a 500 as
    /// <c>application/json</c> and a plain 400 as <c>text/plain</c>. A gate applied
    /// after reading would answer 500 to both.
    /// </summary>
    [Theory]
    [InlineData("{not json")]
    [InlineData(null)] // stands for the oversized body, built below
    public async Task an_unreadable_body_is_only_a_500_when_it_is_json(string? rawBody)
    {
        var body = rawBody ?? new string('x', 300 * 1024);

        var asJson = await PostRawAsync(CredentialsPath, body, "application/json");
        Assert.Equal(HttpStatusCode.InternalServerError, asJson.StatusCode);

        var asText = await PostRawAsync(CredentialsPath, body, "text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, asText.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Defect 2 — security headers, on the success AND error paths
    // ---------------------------------------------------------------------

    /// <summary>
    /// All twelve helmet defaults on a 200. Values are compared literally: the CSP is
    /// semicolon-joined with no following space, and <c>X-XSS-Protection</c> is
    /// <c>0</c>, not the <c>1; mode=block</c> a naive port writes.
    /// </summary>
    [Fact]
    public async Task the_success_path_carries_every_helmet_default()
    {
        var response = await _factory.CreateApiClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHelmetDefaults(response);
    }

    /// <summary>
    /// The same twelve on a 400. Express's error handler replaces the body, not the
    /// headers — so <c>KernelErrorMiddleware.WriteAsync</c> must not
    /// <c>Response.Clear()</c>. This is the assertion that fails the moment somebody
    /// puts Clear back.
    /// </summary>
    [Fact]
    public async Task the_error_path_carries_every_helmet_default()
    {
        var response = await PostRawAsync(CredentialsPath, ValidPayload, "text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertHelmetDefaults(response);
    }

    /// <summary>And on a 500, which travels through a different branch of Translate().</summary>
    [Fact]
    public async Task the_500_path_carries_every_helmet_default()
    {
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertHelmetDefaults(response);
    }

    /// <summary>
    /// The error path must not lose the CORS pair either — a browser cannot read an
    /// error response whose <c>Access-Control-*</c> headers were wiped. Same
    /// <c>Response.Clear()</c> root cause.
    /// </summary>
    [Fact]
    public async Task the_error_path_keeps_the_cors_headers()
    {
        var response = await SendWithOriginAsync(CredentialsPath, ValidPayload, "text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            KernelWebApplicationFactory.AllowedOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    /// <summary>
    /// The 429, captured from the dev-mode reference on <c>:4100</c> after burning the
    /// 20-request authLimiter window. The limiter sets its headers and then throws, so
    /// every one of them travels through the error middleware — which is exactly what
    /// <c>Response.Clear()</c> used to destroy.
    /// </summary>
    [Fact]
    public async Task the_429_keeps_its_retry_after_and_ratelimit_headers()
    {
        // A dedicated factory: enabling the limiter mutates a singleton counter, and the
        // shared fixture is used by every other test in this class.
        using var factory = (Factory)new Factory().WithRateLimiting();
        var client = factory.CreateApiClient();

        HttpResponseMessage? limited = null;
        for (var attempt = 0; attempt < 25 && limited is null; attempt++)
        {
            var response = await client.GetAsync("/__kernel-probe/auth-limited");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        Assert.NotNull(limited);

        // Node, :4100: RateLimit-Policy 20;w=900, Limit 20, Remaining 0, Reset 900,
        // Retry-After 900 — alongside the full helmet set.
        Assert.Equal("20;w=900", Assert.Single(limited.Headers.GetValues("RateLimit-Policy")));
        Assert.Equal("20", Assert.Single(limited.Headers.GetValues("RateLimit-Limit")));
        Assert.Equal("0", Assert.Single(limited.Headers.GetValues("RateLimit-Remaining")));
        Assert.True(limited.Headers.Contains("RateLimit-Reset"));
        Assert.True(limited.Headers.Contains("Retry-After"));
        AssertHelmetDefaults(limited);

        Assert.Equal(
            """{"error":{"code":"rate_limited","message":"Too many requests. Try again in a few minutes."}}""",
            await limited.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A 404 from the method-mismatch rewrite keeps helmet's headers — EXCEPT the
    /// CSP, which Express's <c>finalhandler</c> writes itself as
    /// <c>default-src 'none'</c>, replacing the app-wide policy.
    ///
    /// <para>This test originally asserted the full app CSP here. That expectation
    /// was written before anyone measured the header, and the reference disagrees:
    /// <c>curl -X PUT :4100/health</c> answers <c>default-src 'none'</c>, while a
    /// ROUTE-level 404 such as <c>task_not_found</c> — which never reaches
    /// finalhandler — keeps the full policy. Both were checked live.</para>
    /// </summary>
    [Fact]
    public async Task the_method_mismatch_404_carries_helmet_but_finalhandlers_own_csp()
    {
        var response = await _factory.CreateApiClient().PutAsync("/health", TextContent("", "text/plain"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertHelmetDefaults(response, expectFinalHandlerCsp: true);
        Assert.Equal("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    /// <summary>
    /// The list itself, guarded against silent drift: twelve entries, no duplicates,
    /// and the two values a port is most likely to "improve".
    /// </summary>
    [Fact]
    public void the_default_set_is_the_twelve_the_contract_names()
    {
        Assert.Equal(12, HelmetHeadersMiddleware.Defaults.Count);
        Assert.Equal(
            12,
            HelmetHeadersMiddleware.Defaults.Select(h => h.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var byName = HelmetHeadersMiddleware.Defaults.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

        // helmet disables the legacy XSS auditor rather than enabling it.
        Assert.Equal("0", byName["X-XSS-Protection"]);

        // SAMEORIGIN, not DENY — the reference allows same-origin framing.
        Assert.Equal("SAMEORIGIN", byName["X-Frame-Options"]);

        // No `preload`, and no space after the semicolons inside the CSP.
        Assert.Equal("max-age=31536000; includeSubDomains", byName["Strict-Transport-Security"]);
        Assert.DoesNotContain("; ", byName["Content-Security-Policy"], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------

    /// <param name="expectFinalHandlerCsp">
    /// Set for the fall-through 404s only. Express's finalhandler replaces the
    /// app-wide CSP with its own on those, so the caller asserts that value
    /// separately and this helper skips it.
    /// </param>
    private static void AssertHelmetDefaults(HttpResponseMessage response, bool expectFinalHandlerCsp = false)
    {
        foreach (var (name, expected) in HelmetHeadersMiddleware.Defaults)
        {
            Assert.True(response.Headers.Contains(name), $"missing security header: {name}");

            if (expectFinalHandlerCsp && name == "Content-Security-Policy")
            {
                continue;
            }

            Assert.Equal(expected, Assert.Single(response.Headers.GetValues(name)));
        }

        // Node sends no server-identity header: `app.disable('x-powered-by')` on its
        // side, AddServerHeader=false on ours.
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    private async Task<HttpResponseMessage> SendWithOriginAsync(string path, string body, string contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = TextContent(body, contentType),
        };
        request.Headers.Add("Origin", KernelWebApplicationFactory.AllowedOrigin);

        return await _factory.CreateApiClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostRawAsync(string path, string body, string? contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = TextContent(body, contentType),
        };

        return await _factory.CreateApiClient().SendAsync(request);
    }

    /// <summary>
    /// A body with the content type set LITERALLY — or removed entirely, which
    /// <c>StringContent</c> will not do on its own. The absent-header case is one of the
    /// five measured rows, and <c>garbage</c> is a deliberately unparseable value, so
    /// both have to survive the client without being normalised or rejected.
    /// </summary>
    private static HttpContent TextContent(string body, string? contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.Remove("Content-Type");

        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return content;
    }
}
