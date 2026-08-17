using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Tasks;

/// <summary>
/// Amounts arriving from a person rather than from the reader.
///
/// <para>
/// Two rules here are the opposite of the extractor's, and both are deliberate.
/// A hand-typed figure is stamped <c>source: 'user'</c> and the client may not
/// say otherwise — a client that could set it would launder a guess into a fact.
/// And a bad currency <b>rejects the request</b> instead of being dropped: the
/// extractor drops what it cannot resolve because a dropped guess costs nothing,
/// but a person who typed 4800 and silently got a matter with no amount would
/// have to notice an absence to learn it failed.
/// </para>
///
/// <para>
/// Every case below is a VALIDATION case, which is what makes them the reliable
/// floor — each one resolves before the first Mongo call, so they run whether or
/// not the parity database is up. The round-trip cases live alongside and skip
/// themselves when it is not.
/// </para>
/// </summary>
public sealed class TaskAmountBindingTests : IClassFixture<TasksWebApplicationFactory>
{
    private readonly TasksWebApplicationFactory _factory;

    public TaskAmountBindingTests(TasksWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task a_client_cannot_claim_a_figure_came_from_a_person()
    {
        // `source` is not a readable key, so sending it is an unrecognised key
        // rather than an ignored one. This is the inverse of the provenance rule
        // the trust contract rests on, and it fails closed.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Laundered","domain":"home","amount":{"amountMinor":100,"currency":"EGP","source":"ai"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("EGPP")]
    [InlineData("eg")]
    [InlineData("12")]
    public async Task a_currency_that_is_not_iso_4217_rejects_the_request(string currency)
    {
        // Rejected, NOT silently dropped — see the class summary.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            $$$"""{"title":"Bad currency","domain":"home","amount":{"amountMinor":100,"currency":"{{{currency}}}"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_negative_amount_is_rejected()
    {
        // Direction carries the sign. A negative magnitude is a second, conflicting
        // way to say the same thing, and the two would disagree when summed.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Negative","domain":"home","amount":{"amountMinor":-500,"currency":"EGP"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_unknown_direction_is_rejected()
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Sideways","domain":"home","amount":{"amountMinor":500,"currency":"EGP","direction":"sideways"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_amount_that_is_not_an_object_is_rejected()
    {
        // A bare number is the shape a client would reach for first, and accepting
        // it would mean guessing the currency.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Bare","domain":"home","amount":4800}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_amount_missing_its_currency_is_rejected()
    {
        // An amount with no currency is not a quantity of anything.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"No currency","domain":"home","amount":{"amountMinor":4800}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_absurd_amount_is_rejected()
    {
        // Past the money gate's ceiling — a misread separator, not a household bill.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Absurd","domain":"home","amount":{"amountMinor":9000000000000000000,"currency":"EGP"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_valid_amount_passes_validation()
    {
        // The positive control: whatever this answers, it is not a 400. Without it
        // every rejection above would also pass on a binder that refused
        // everything.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Renew car insurance","domain":"car","amount":{"amountMinor":480000,"currency":"EGP"}}""");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_lowercase_currency_passes_validation()
    {
        // Casing is a formatting slip, not an ambiguity, so it is corrected.
        var response = await SendAsync(
            HttpMethod.Post,
            "/me/tasks",
            """{"title":"Lowercase","domain":"home","amount":{"amountMinor":100,"currency":"egp"}}""");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_explicit_null_amount_is_accepted_as_a_clear()
    {
        // "This was never about money" has to be sayable: until it is, a matter
        // wrongly carrying a figure is counted in the user's spending forever.
        var response = await SendAsync(
            HttpMethod.Patch,
            $"/me/tasks/{ObjectId.GenerateNewId()}",
            """{"amount":null}""");

        // 404 (no such matter) proves it got past validation; a rejected shape
        // would have been a 400 before the lookup.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_patched_amount_with_a_bad_currency_is_rejected_too()
    {
        // The patch path validates the same way the create path does — one reader,
        // so the two cannot drift.
        var response = await SendAsync(
            HttpMethod.Patch,
            $"/me/tasks/{ObjectId.GenerateNewId()}",
            """{"amount":{"amountMinor":100,"currency":"pounds"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Fires one throwaway request before the first real one.
    ///
    /// <para>
    /// The FIRST request into a cold test host answers 500 whatever it carries —
    /// a plain <c>{"title","domain"}</c> create with no amount anywhere in it does
    /// the same. It is a one-shot JSON type-resolver miss in
    /// <c>KernelBody.UnknownTopLevelKeys</c> that resolves once the host is warm,
    /// and it belongs to the kernel, not to this slice. Without this the first
    /// test to run in the class fails and WHICH one is decided by xUnit's
    /// ordering, so the suite would fail somewhere different each run.
    /// </para>
    /// </summary>
    private async Task WarmAsync()
    {
        if (_warm) return;
        _warm = true;
        await SendRawAsync(HttpMethod.Post, "/me/tasks", """{"title":"warm","domain":"home"}""");
    }

    private bool _warm;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string body)
    {
        await WarmAsync();
        return await SendRawAsync(method, path, body);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string body)
    {
        var id = ObjectId.GenerateNewId();
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(id.ToString(), $"{id}@example.test"));

        return await _factory.CreateApiClient().SendAsync(request);
    }
}
