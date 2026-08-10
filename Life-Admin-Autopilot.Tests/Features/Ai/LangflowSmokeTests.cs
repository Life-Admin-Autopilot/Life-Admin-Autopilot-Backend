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
/// never fails a normal run and never pretends to have checked anything:
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
/// <b>NOT YET EXECUTED.</b> Written from a worktree with no Langflow reachable, so
/// the live path has never run once. The assertions below are the same invariants
/// <c>LangflowProviderTests</c> checks against a stub, and those do run — but until
/// someone points this at a real instance, treat it as unproven scaffolding rather
/// than as coverage. A skipping test that is quietly broken is worse than no test.
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

        // Partial configuration is ALWAYS a failure, never a silent pass. A base URL
        // with no flow id means someone meant to run this and the wiring broke — the
        // exact case where returning early would hide the problem.
        AssertConfigurationIsUsable(options, database);

        if (options is null || database is null)
        {
            return;
        }

        var provider = new LangflowAiProvider(
            new SmokeHttpClientFactory(),
            options,
            SmokeBinding(),
            new AiConversationRepository(database));

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

    /// <summary>
    /// The honesty guard, and the reason this test does not simply return early.
    ///
    /// <para>
    /// <b>The failure mode being defended against</b> is a CI summary reporting a
    /// pass for a test that did nothing, on the day the credentials stop being
    /// injected — nobody reads a green line. A real "skipped" result would say so,
    /// but <c>Assert.Skip</c> and <c>SkipTestWithoutData</c> are both xUnit v3, and
    /// this suite is on 2.9.3 where <c>Skip</c> is a compile-time constant. Both were
    /// checked rather than assumed. Adding a skip package to the shared Tests project
    /// mid-merge is a poor trade for one opt-in test.
    /// </para>
    ///
    /// <para>
    /// So the protection is put where it actually bites, and it is arguably stronger
    /// than a skip: any pipeline that MEANS to run this sets
    /// <c>LANGFLOW_SMOKE_REQUIRED=1</c>, and the day the rest of the environment goes
    /// missing the test FAILS instead of quietly passing. Half-configured fails the
    /// same way, because that is always a mistake. Only a completely unconfigured
    /// developer machine returns early.
    /// </para>
    ///
    /// <para>If this suite ever moves to xUnit v3, replace all of this with
    /// <c>Assert.Skip</c> and delete the environment variable.</para>
    /// </summary>
    private static void AssertConfigurationIsUsable(LangflowOptions? options, IMongoDatabase? database)
    {
        var required = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_REQUIRED") == "1";
        var baseUrl = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_BASE_URL");
        var flowId = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_FLOW_ID");
        var anySet = !string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(flowId);

        if (required)
        {
            Assert.True(
                options is not null,
                "LANGFLOW_SMOKE_REQUIRED=1 but LANGFLOW_SMOKE_BASE_URL / LANGFLOW_SMOKE_FLOW_ID are missing or "
                + "unusable — this run would have reported a pass without contacting Langflow at all.");
            Assert.True(
                database is not null,
                "LANGFLOW_SMOKE_REQUIRED=1 but the parity Mongo is unreachable, so the turn could not be "
                + "persisted and the test would have reported a pass without running.");
            return;
        }

        Assert.False(
            anySet && options is null,
            $"Langflow smoke configuration is incomplete: BASE_URL={Describe(baseUrl)}, "
            + $"FLOW_ID={Describe(flowId)}. Set both (and an absolute URL), or neither.");
    }

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<unset>" : "<set>";

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
