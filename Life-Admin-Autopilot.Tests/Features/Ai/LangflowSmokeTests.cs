using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <b>One real turn against a live Langflow.</b> The only test in this slice that
/// touches the boundary every defect in it has come from.
///
/// <para>
/// <b>Why it exists.</b> Every bug found in this adapter came from an assumption
/// about what Langflow emits — that a <c>tool_use</c> block carries an id, that a row
/// arrives once, that an <c>output</c> means the call ran. None was wrong about the
/// translation; each was fed a wire shape that did not exist. The synthetic tests now
/// encode measured shapes, but they will keep passing if Langflow changes on a
/// version bump, precisely because they supply the input. This one does not supply
/// the input, so it is the only thing that can notice.
/// </para>
///
/// <para>
/// <b>OPT-IN, and silent by default.</b> It runs only when
/// <c>LANGFLOW_SMOKE_BASE_URL</c> and <c>LANGFLOW_SMOKE_FLOW_ID</c> are set, so it
/// never fails a normal run. <b>A green suite is therefore NOT evidence that this
/// ran</b> — see the known gap in the body:
/// </para>
///
/// <code>
/// LANGFLOW_SMOKE_BASE_URL=http://127.0.0.1:7860 \
/// LANGFLOW_SMOKE_FLOW_ID=&lt;flow&gt; \
/// LANGFLOW_SMOKE_INPUT_NODE=PlanningInput-v4 \
/// dotnet test --filter FullyQualifiedName~LangflowSmokeTests
/// </code>
///
/// <para>
/// <b>Executed and passing</b> against live Langflow 1.11.2 (12s — a real round
/// trip, not a silent skip), and verified to FAIL against a bogus flow id, so the
/// connection has teeth rather than only the assertions.
/// </para>
///
/// <para>
/// <b>What it asserts is deliberately prompt-agnostic</b> — the four invariants that
/// actually broke, all of which must hold for any turn, so the test needs no seeded
/// data and causes no writes beyond the conversation row. It does NOT confirm
/// anything: a gated tool is only dry-run before confirmation, so asking about a bulk
/// delete here would still delete nothing.
/// </para>
/// </summary>
public sealed class LangflowSmokeTests
{
    [Fact]
    public async Task one_real_turn_still_matches_the_shapes_the_translator_expects()
    {
        var options = SmokeOptions();
        var database = TryGetDatabase();

        // KNOWN GAP: this returns rather than skipping, so an unconfigured run
        // reports as PASSED in 111ms. That is the same false-green this test exists
        // to prevent, one level up — on the day the credentials stop being injected
        // the summary still reads green and nobody notices the boundary went
        // unchecked. xunit 2.9.3 has no dynamic skip (`Assert.Skip` binds to
        // AsyncEnumerable.Skip); fixing it properly needs Xunit.SkippableFact or
        // xunit v3. Until then the mitigation is that LangflowTurnInvariants also
        // runs against the stubbed turn in LangflowProviderTests, so the assertions
        // are always exercised — only the live connection can go silently unrun.
        if (options is null || database is null)
        {
            return;
        }

        var provider = new LangflowAiProvider(
            new SmokeHttpClientFactory(),
            options!,
            SmokeBinding(),
            new AiConversationRepository(database!));

        var events = new List<AiStreamEvent>();
        var request = new AiAskRequest(
            ObjectId.GenerateNewId().ToString(),
            "What is on my list this week?",
            "Europe/London");

        await foreach (var value in provider.AskAsync(request))
        {
            events.Add(value);
        }

        LangflowTurnInvariants.AssertTurnShape(events);
    }

    // ---- opt-in configuration ----------------------------------------------

    private static LangflowOptions? SmokeOptions()
    {
        var options = LangflowOptions.FromConfiguration(SmokeConfiguration());
        return options.IsConfigured ? options : null;
    }

    private static LangflowInputBinding SmokeBinding() =>
        LangflowInputBinding.FromConfiguration(SmokeConfiguration());

    /// <summary>
    /// Reads the SMOKE-prefixed variables into the adapter's own keys, so pointing
    /// this at an instance cannot accidentally reconfigure anything else.
    /// </summary>
    private static IConfiguration SmokeConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Langflow:BaseUrl"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_BASE_URL"),
                ["Ai:Langflow:FlowId"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_FLOW_ID"),
                ["Ai:Langflow:ApiKey"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_API_KEY"),
                ["Ai:Langflow:InputNode"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_INPUT_NODE"),
            })
            .Build();

    private static IMongoDatabase? TryGetDatabase()
    {
        try
        {
            MongoKernelConventions.Register();

            var client = new MongoClient(
                $"{KernelWebApplicationFactory.ParityMongoUri}/?serverSelectionTimeoutMS=800");
            var database = client.GetDatabase("kitto_parity_dotnet_m_smoke");
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class SmokeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>
/// The invariants one Langflow turn must satisfy, each one a bug that actually
/// shipped in this adapter.
///
/// <para>
/// Deliberately NOT on the test class: a public non-test method there is an xUnit1013
/// warning, and more usefully, these belong to the turn rather than to the test that
/// happens to run them. <c>LangflowProviderTests</c> runs them against a stubbed turn
/// so the assertions are proven even where no Langflow exists — which is what makes a
/// live pass mean something later.
/// </para>
/// </summary>
public static class LangflowTurnInvariants
{
    public static void AssertTurnShape(IReadOnlyList<AiStreamEvent> events)
    {
        Assert.NotEmpty(events);

        // 1. The turn opens with sources and closes with done.
        Assert.Equal(AiStreamEvents.SourcesKind, events[0].Kind);
        Assert.Equal(AiStreamEvents.DoneKind, events[^1].Kind);

        var calls = events.Where(e => e.Kind == AiStreamEvents.ToolCallKind).ToList();
        var callIds = calls.Select(c => (string)c.Payload["callId"]!).ToList();

        // 2. No tool is announced twice. A redelivered add_message row whose blocks
        //    carry no id used to mint a fresh id each time — seven frames for one
        //    call, and seven confirmation cards for one bulk delete.
        Assert.Equal(callIds.Count, callIds.Distinct(StringComparer.Ordinal).Count());

        var resultIds = events
            .Where(e => e.Kind == AiStreamEvents.ToolResultKind)
            .Select(e => (string)e.Payload["callId"]!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var call in calls)
        {
            var callId = (string)call.Payload["callId"]!;

            // 3. A gated call is never resolved by the stream — Langflow's dry-run
            //    output is a preview, and treating it as an outcome killed every
            //    confirmation. The confirm route emits the only tool_result.
            if ((bool)call.Payload["needsConfirmation"]!)
            {
                Assert.DoesNotContain(callId, resultIds);
            }
        }

        // 4. Every result belongs to a call that was announced.
        Assert.All(resultIds, id => Assert.Contains(id, callIds));
    }

}
