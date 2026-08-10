using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Life_Admin_Autopilot_Backend.Kernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <c>POST /ai/ask</c> as an HTTP response, through the kernel's host — the header
/// block, the frame sequence, the quota settlement, and THE RULE about which side of
/// the flush a failure lands on.
///
/// <para>
/// The provider is scripted, not Langflow: no Langflow instance was reachable while
/// this was written, so what these tests prove is that the ROUTE produces the
/// measured contract given a well-behaved provider. Whether Langflow really emits
/// what <c>LangflowTranslationTests</c> feeds the translator is not established here
/// and could not be.
/// </para>
/// </summary>
public sealed class AiStreamContractTests
{
    // ---- the header block ---------------------------------------------------

    [Fact]
    public async Task answers_200_text_event_stream_chunked()
    {
        using var factory = Factory(new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()));

        var response = await AskAsync(factory);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        // Only the DIRECTIVES are asserted here. Cache-Control is a known header, so
        // HttpClient parses it into a CacheControlHeaderValue and re-serialises it
        // in its own order — even GetValues returns the reparsed form, so the literal
        // the reference sends cannot be observed through this client at all. The
        // exact string, in order, is asserted where it is actually set:
        // AiSseWriterTests.sets_the_four_headers_in_the_reference_order.
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.True(response.Headers.CacheControl.NoTransform);

        // Content-Length is deliberately NOT asserted here. TestServer completes the
        // response into a buffer and stamps a length on it, so the value seen through
        // this client says nothing about whether Kestrel would chunk. That the writer
        // leaves it unset — which is what makes Kestrel chunk — is asserted in
        // AiSseWriterTests.leaves_content_length_unset_so_the_body_is_chunked.
    }

    [Fact]
    public async Task keeps_all_twelve_helmet_headers_on_the_stream()
    {
        using var factory = Factory(new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()));

        var response = await AskAsync(factory);

        // Opening the stream must not disturb the security headers — they are set on
        // the way in and the SSE writer only adds to them.
        foreach (var (name, value) in HelmetHeadersMiddleware.Defaults)
        {
            Assert.True(response.Headers.TryGetValues(name, out var actual), $"{name} was missing");
            Assert.Equal(value, actual!.Single());
        }
    }

    // ---- the frame sequence -------------------------------------------------

    [Fact]
    public async Task emits_sources_then_tokens_then_done_then_quota()
    {
        using var factory = Factory(new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()));

        var frames = await FramesAsync(await AskAsync(factory));

        Assert.Equal(
            new[] { "sources", "token", "token", "done", "quota" },
            frames.Select(f => f.GetProperty("type").GetString()).ToArray());
    }

    [Fact]
    public async Task synthesises_the_quota_frame_after_done_so_it_is_last()
    {
        using var factory = Factory(new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()));

        var frames = await FramesAsync(await AskAsync(factory));
        var quota = frames[^1];

        // The provider never emits this one — the route does, right after `done`.
        Assert.Equal("quota", quota.GetProperty("type").GetString());
        Assert.Equal("free", quota.GetProperty("tier").GetString());

        var meter = quota.GetProperty("quotas")[0];
        Assert.Equal("message", meter.GetProperty("kind").GetString());
        Assert.Equal(1, meter.GetProperty("used").GetInt32());
        Assert.EndsWith("Z", meter.GetProperty("resetAt").GetString());
    }

    [Fact]
    public async Task every_frame_is_a_bare_data_line_with_no_sse_field_names()
    {
        using var factory = Factory(new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()));

        var body = await (await AskAsync(factory)).Content.ReadAsStringAsync();

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith("data: ", line);
        }

        Assert.DoesNotContain("event:", body);
        Assert.DoesNotContain("id:", body);
        Assert.DoesNotContain("retry:", body);
    }

    [Fact]
    public async Task passes_the_validated_question_and_the_callers_token_to_the_provider()
    {
        var provider = new ScriptedAiProvider(ScriptedAiProvider.HappyTurn());
        using var factory = Factory(provider);

        await AskAsync(factory, """{"question":"  What is due?  ","timezone":"Europe/London"}""");

        var ask = Assert.Single(provider.Asks);

        // Trimmed by the zod chain before it ever reaches a provider.
        Assert.Equal("What is due?", ask.Question);
        Assert.Equal("Europe/London", ask.Timezone);

        // Forwarded so an agent whose tools call this API back acts AS the user.
        Assert.False(string.IsNullOrEmpty(ask.AccessToken));
        Assert.DoesNotContain("Bearer", ask.AccessToken!);
    }

    // ---- THE RULE: which side of the flush ----------------------------------

    [Fact]
    public async Task a_failure_before_the_flush_is_an_ordinary_json_http_error()
    {
        // Limit 0 refuses the reservation, and that happens BEFORE the headers go
        // out — so it is a real 402 a client can branch on by status.
        using var factory = Factory(
            new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()),
            dailyLimit: 0);

        var response = await AskAsync(factory);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("quota_exceeded", error.GetProperty("code").GetString());
        Assert.Equal("message", error.GetProperty("details").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task a_failure_after_the_flush_is_an_error_frame_inside_a_200()
    {
        using var factory = Factory(new ScriptedAiProvider(
            ScriptedAiProvider.HappyTurn(),
            new AppException(502, "langflow_unavailable", "Could not reach the agent."),
            throwAfter: 2));

        var response = await AskAsync(factory);
        var frames = await FramesAsync(response);

        // The status line was committed at the flush, so the ONLY way to report this
        // is inside the stream. Clients branch on Content-Type.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var error = frames[^1];
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("langflow_unavailable", error.GetProperty("code").GetString());
        Assert.Equal("Could not reach the agent.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task an_unexpected_exception_becomes_internal_error_with_its_own_message()
    {
        using var factory = Factory(new ScriptedAiProvider(
            ScriptedAiProvider.HappyTurn(),
            new InvalidOperationException("something broke"),
            throwAfter: 1));

        var error = (await FramesAsync(await AskAsync(factory)))[^1];

        // Node: `err instanceof AppError ? err.code : 'internal_error'`, and the
        // message is the Error's own.
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.Equal("something broke", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task a_turn_that_fails_before_done_emits_no_quota_frame()
    {
        using var factory = Factory(new ScriptedAiProvider(
            ScriptedAiProvider.HappyTurn(),
            new InvalidOperationException("boom"),
            throwAfter: 1));

        var frames = await FramesAsync(await AskAsync(factory));

        Assert.DoesNotContain(frames, f => f.GetProperty("type").GetString() == "quota");
    }

    // ---- quota settlement ---------------------------------------------------

    [Fact]
    public async Task consumes_exactly_one_slot_for_a_turn_that_reaches_done()
    {
        var store = new InMemoryUsageQuotaStore();
        using var factory = Factory(new ScriptedAiProvider(ScriptedAiProvider.HappyTurn()), store: store);

        await AskAsync(factory);

        Assert.Equal(1, store.UsedFor(Bucket()));
    }

    [Fact]
    public async Task refunds_the_slot_when_the_turn_never_reaches_done()
    {
        var store = new InMemoryUsageQuotaStore();
        using var factory = Factory(
            new ScriptedAiProvider(
                ScriptedAiProvider.HappyTurn(),
                new InvalidOperationException("boom"),
                throwAfter: 1),
            store: store);

        await AskAsync(factory);

        // Node's finally releases whenever reachedDone is false. Without it a
        // crashed stream permanently burns a slot, and a flapping provider eats a
        // user's whole day of quota on turns that produced nothing.
        Assert.Equal(0, store.UsedFor(Bucket()));
    }

    // ---- the not-configured path is untouched -------------------------------

    [Fact]
    public async Task still_answers_the_reference_503_when_no_provider_is_configured()
    {
        // No Langflow settings, no GEMINI_API_KEY: the container keeps
        // NotConfiguredAiProvider and the route is byte-identical to the reference.
        // This is the parity target, and adding a second provider must not move it.
        using var factory = new AiWebApplicationFactory();

        var response = await AskAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("ai_not_configured", error.GetProperty("code").GetString());
        Assert.Equal(
            "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task selects_the_langflow_provider_only_when_both_url_and_flow_are_set()
    {
        // Half-configured is NOT configured: a base URL with no flow id has nothing
        // to call, and silently degrading to a broken stream would be worse than the
        // honest 503.
        using var halfway = new AiWebApplicationFactory()
            .With("LANGFLOW_BASE_URL", "http://127.0.0.1:7860") as AiWebApplicationFactory;

        var response = await AskAsync(halfway!);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly string UserId = ObjectId.GenerateNewId().ToString();

    private static UsageQuotaBucket Bucket() => new(
        MongoCollections.AiUsageCounters,
        ObjectId.Parse(UserId),
        new Dictionary<string, string>
        {
            ["date"] = UsageQuotaBuckets.UtcDate(),
            ["kind"] = "message",
        },
        AiQuotaService.DefaultFreeDaily);

    private static StreamFactory Factory(
        ScriptedAiProvider provider,
        int? dailyLimit = null,
        InMemoryUsageQuotaStore? store = null)
    {
        // A limit of 0 is not `positive()`, so setting AI_QUOTA_FREE_DAILY=0 would
        // fall back to 30 — refuse by giving the store no headroom instead.
        var quotaStore = store ?? new InMemoryUsageQuotaStore();
        var denyEverything = dailyLimit == 0;

        return new StreamFactory(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IAiProvider>(_ => provider));
            services.Replace(ServiceDescriptor.Singleton<IUsageQuotaStore>(
                _ => denyEverything ? new ExhaustedQuotaStore() : quotaStore));
        });
    }

    private static Task<HttpResponseMessage> AskAsync(
        KernelWebApplicationFactory factory,
        string body = """{"question":"What is due this week?"}""")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/ai/ask")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", KernelPipelineTests.NodeShapedToken(UserId, "ai@probe.com"));

        return factory.CreateApiClient().SendAsync(request);
    }

    private static async Task<IReadOnlyList<JsonElement>> FramesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        return body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(record => record.StartsWith("data: ", StringComparison.Ordinal))
            .Select(record => JsonDocument.Parse(record["data: ".Length..]).RootElement)
            .ToList();
    }

    /// <summary>A store with no headroom, for the pre-flush 402.</summary>
    private sealed class ExhaustedQuotaStore : IUsageQuotaStore
    {
        public Task<UsageQuotaAdmission> TryAdmitAsync(UsageQuotaBucket bucket, CancellationToken ct = default) =>
            Task.FromResult(UsageQuotaAdmission.Deny(bucket.Limit, bucket.Limit));

        public Task<int> ReadUsedAsync(UsageQuotaBucket bucket, CancellationToken ct = default) =>
            Task.FromResult(bucket.Limit);

        public Task RecordAsync(UsageQuotaBucket bucket, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReleaseAsync(UsageQuotaBucket bucket, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// The kernel host with two registrations swapped.
    ///
    /// <para>
    /// <c>ConfigureTestServices</c> runs AFTER the app's own registration, which is
    /// what lets <c>Replace</c> win over the <c>TryAdd</c> in <c>AiFeature</c>. It is
    /// also why this is a subclass rather than <c>WithWebHostBuilder</c> — that
    /// returns a base-typed factory and loses the AI database override below.
    /// </para>
    /// </summary>
    private sealed class StreamFactory : KernelWebApplicationFactory
    {
        private readonly Action<IServiceCollection> _configure;

        public StreamFactory(Action<IServiceCollection> configure)
        {
            _configure = configure;

            // Its own database, so a parallel slice's run cannot see these rows —
            // though with the quota store faked, these tests touch Mongo only if a
            // scripted provider does, and none of them does.
            With("MongoDbSettings:DatabaseName", "kitto_parity_dotnet_m_stream");
            With("GEMINI_API_KEY", string.Empty);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(_configure);
        }
    }
}
