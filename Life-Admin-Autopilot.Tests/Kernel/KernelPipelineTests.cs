using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// End-to-end parity checks for the kernel pipeline. Every expectation here was
/// verified against the live Node server on port 4100 — if one of these fails,
/// the .NET server has diverged, not the test.
/// </summary>
public sealed class KernelPipelineTests : IClassFixture<KernelWebApplicationFactory>
{
    private readonly KernelWebApplicationFactory _factory;

    public KernelPipelineTests(KernelWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- CORS -------------------------------------------------------------

    [Fact]
    public async Task allows_a_request_with_no_origin_header()
    {
        // Arrange
        var client = _factory.CreateApiClient();

        // Act
        var response = await client.GetAsync("/__kernel-probe/query");

        // Assert — native clients and curl send no Origin and must not be blocked.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
        Assert.Equal("Origin", Single(response, "Vary"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task echoes_an_allowlisted_origin()
    {
        // Arrange
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/__kernel-probe/query");
        request.Headers.Add("Origin", KernelWebApplicationFactory.AllowedOrigin);

        // Act
        var response = await client.SendAsync(request);

        // Assert — never '*': reflecting an arbitrary origin with credentials is the
        // vulnerability the allowlist exists to prevent.
        Assert.Equal(
            KernelWebApplicationFactory.AllowedOrigin,
            Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task sends_no_cors_headers_for_a_foreign_origin()
    {
        // Arrange
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/__kernel-probe/query");
        request.Headers.Add("Origin", "http://evil.example");

        // Act
        var response = await client.SendAsync(request);

        // Assert — the request still succeeds; the BROWSER refuses the response.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("Vary"));
    }

    [Fact]
    public async Task answers_an_allowlisted_preflight_with_204()
    {
        // Arrange
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/__kernel-probe/query");
        request.Headers.Add("Origin", KernelWebApplicationFactory.AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("GET,HEAD,PUT,PATCH,POST,DELETE", Single(response, "Access-Control-Allow-Methods"));
        Assert.Equal("authorization,content-type", Single(response, "Access-Control-Allow-Headers"));
        Assert.Equal("Origin, Access-Control-Request-Headers", Single(response, "Vary"));
    }

    [Fact]
    public async Task does_not_short_circuit_a_foreign_preflight()
    {
        // Arrange
        var client = _factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/__kernel-probe/query");
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        // Assert — Node's cors calls next() without applying anything, so this is NOT
        // a 204 and carries no CORS headers.
        var response = await client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- Error envelope ---------------------------------------------------

    [Fact]
    public async Task renders_an_app_exception_with_flattened_details()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/app-error");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = json.GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal("Some of those settings looked off.", error.GetProperty("message").GetString());

        var fieldErrors = error.GetProperty("details").GetProperty("fieldErrors");

        // The nested issue is keyed under the TOP-LEVEL field, exactly as zod's
        // flatten() does — "mic", never "mic.quality".
        Assert.True(fieldErrors.TryGetProperty("mic", out _));
        Assert.False(fieldErrors.TryGetProperty("mic.quality", out _));
        Assert.True(fieldErrors.TryGetProperty("theme", out _));
    }

    [Fact]
    public async Task renders_a_validation_exception_as_a_path_message_array()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/zod-error");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = json.GetProperty("error");
        Assert.Equal("validation_error", error.GetProperty("code").GetString());
        Assert.Equal("Request validation failed", error.GetProperty("message").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(JsonValueKind.Array, details.ValueKind);
        Assert.Equal("email", details[0].GetProperty("path").GetString());
        Assert.Equal("Invalid email", details[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task omits_details_when_there_are_none()
    {
        // Act — an unauthenticated call produces a details-free envelope.
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/me");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.False(json.GetProperty("error").TryGetProperty("details", out _));
    }

    [Fact]
    public async Task maps_an_unexpected_exception_to_500_internal_error()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/boom");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Internal server error", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task maps_a_malformed_object_id_to_404_not_found()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/objectid/notanid");
        var json = await ReadJsonAsync(response);

        // Assert — Mongoose CastError semantics: a bad :id is "no such resource".
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Not found", json.GetProperty("error").GetProperty("message").GetString());
    }

    // ---- The five cross-cutting edge cases --------------------------------

    [Fact]
    public async Task returns_500_not_400_for_malformed_json()
    {
        // Arrange — express's body-parser SyntaxError is unrecognised by the Node
        // error handler and falls through to the generic 500.
        var content = new StringContent("{bad", Encoding.UTF8, "application/json");

        // Act
        var response = await _factory.CreateApiClient().PostAsync("/__kernel-probe/lenient-body", content);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task returns_500_not_413_for_a_body_over_256kb()
    {
        // Arrange
        var payload = $"{{\"title\":\"{new string('a', 300_000)}\"}}";
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // Act
        var response = await _factory.CreateApiClient().PostAsync("/__kernel-probe/lenient-body", content);
        var json = await ReadJsonAsync(response);

        // Assert — PayloadTooLargeError is equally unrecognised by the Node handler.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task serves_the_default_404_for_an_unknown_route()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/definitely-not-a-route");

        // Assert — NOT the JSON envelope. Adding a catch-all would break parity.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"internal_error\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"not_found\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task accepts_unknown_keys_on_a_lenient_body()
    {
        // Arrange — a plain zod object STRIPS unknown keys and succeeds. Verified
        // live: POST /auth/signup with an extra field returns 201.
        var content = new StringContent(
            "{\"title\":\"hi\",\"count\":2,\"bogus\":1}",
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _factory.CreateApiClient().PostAsync("/__kernel-probe/lenient-body", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task rejects_unknown_keys_on_a_strict_body()
    {
        // Arrange
        var content = new StringContent("{\"title\":\"hi\",\"bogus\":1}", Encoding.UTF8, "application/json");

        // Act
        var response = await _factory.CreateApiClient().PostAsync("/__kernel-probe/strict-body", content);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = json.GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal(
            "Unrecognized key(s) in object: 'bogus'",
            error.GetProperty("details").GetProperty("formErrors")[0].GetString());
    }

    [Fact]
    public async Task rejects_an_unknown_query_parameter()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/query?bogus=1");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = json.GetProperty("error");
        Assert.Equal("invalid_query", error.GetProperty("code").GetString());
        Assert.Equal(
            "Unrecognized key(s) in object: 'bogus'",
            error.GetProperty("details").GetProperty("formErrors")[0].GetString());
    }

    [Fact]
    public async Task rejects_an_empty_query_parameter()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/query?status=");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "must not be empty",
            json.GetProperty("error").GetProperty("details")
                .GetProperty("fieldErrors").GetProperty("status")[0].GetString());
    }

    // ---- Auth -------------------------------------------------------------

    [Fact]
    public async Task rejects_a_missing_authorization_header_with_missing_token()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/__kernel-probe/me");
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Missing access token", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task treats_a_non_bearer_scheme_as_a_missing_token()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/__kernel-probe/me");
        request.Headers.Add("Authorization", "Basic abc");

        // Act
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert — Node checks the "Bearer " prefix, so this is NOT invalid_token.
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task rejects_a_bad_bearer_token_with_invalid_token()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/__kernel-probe/me");
        request.Headers.Add("Authorization", "Bearer xxx");

        // Act
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_token", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Invalid or expired access token",
            json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task accepts_a_node_shaped_token_and_exposes_the_user_id()
    {
        // Arrange — Node signs { sub, email } with HS256 and no issuer/audience.
        var userId = "6a78c216aa461ae1dc64ab59";
        var request = new HttpRequestMessage(HttpMethod.Get, "/__kernel-probe/me");
        request.Headers.Add("Authorization", $"Bearer {NodeShapedToken(userId, "kernel@probe.com")}");

        // Act
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(userId, json.GetProperty("id").GetString());
        Assert.Equal("kernel@probe.com", json.GetProperty("email").GetString());
    }

    // ---- Raw body ---------------------------------------------------------

    [Fact]
    public async Task reads_a_raw_body_with_x_headers()
    {
        // Arrange
        var content = new ByteArrayContent(new byte[64]);
        content.Headers.TryAddWithoutValidation("Content-Type", "audio/m4a");
        var request = new HttpRequestMessage(HttpMethod.Post, "/__kernel-probe/raw") { Content = content };
        request.Headers.Add("x-voice-note-duration-ms", "1200");

        // Act
        var response = await _factory.CreateApiClient().SendAsync(request);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(64, json.GetProperty("bytes").GetInt32());
        Assert.Equal("audio/m4a", json.GetProperty("contentType").GetString());
        Assert.Equal(1200, json.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task rejects_an_empty_raw_body()
    {
        // Act
        var response = await _factory.CreateApiClient()
            .PostAsync("/__kernel-probe/raw", new ByteArrayContent(Array.Empty<byte>()));
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("empty_body", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task rejects_an_oversize_raw_body_with_a_friendly_400()
    {
        // Arrange — over the friendly limit but under the 2x transport ceiling, which
        // is exactly the case Node's doubled express.raw() limit exists to catch.
        var content = new ByteArrayContent(new byte[1500]);
        content.Headers.TryAddWithoutValidation("Content-Type", "audio/m4a");

        // Act
        var response = await _factory.CreateApiClient().PostAsync("/__kernel-probe/raw", content);
        var json = await ReadJsonAsync(response);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("payload_too_large", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- helpers ----------------------------------------------------------

    internal static string NodeShapedToken(string sub, string email, TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(KernelWebApplicationFactory.TestJwtSecret));
        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, sub),
                new Claim(JwtRegisteredClaimNames.Email, email),
            },
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(15)),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string? Single(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) ? string.Join(", ", values) : null;

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
