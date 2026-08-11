namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <b>Real turns against a live Langflow.</b> The only tests in this slice that touch
/// the boundary every defect in it has come from.
///
/// <para>
/// <b>Why they exist.</b> The suite has well over a thousand tests and not one of them
/// could have caught the defects found by hand this week: a created matter rendering
/// as a bare ledger row, a stated time silently defaulted away, the agent's JSON
/// envelope printed into the chat bubble. Every one came from an assumption about what
/// Langflow emits — that a <c>tool_use</c> block carries an id, that a row arrives
/// once, that an <c>output</c> means the call ran, that a tool result is a plain
/// object. None was a translation error. Each was fed a wire shape that did not exist.
/// More synthetic frames cannot help, because the synthetic frames ARE the wrong
/// assumption. Only a test that does not supply its own input can notice.
/// </para>
///
/// <para>
/// <b>Nothing below is asserted against the reply.</b> Every claim is settled in Mongo
/// or through the API — a row count, a stored status, a stored <c>dueAt</c>. In all
/// those defects the reply read perfectly, which is precisely why they shipped.
/// </para>
///
/// <para>
/// <b>OPT-IN, and silent by default.</b> They run only when
/// <c>LANGFLOW_SMOKE_BASE_URL</c> and <c>LANGFLOW_SMOKE_FLOW_ID</c> are set, so a
/// normal <c>dotnet test</c> stays offline and green:
/// </para>
///
/// <code>
/// LANGFLOW_SMOKE_BASE_URL=http://127.0.0.1:7860 \
/// LANGFLOW_SMOKE_FLOW_ID=&lt;flow&gt; \
/// LANGFLOW_SMOKE_INPUT_NODE=PlanningInput-v4 \
/// LANGFLOW_SMOKE_API_KEY="$(cat "${TMPDIR:-/tmp}"/kitto-stack/langflow_api_key)" \
/// LANGFLOW_SMOKE_REQUIRED=1 \
/// dotnet test --filter FullyQualifiedName~LangflowSmokeTests
/// </code>
///
/// <para>
/// <b>They need the whole stack, not just Langflow.</b> The flow's eleven tools are
/// HTTP wrappers that call this backend back as the caller, so the backend must be up
/// (<c>LANGFLOW_SMOKE_API_BASE_URL</c>, default <c>http://localhost:5080</c>) and the
/// suite must be pointed at the database it writes to
/// (<c>LANGFLOW_SMOKE_MONGO_DB</c>, default <c>kitto_dev</c>). A mismatch fails loudly
/// at signup rather than confusingly later. <c>./tools/dev/stack.sh up</c> provides
/// all of it.
/// </para>
///
/// <para>
/// <b>Two model rounds and one confirm, shared.</b> See <see cref="LangflowSmokeStack"/>
/// — the turns are taken once by the fixture and every test here asserts one invariant
/// about them. Turns are spaced (<c>LANGFLOW_SMOKE_SPACING_SECONDS</c>, default 45) and
/// retried on a fresh account (<c>LANGFLOW_SMOKE_ATTEMPTS</c>, default 3), because the
/// free-tier 429 arrives as an <c>error</c> frame inside a healthy 200 and would
/// otherwise read as a flow defect.
/// </para>
/// </summary>
public sealed class LangflowSmokeTests : IClassFixture<LangflowSmokeStack>
{
    private readonly LangflowSmokeStack _stack;

    public LangflowSmokeTests(LangflowSmokeStack stack) => _stack = stack;

    // ---- 1. the tool result the card reads ----------------------------------

    [Fact]
    public void a_created_matter_arrives_as_something_the_chat_can_render_as_a_card()
    {
        if (!Live())
        {
            return;
        }

        // `task` must be at the TOP level of tool_result.result. Langflow wraps a
        // tool's return in `content` and our tool wraps its body in `value`, both as
        // JSON *strings*; unwrapped wrongly, ToolCallCard's `result.task` is undefined
        // and the matter the agent just created is invisible behind a plain row.
        var taskId = LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(
            _stack.Created.Events, "createTask");

        // And the id it carries names a row that really exists.
        LangflowTurnInvariants.AssertClaimedMatterIsInTheStore(taskId, _stack.Created.StoredDueAtById);
    }

    // ---- 2. the envelope stays out of the bubble ----------------------------

    [Fact]
    public void the_agents_json_envelope_never_reaches_the_chat_bubble()
    {
        if (!Live())
        {
            return;
        }

        LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(_stack.Created.Events);
    }

    // ---- 3. one frame per invocation ----------------------------------------

    [Fact]
    public void one_tool_call_frame_is_emitted_for_each_thing_the_agent_actually_did()
    {
        if (!Live())
        {
            return;
        }

        LangflowTurnInvariants.AssertOneFramePerToolInvocation(_stack.Created.Events);
        LangflowTurnInvariants.AssertOneFramePerToolInvocation(_stack.Gated.AskEvents);

        // The version that cannot be argued with: the client was shown N createTask
        // pills, and the store gained exactly N matters.
        LangflowTurnInvariants.AssertToolCallCountMatchesSideEffects(
            _stack.Created.Events, "createTask", _stack.Created.MattersCreated);
    }

    // ---- 4. confirmation gating ---------------------------------------------

    [Fact]
    public void a_bulk_wipe_is_staged_until_the_user_confirms_and_only_then_deletes()
    {
        if (!Live())
        {
            return;
        }

        // Before: a card, no result, a pending record, and NOTHING removed — Langflow
        // runs the dry run and redelivers the row with its output populated inside the
        // same turn, and reading that as an outcome killed every confirmation.
        var callId = LangflowTurnInvariants.AssertGatedCallIsStagedNotRun(
            _stack.Gated.AskEvents,
            _stack.Gated.StatusesAfterAsk,
            _stack.Gated.OpenBeforeAsk,
            _stack.Gated.OpenAfterAsk);

        // After: the gate opens. A trailing error frame on the continuation does not
        // mean it failed, so the open-matter count is what settles it.
        LangflowTurnInvariants.AssertConfirmedCallActuallyRan(
            _stack.Gated.ConfirmEvents,
            callId,
            _stack.Gated.StatusesAfterConfirm,
            _stack.Gated.OpenAfterAsk,
            _stack.Gated.OpenAfterConfirm,
            _stack.Gated.ConfirmFailure);
    }

    // ---- 5. the time the user said ------------------------------------------

    [Fact]
    public void a_time_the_user_stated_round_trips_into_the_stored_matter()
    {
        if (!Live())
        {
            return;
        }

        var taskId = LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(
            _stack.Created.Events, "createTask");

        var storedDueAt = LangflowTurnInvariants.AssertClaimedMatterIsInTheStore(
            taskId, _stack.Created.StoredDueAtById);

        // 15:00 in Africa/Cairo, which is 12:00Z through the summer. Computed via the
        // zone rather than hard-coded, because Egypt observes DST and a winter run of
        // the same prompt is correctly 13:00Z.
        LangflowTurnInvariants.AssertStatedTimeRoundTrips(
            _stack.Created.ExpectedDueAtUtc, storedDueAt, _stack.Created.StatedLocally);
    }

    // ---- 0. the shape both turns must have ----------------------------------

    [Fact]
    public void one_real_turn_still_matches_the_shapes_the_translator_expects()
    {
        if (!Live())
        {
            return;
        }

        LangflowTurnInvariants.AssertTurnShape(_stack.Created.Events);
        LangflowTurnInvariants.AssertTurnShape(_stack.Gated.AskEvents);
    }

    // ---- the honesty guard ---------------------------------------------------

    /// <summary>
    /// Runs the configuration guard and reports whether the live assertions should
    /// execute at all.
    ///
    /// <para>
    /// <b>The failure mode being defended against</b> is a CI summary reporting a pass
    /// for a test that did nothing, on the day the credentials stop being injected —
    /// nobody reads a green line. A real "skipped" result would say so, but
    /// <c>Assert.Skip</c> and <c>SkipTestWithoutData</c> are both xUnit v3, and this
    /// suite is on 2.9.3 where <c>Skip</c> is a compile-time constant. Both were
    /// checked rather than assumed.
    /// </para>
    ///
    /// <para>
    /// So the protection is put where it bites, and it is arguably stronger than a
    /// skip: any pipeline that MEANS to run this sets <c>LANGFLOW_SMOKE_REQUIRED=1</c>,
    /// and the day the rest of the environment goes missing the tests FAIL instead of
    /// quietly passing. Half-configured fails the same way, because that is always a
    /// mistake — a base URL with no flow id means someone meant to run this and the
    /// wiring broke. Only a completely unconfigured developer machine returns early.
    /// </para>
    ///
    /// <para>If this suite ever moves to xUnit v3, replace all of this with
    /// <c>Assert.Skip</c> and delete the environment variable.</para>
    /// </summary>
    private bool Live()
    {
        var required = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_REQUIRED") == "1";
        var baseUrl = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_BASE_URL");
        var flowId = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_FLOW_ID");
        var anySet = !string.IsNullOrWhiteSpace(baseUrl) || !string.IsNullOrWhiteSpace(flowId);

        if (required)
        {
            Assert.True(
                LangflowSmokeStack.SmokeOptions() is not null,
                "LANGFLOW_SMOKE_REQUIRED=1 but LANGFLOW_SMOKE_BASE_URL / LANGFLOW_SMOKE_FLOW_ID are missing "
                + "or unusable — this run would have reported a pass without contacting Langflow at all.");

            Assert.True(
                LangflowSmokeStack.TryGetDatabase() is not null,
                "LANGFLOW_SMOKE_REQUIRED=1 but the backend's Mongo is unreachable, so nothing could be "
                + "asserted against the store and the test would have reported a pass without running.");

            Assert.True(
                _stack.Configured,
                $"LANGFLOW_SMOKE_REQUIRED=1 but the backend at {LangflowSmokeStack.ApiBaseUrl} did not answer "
                + "/health. The flow's tools call it back as the caller, so without it no turn can create, "
                + "read or delete anything — run ./tools/dev/stack.sh up, or set LANGFLOW_SMOKE_API_BASE_URL.");
        }
        else
        {
            Assert.False(
                anySet && LangflowSmokeStack.SmokeOptions() is null,
                $"Langflow smoke configuration is incomplete: BASE_URL={Describe(baseUrl)}, "
                + $"FLOW_ID={Describe(flowId)}. Set both (and an absolute URL), or neither.");
        }

        if (!_stack.Configured)
        {
            return false;
        }

        Assert.True(
            _stack.SetupFailure is null,
            $"the live turns could not be taken, so nothing below was checked: {_stack.SetupFailure}");

        return true;
    }

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<unset>" : "<set>";
}
