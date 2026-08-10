using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>Its own database, so a parallel slice's run cannot see these rows.</summary>
public sealed class AiWebApplicationFactory : KernelWebApplicationFactory
{
    public const string AiDatabase = "kitto_parity_dotnet_i_tests";

    public AiWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", AiDatabase);

        // The parity target is Node with GEMINI_API_KEY empty, which is also the
        // default here — set explicitly so a developer's exported key cannot make
        // the suite take the other branch.
        With("GEMINI_API_KEY", string.Empty);
    }
}

/// <summary>
/// The six AI-shell routes, against behaviour captured live on the reference at
/// <c>:4200</c>.
///
/// <para>
/// Split the way the Matters and notifications suites are: <b>auth and validation
/// cases touch no database</b> and always run, while the two conversation reads
/// need Mongo and are covered separately in
/// <see cref="AiConversationRepositoryTests"/>, which skips when the parity
/// instance is down.
/// </para>
/// </summary>
public sealed class AiEndpointTests : IClassFixture<AiWebApplicationFactory>
{
    private readonly AiWebApplicationFactory _factory;

    public AiEndpointTests(AiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Auth --------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/ai/conversation")]
    [InlineData("POST", "/ai/conversation/reset")]
    [InlineData("GET", "/ai/quota")]
    [InlineData("POST", "/ai/ask")]
    [InlineData("POST", "/ai/tools/confirm/xyz")]
    [InlineData("POST", "/ai/voice/transcribe")]
    public async Task requires_a_token_on_every_route(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await _factory.CreateApiClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("missing_token", error.GetProperty("code").GetString());
        Assert.Equal("Missing access token", error.GetProperty("message").GetString());
    }

    // ---- POST /ai/ask ------------------------------------------------------

    [Fact]
    public async Task ask_short_circuits_to_its_own_503_message()
    {
        var response = await PostJsonAsync("/ai/ask", """{"question":"What is due this week?"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("ai_not_configured", error.GetProperty("code").GetString());

        // Carries the trailing " to enable." that the transcribe message omits.
        Assert.Equal(
            "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ask_validates_the_body_BEFORE_checking_availability()
    {
        // The gate order is observable: a bad payload reports what is wrong with it
        // rather than masking as the 503. (Contrast /me/tasks/summarize, and
        // /ai/voice/transcribe below, which check availability first.)
        var response = await PostJsonAsync("/ai/ask", """{"question":"   "}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal("Invalid ask payload.", error.GetProperty("message").GetString());
        Assert.Equal(
            "question cannot be empty",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("question")[0].GetString());
    }

    [Theory]
    // Absent, wrong type, and the two custom length messages.
    [InlineData("{}", "question", "Required")]
    [InlineData("""{"question":null}""", "question", "Expected string, received null")]
    [InlineData("""{"question":123}""", "question", "Expected string, received number")]
    [InlineData("""{"question":true}""", "question", "Expected string, received boolean")]
    // zod's enum has TWO forms. A non-member STRING gets the "Invalid enum value."
    // prefix; a non-string gets the invalid_type wording and the JS type name.
    [InlineData("""{"question":"hi","scope":"team"}""", "scope",
        "Invalid enum value. Expected 'personal', received 'team'")]
    [InlineData("""{"question":"hi","scope":null}""", "scope", "Expected 'personal', received null")]
    [InlineData("""{"question":"hi","scope":7}""", "scope", "Expected 'personal', received number")]
    [InlineData("""{"question":"hi","scope":[]}""", "scope", "Expected 'personal', received array")]
    [InlineData("""{"question":"hi","scope":{}}""", "scope", "Expected 'personal', received object")]
    [InlineData("""{"question":"hi","timezone":""}""", "timezone",
        "String must contain at least 1 character(s)")]
    [InlineData("""{"question":"hi","timezone":null}""", "timezone", "Expected string, received null")]
    public async Task ask_reports_each_zod_message_verbatim(string body, string field, string message)
    {
        var response = await PostJsonAsync("/ai/ask", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var fieldErrors = (await ReadJsonAsync(response))
            .GetProperty("error").GetProperty("details").GetProperty("fieldErrors");

        Assert.Equal(message, fieldErrors.GetProperty(field)[0].GetString());
    }

    [Fact]
    public async Task ask_reports_a_too_long_question_and_timezone()
    {
        var longQuestion = await PostJsonAsync(
            "/ai/ask", JsonSerializer.Serialize(new { question = new string('x', 2001) }));

        Assert.Equal(
            "question cannot exceed 2000 characters",
            (await FieldErrorsAsync(longQuestion)).GetProperty("question")[0].GetString());

        var longZone = await PostJsonAsync(
            "/ai/ask", JsonSerializer.Serialize(new { question = "hi", timezone = new string('z', 65) }));

        Assert.Equal(
            "String must contain at most 64 character(s)",
            (await FieldErrorsAsync(longZone)).GetProperty("timezone")[0].GetString());
    }

    [Fact]
    public async Task ask_trims_the_question_before_measuring_it()
    {
        // `.trim()` sits FIRST in the chain, so 2000 characters plus surrounding
        // whitespace is still within the ceiling.
        var response = await PostJsonAsync(
            "/ai/ask", JsonSerializer.Serialize(new { question = "  " + new string('x', 2000) + "  " }));

        // Past validation, so it reaches the availability gate.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ask_reports_every_issue_in_one_response()
    {
        var response = await PostJsonAsync("/ai/ask", """{"scope":"team","timezone":""}""");

        var fieldErrors = await FieldErrorsAsync(response);

        Assert.Equal("Required", fieldErrors.GetProperty("question")[0].GetString());
        Assert.Equal(
            "Invalid enum value. Expected 'personal', received 'team'",
            fieldErrors.GetProperty("scope")[0].GetString());
        Assert.Equal(
            "String must contain at least 1 character(s)",
            fieldErrors.GetProperty("timezone")[0].GetString());
    }

    [Fact]
    public async Task ask_strips_unknown_keys_rather_than_rejecting_them()
    {
        // askBodySchema is a plain zod object, NOT .strict().
        var response = await PostJsonAsync("/ai/ask", """{"question":"hi","bogus":1}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ask_treats_an_array_body_as_a_whole_object_type_issue()
    {
        var response = await PostJsonAsync("/ai/ask", "[]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var details = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details");
        Assert.Equal("Expected object, received array", details.GetProperty("formErrors")[0].GetString());
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task ask_answers_500_for_a_body_body_parser_refuses(string body)
    {
        // express.json() runs with strict:true — a top-level primitive is a
        // SyntaxError, and so is malformed JSON. Both fall through the error
        // handler as 500, NOT 400. Verified live.
        var response = await PostJsonAsync("/ai/ask", body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task ask_ignores_a_body_express_would_not_parse(string? contentType)
    {
        // Only application/json is parsed at all; anything else leaves req.body = {}
        // and the schema then reports `question: Required`.
        var request = new HttpRequestMessage(HttpMethod.Post, "/ai/ask")
        {
            Content = new StringContent("""{"question":"hi"}"""),
        };
        request.Content.Headers.ContentType =
            contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        Authorize(request);

        var response = await _factory.CreateApiClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Required", (await FieldErrorsAsync(response)).GetProperty("question")[0].GetString());
    }

    // ---- POST /ai/tools/confirm/{callId} -----------------------------------

    [Fact]
    public async Task confirm_is_404_not_503_when_no_pending_call_exists()
    {
        // There is NO isAiConfigured() gate on this route. With no key no pending
        // call can ever have been created, so every call falls through to the
        // durable-record lookup. Verified live.
        var response = await PostJsonAsync("/ai/tools/confirm/xyz", """{"action":"confirm"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("pending_call_not_found", error.GetProperty("code").GetString());
        Assert.Equal("This confirmation has expired.", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("6a78c437aa461ae1dc64ffff")]
    [InlineData("2f1b0a44-6c9d-4a1e-8f22-0d3c7e9a1b55")]
    public async Task confirm_never_produces_a_cast_error_404(string callId)
    {
        // callId is a randomUUID() string, not an ObjectId — an ObjectId-shaped
        // value is not special and a malformed one is the ordinary not-found.
        var response = await PostJsonAsync($"/ai/tools/confirm/{callId}", """{"action":"decline"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "pending_call_not_found",
            (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{}", "Required")]
    [InlineData("""{"action":"nope"}""", "Invalid enum value. Expected 'confirm' | 'decline', received 'nope'")]
    [InlineData("""{"action":null}""", "Expected 'confirm' | 'decline', received null")]
    [InlineData("""{"action":1}""", "Expected 'confirm' | 'decline', received number")]
    public async Task confirm_reports_each_action_message_verbatim(string body, string message)
    {
        var response = await PostJsonAsync("/ai/tools/confirm/xyz", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("invalid_body", error.GetProperty("code").GetString());
        Assert.Equal("Invalid confirm payload.", error.GetProperty("message").GetString());
        Assert.Equal(
            message,
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("action")[0].GetString());
    }

    [Fact]
    public async Task confirm_validates_the_body_before_the_record_lookup()
    {
        // A bad action is 400 even though the call would 404 — the schema runs first.
        var response = await PostJsonAsync("/ai/tools/confirm/xyz", """{"action":"nope"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- POST /ai/voice/transcribe -----------------------------------------

    [Fact]
    public async Task transcribe_uses_a_message_two_words_shorter_than_asks()
    {
        var response = await PostAudioAsync(new byte[1000], "audio/m4a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var error = (await ReadJsonAsync(response)).GetProperty("error");
        Assert.Equal("ai_not_configured", error.GetProperty("code").GetString());

        // NO trailing " to enable." — this differs from /ai/ask by exactly two
        // words, and the differ compares it literally.
        Assert.Equal(
            "AI is not configured. Set GEMINI_API_KEY in server/.env.",
            error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(0)]           // would otherwise be 400 empty_body
    [InlineData(7 * 1024 * 1024)] // would otherwise be 400 payload_too_large
    public async Task transcribe_checks_availability_BEFORE_the_payload_checks(int size)
    {
        // The gate order is inverted relative to /ai/ask, and it is observable:
        // an 11MB body on the reference returns 503, not 400. Verified live.
        var response = await PostAudioAsync(new byte[size], "audio/m4a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task transcribe_answers_500_above_the_doubled_raw_ceiling()
    {
        // express.raw() runs as middleware with limit = 2 x 6MiB, so a larger body
        // never reaches the handler and body-parser's failure surfaces as 500 —
        // beating even the availability gate. Verified live at 13MB.
        var response = await PostAudioAsync(new byte[13_000_000], "audio/m4a");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task transcribe_leaves_a_non_audio_body_unread()
    {
        // express.raw() matches only the four audio types, so a 13MB text/plain body
        // is never buffered and the availability gate answers first.
        var response = await PostAudioAsync(new byte[13_000_000], "text/plain");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("audio/m4a")]
    [InlineData("audio/mp4")]
    [InlineData("audio/aac")]
    [InlineData("application/octet-stream")]
    [InlineData("audio/mp4; codecs=mp4a")]
    [InlineData(null)]
    public async Task transcribe_accepts_every_raw_content_type(string? contentType)
    {
        var response = await PostAudioAsync(new byte[100], contentType);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------

    private static readonly string UserId = ObjectId.GenerateNewId().ToString();

    private static void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", KernelPipelineTests.NodeShapedToken(UserId, "ai@probe.com"));

    private Task<HttpResponseMessage> PostJsonAsync(string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Authorize(request);

        return _factory.CreateApiClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> PostAudioAsync(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);

        var request = new HttpRequestMessage(HttpMethod.Post, "/ai/voice/transcribe") { Content = content };
        Authorize(request);

        return _factory.CreateApiClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> FieldErrorsAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("details").GetProperty("fieldErrors");
}
