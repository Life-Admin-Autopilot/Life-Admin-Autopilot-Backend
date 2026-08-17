using System.Net;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <see cref="LangflowAiProvider"/> against a stubbed Langflow: a canned response
/// body, read incrementally, translated, and persisted.
///
/// <para>
/// <b>The request and the reading are really exercised; the server is not.</b> No
/// Langflow was reachable from this worktree, so the canned bodies are authored, not
/// captured. What these prove is the provider's own behaviour — what it POSTs, that
/// it reads frame by frame rather than buffering, how it reports an unreachable or
/// failing server, and what it writes to the conversation.
/// </para>
///
/// <para>Persistence needs the parity Mongo; those tests skip when it is down,
/// following <c>AiConversationRepositoryTests</c>.</para>
/// </summary>
public sealed class LangflowProviderTests
{
    // ---- what it sends ------------------------------------------------------

    [Fact]
    public async Task posts_to_the_streaming_run_endpoint_with_the_api_key()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler));

        // ?stream=true is not optional: the blocking form of the same route answers
        // once the whole turn is done, which is the UX regression this exists to
        // avoid.
        Assert.Equal(
            "http://langflow.test/api/v1/run/flow-1?stream=true",
            handler.LastRequestUri!.ToString());

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("What is due?", body.GetProperty("input_value").GetString());
        Assert.Equal("chat", body.GetProperty("input_type").GetString());

        // `<userId>:<conversation generation>`, never the bare user id — that was a
        // permanent per-user session and it made the reset button dishonest. The
        // three properties the key has to hold are pinned in AiSessionIdentityTests;
        // here we only care that the run carries one and that it names the caller.
        Assert.StartsWith(UserId + ":", body.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task omits_tweaks_entirely_when_no_input_node_is_configured()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler));

        // The default is the plain ChatInput flow, unchanged. A tweaks map appears
        // only when something asked for one.
        Assert.DoesNotContain("tweaks", handler.LastRequestBody!);
    }

    [Fact]
    public async Task delivers_the_prompt_as_a_tweak_when_the_flow_has_no_chat_input()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        var provider = Provider(handler, new Dictionary<string, string?>
        {
            ["Ai:Langflow:InputNode"] = "PlanningInput-v4",
            ["Ai:Langflow:Tweaks:PlanningInput-v4:mode"] = "plan",
        });

        await DrainAsync(provider, accessToken: "jwt-abc");

        var tweaks = JsonDocument.Parse(handler.LastRequestBody!)
            .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4");

        // A flow with no ChatInput ignores input_value completely: it accepts the
        // request, streams a healthy-looking turn, and hands the agent an empty
        // prompt with no error anywhere.
        Assert.Equal("What is due?", tweaks.GetProperty("transcript").GetString());
        Assert.Equal("plan", tweaks.GetProperty("mode").GetString());

        // The bearer is NOT here. It goes to the tool components instead — see
        // sends_the_bearer_to_every_tool_node_and_never_to_the_prompt.
        Assert.False(tweaks.TryGetProperty("accessToken", out _));

        // Offset-bearing, always — see sends_current_date_with_the_callers_utc_offset
        // for why a bare date is a silent three-hour error rather than a format nit.
        // The trailing weekday is Node's formatNow() shape, not decoration.
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2} \([A-Z][a-z]+day\)$",
            tweaks.GetProperty("currentDate").GetString()!);

        // The grounding blocks ride on the same node. Their CONTENT is asserted
        // against Node's own output in AiDateGroundingTests / AiTaskGroundingTests;
        // what matters here is that the binding actually sends them, because the
        // failure mode when it does not is a perfectly healthy-looking empty turn.
        Assert.StartsWith(
            DateGrounding.ReferenceHeader,
            tweaks.GetProperty("dateReference").GetString()!);
        Assert.Equal(TaskGrounding.NoTasks, tweaks.GetProperty("myTasks").GetString());
    }

    [Fact]
    public async Task sends_the_bearer_to_every_tool_node_and_never_to_the_prompt()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        var provider = Provider(handler, new Dictionary<string, string?>
        {
            ["Ai:Langflow:InputNode"] = "PlanningInput-v4",
        });

        await DrainAsync(provider, accessToken: "jwt-abc");

        var tweaks = JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("tweaks");

        // Every tool component gets the token DIRECTLY. It used to be `tool_mode: true`
        // on all eleven, so the agent carried it and could drop it — and on 2026-08-17 a
        // dropped token produced a failed holdForClarification beside its successful
        // retry, which the chat drew as a phantom question card. A field the model never
        // sees is a field it cannot omit.
        foreach (var node in LangflowInputBinding.ToolNodes)
        {
            Assert.True(tweaks.TryGetProperty(node, out var tool), $"no tweak for {node}");
            Assert.Equal(
                "jwt-abc",
                tool.GetProperty(LangflowInputBinding.ToolAccessTokenField).GetString());
        }

        // …and the prompt node carries no credential at all, so a live JWT never enters
        // the model's context and never comes back out in a tool argument.
        var input = tweaks.GetProperty("PlanningInput-v4");
        Assert.False(input.TryGetProperty("accessToken", out _));
        Assert.DoesNotContain("jwt-abc", input.GetProperty("transcript").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task sends_no_tool_tweaks_when_the_caller_has_no_token()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        var provider = Provider(handler, new Dictionary<string, string?>
        {
            ["Ai:Langflow:InputNode"] = "PlanningInput-v4",
        });

        // An absent token stays absent rather than being sent as "": the tool reports
        // "access_token is empty" either way, and an empty tweak would also overwrite a
        // value an operator had pinned on the node.
        await DrainAsync(provider, accessToken: null);

        var tweaks = JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("tweaks");

        foreach (var node in LangflowInputBinding.ToolNodes)
        {
            Assert.False(tweaks.TryGetProperty(node, out _), $"unexpected tweak for {node}");
        }
    }

    [Theory]
    [InlineData("Africa/Cairo", "+03:00")]
    [InlineData("Asia/Kolkata", "+05:30")]
    [InlineData("UTC", "+00:00")]
    public async Task sends_current_date_with_the_callers_utc_offset(string timezone, string offset)
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));
        var provider = Provider(handler, InputNodeBound);

        await DrainAsync(provider, timezone: timezone);

        var currentDate = JsonDocument.Parse(handler.LastRequestBody!)
            .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4")
            .GetProperty("currentDate").GetString()!;

        // A bare yyyy-MM-dd is the v3 format the flow was redesigned to REJECT, and
        // it fails silently: the agent invents +00:00 rather than erroring, so every
        // dueAt it derives lands out by the user's whole offset. Measured live with
        // Africa/Cairo: a 12:00 local reminder was stored as 09:00Z.
        Assert.Contains(offset + " (", currentDate);
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2} \([A-Z][a-z]+day\)$",
            currentDate);
    }

    [Fact]
    public async Task falls_back_to_utc_rather_than_failing_the_turn_on_a_bad_timezone()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        // `timezone` is optional on the request, and a turn is worth more than a
        // perfect offset. It still carries AN offset, which is the part that matters.
        await DrainAsync(Provider(handler, InputNodeBound), timezone: "Mars/Olympus_Mons");

        var currentDate = JsonDocument.Parse(handler.LastRequestBody!)
            .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4")
            .GetProperty("currentDate").GetString()!;

        Assert.Contains("+00:00 (", currentDate);
    }

    [Fact]
    public async Task defaults_mode_to_chat_because_ask_is_the_chat_surface()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler, InputNodeBound));

        Assert.Equal(
            "chat",
            JsonDocument.Parse(handler.LastRequestBody!)
                .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4")
                .GetProperty("mode").GetString());
    }

    [Fact]
    public async Task lets_an_operators_static_mode_pin_win()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));
        var settings = new Dictionary<string, string?>(InputNodeBound)
        {
            ["Ai:Langflow:Tweaks:PlanningInput-v4:mode"] = "transcript",
        };

        await DrainAsync(Provider(handler, settings));

        Assert.Equal(
            "transcript",
            JsonDocument.Parse(handler.LastRequestBody!)
                .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4")
                .GetProperty("mode").GetString());
    }

    [Fact]
    public async Task never_sends_the_access_token_when_no_node_is_configured_to_receive_it()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler), accessToken: "jwt-abc");

        // The caller's credential leaves the process only when something is bound to
        // consume it, and only to the configured Langflow host.
        Assert.DoesNotContain("jwt-abc", handler.LastRequestBody!);
    }

    // ---- what it reads ------------------------------------------------------

    [Fact]
    public async Task translates_a_whole_streamed_turn_in_order()
    {
        if (Database is null)
        {
            return;
        }

        var handler = Handler(Ndjson(
            """{"event":"token","data":{"chunk":"Book "}}""",
            """{"event":"token","data":{"chunk":"the vet."}}""",
            """{"event":"end","data":{"result":{"outputs":[]}}}"""));

        var events = await DrainAsync(Provider(handler));

        Assert.Equal(
            new[] { "sources", "token", "token", "done" },
            events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task yields_each_frame_before_the_body_is_finished()
    {
        if (Database is null)
        {
            return;
        }

        // A body that never completes: if the provider buffered the response, the
        // first MoveNext would hang and this test would time out rather than fail.
        var release = new TaskCompletionSource();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream(
                Encoding.UTF8.GetBytes(Ndjson("""{"event":"token","data":{"chunk":"live"}}""")),
                release.Task)),
        });

        var enumerator = Provider(handler)
            .AskAsync(new AiAskRequest(UserId.ToString(), "What is due?", null))
            .GetAsyncEnumerator();

        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(AiStreamEvents.SourcesKind, enumerator.Current.Kind);

            // Arrived while the server is still holding the connection open.
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(AiStreamEvents.TokenKind, enumerator.Current.Kind);
            Assert.Equal("live", enumerator.Current.Payload["text"]);
        }
        finally
        {
            release.SetResult();
            await enumerator.DisposeAsync();
        }
    }

    // ---- how it fails -------------------------------------------------------

    [Fact]
    public async Task reports_an_unreachable_server_as_a_502_app_exception()
    {
        if (Database is null)
        {
            return;
        }

        var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));

        var error = await Assert.ThrowsAsync<AppException>(() => DrainAsync(Provider(handler)));

        // An AppException carries a code the route puts on the error frame; a bare
        // exception would surface as `internal_error` with no useful message.
        Assert.Equal("langflow_unavailable", error.Code);
        Assert.Contains("Connection refused", error.Message);
    }

    [Fact]
    public async Task reports_a_failing_flow_with_its_status_and_body()
    {
        if (Database is null)
        {
            return;
        }

        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "flow not found");

        var error = await Assert.ThrowsAsync<AppException>(() => DrainAsync(Provider(handler)));

        Assert.Equal("langflow_error", error.Code);
        Assert.Contains("500", error.Message);
        Assert.Contains("flow not found", error.Message);
    }

    [Fact]
    public async Task answers_the_contract_503_when_asked_to_transcribe()
    {
        if (Database is null)
        {
            return;
        }

        var provider = Provider(Handler(string.Empty));

        // Langflow's run endpoint is text-only and the baseline flow has no speech
        // input, so this adapter genuinely cannot transcribe. It used to say so with
        // a NotSupportedException, on the argument that a real gap should fail loudly
        // — but the route has already passed IsConfigured by then, so "loudly" meant
        // a bare 500 at a caller who did nothing wrong and cannot tell a missing
        // capability from a broken server. The contract has a code for exactly this.
        var error = await Assert.ThrowsAsync<AppException>(() =>
            provider.TranscribeAsync(new AiTranscriptionRequest(Array.Empty<byte>(), "audio/m4a", "en")));

        Assert.Equal(503, error.Status);
        Assert.Equal("ai_not_configured", error.Code);

        // Byte-for-byte the literal the route's own pre-flight check uses
        // (AiShellErrors.Transcribe). It OMITS the trailing " to enable." that
        // /ai/ask carries — verified live against the reference server, and the
        // differ compares these strings exactly.
        Assert.Equal("AI is not configured. Set GEMINI_API_KEY in server/.env.", error.Message);
    }

    // ---- configuration ------------------------------------------------------

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("http://langflow.test", null, false)]
    [InlineData(null, "flow-1", false)]
    [InlineData("not a url", "flow-1", false)]
    [InlineData("http://langflow.test", "flow-1", true)]
    public void is_configured_only_with_an_absolute_url_and_a_flow(string? url, string? flow, bool expected)
    {
        var options = LangflowOptions.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["Ai:Langflow:BaseUrl"] = url,
            ["Ai:Langflow:FlowId"] = flow,
        }));

        Assert.Equal(expected, options.IsConfigured);
    }

    [Fact]
    public void falls_back_to_the_default_timeout_rather_than_failing_startup()
    {
        var options = LangflowOptions.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["Ai:Langflow:TimeoutSeconds"] = "not-a-number",
        }));

        Assert.Equal(LangflowOptions.DefaultTimeoutSeconds, options.TimeoutSeconds);
    }

    // ---- persistence (needs the parity Mongo) -------------------------------

    [Fact]
    public async Task persists_the_user_turn_before_streaming_and_the_answer_after()
    {
        var database = Database;
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(database);
        var handler = Handler(Ndjson(
            """{"event":"token","data":{"chunk":"Booked."}}""",
            """{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler, conversations: repository), userId);

        var messages = await repository.RecentTurnsAsync(userId, AiConversationVocabulary.PersonalScope, 10);

        // The question is written BEFORE the first frame, so a mid-stream disconnect
        // still leaves it in history.
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("What is due?", messages[0].Text);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("Booked.", messages[1].Text);
    }

    [Fact]
    public async Task stores_a_confirmable_call_as_pending_so_it_survives_a_restart()
    {
        var database = Database;
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(database);
        var handler = Handler(Ndjson(
            """{"event":"tool_call","data":{"callId":"call-9","name":"deleteAllTasks","args":{"domain":"car"}}}""",
            """{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler, conversations: repository), userId);

        // The DURABLE record is what the confirm route reads. An in-memory map would
        // lose this across a deploy and the confirmation card would 404 forever.
        var call = await repository.FindToolCallAsync(userId, AiConversationVocabulary.PersonalScope, "call-9");

        Assert.NotNull(call);
        Assert.Equal(AiConversationVocabulary.PendingConfirmation, call!.Status);
        Assert.Equal("deleteAllTasks", call.Name);
        Assert.Equal("car", call.Args.AsBsonDocument["domain"].AsString);
    }

    [Fact]
    public async Task a_gated_call_is_still_pending_after_a_full_turn_with_the_output_redelivery()
    {
        if (Database is null)
        {
            return;
        }

        // The regression, end to end: translator → provider → Mongo. Langflow
        // redelivers the bulk-wipe row with its DRY-RUN output inside the same turn,
        // and the persisted record must survive that as `pending_confirmation` —
        // otherwise the confirm route 404s and the card the user is looking at can
        // never be actioned. Asserting on the stored document rather than on frames,
        // because the stored status is what the confirm route reads.
        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(Database);

        // ONE physical line per frame — this is NDJSON, and a raw string literal
        // spanning two lines silently splits the frame in half.
        const string row =
            """{"event":"add_message","data":{"id":"bb209266-1f6e-4a3f-9d21-6f0f9e2a1c77","content_blocks":[{"contents":[{"type":"tool_use","name":"DeleteAllTasksTool-v4","tool_input":{"status_filter":"done"}__OUT__}]}]}}""";

        var handler = Handler(Ndjson(
            row.Replace("__OUT__", string.Empty, StringComparison.Ordinal),
            row.Replace(
                "__OUT__",
                ""","output":{"executed":false,"requiresConfirmation":true}""",
                StringComparison.Ordinal),
            """{"event":"end","data":{}}"""));

        await DrainAsync(Provider(handler, conversations: repository), userId);

        var call = await repository.FindToolCallAsync(
            userId, AiConversationVocabulary.PersonalScope, "bb209266-1f6e-4a3f-9d21-6f0f9e2a1c77~0");

        Assert.NotNull(call);
        Assert.Equal(AiConversationVocabulary.PendingConfirmation, call!.Status);
        Assert.Equal(AiToolCatalog.DeleteAllTasks, call.Name);

        // And the narrowing the user agreed to is intact for re-validation.
        Assert.Equal("done", call.Args.AsBsonDocument["status_filter"].AsString);
    }

    [Fact]
    public async Task a_stubbed_turn_satisfies_the_same_invariants_the_live_smoke_test_asserts()
    {
        if (Database is null)
        {
            return;
        }

        // Runs LangflowTurnInvariants.AssertTurnShape against a turn carrying BOTH a
        // gated and an ungated tool. The smoke test can only run where a Langflow
        // exists; this proves its assertion logic is not itself broken, so that when
        // someone finally points it at an instance, a pass means something and a
        // failure is about Langflow rather than about the test.
        var handler = Handler(Ndjson(
            """{"event":"tool_call","data":{"callId":"c-gated","name":"deleteAllTasks","args":{}}}""",
            """{"event":"tool_result","data":{"callId":"c-gated","result":{"executed":false}}}""",
            """{"event":"tool_call","data":{"callId":"c-inline","name":"queryTasks","args":{}}}""",
            """{"event":"tool_result","data":{"callId":"c-inline","result":{"count":3}}}""",
            """{"event":"token","data":{"chunk":"Three things."}}""",
            """{"event":"end","data":{}}"""));

        var events = await DrainAsync(Provider(handler), ObjectId.GenerateNewId());

        LangflowTurnInvariants.AssertTurnShape(events);

        // And specifically the invariant the live run is there to police.
        Assert.DoesNotContain(
            events,
            e => e.Kind == AiStreamEvents.ToolResultKind && (string)e.Payload["callId"]! == "c-gated");
    }

    [Fact]
    public async Task a_stubbed_create_turn_satisfies_the_behavioural_invariants_too()
    {
        if (Database is null)
        {
            return;
        }

        // The other half of what makes a live pass mean something. Every wire shape
        // below was MEASURED off Langflow 1.11.2 and is here replayed through the real
        // translator: the double-encoded tool result, the envelope arriving as token
        // chunks, and the same add_message row redelivered as it fills in. Then the
        // very assertions the smoke suite runs are applied to the output.
        //
        // This does supply its own input, so it proves nothing about today's Langflow.
        // What it proves is that the checker works — LangflowTurnInvariantsTests then
        // proves the same checker rejects each defect. Between them, a green live run
        // is a statement about Langflow rather than about the test.
        const string row =
            """{"event":"add_message","data":{"id":"bb209266-1f6e-4a3f-9d21-6f0f9e2a1c77","content_blocks":[{"contents":[{"type":"tool_use","name":"createTask","tool_input":{"title":"call the dentist"}__OUT__}]}]}}""";

        // Exactly as captured: `content` holds a JSON *string* holding another JSON
        // string. Unwrapped wrongly, result.task is undefined and the card degrades.
        const string wrapped =
            ""","output":{"content":"{\"value\": \"{\\\"ok\\\": true, \\\"task\\\": {\\\"id\\\": \\\"6a7a6dc8522aceb15af17b33\\\"}}\"}","status":"success"}""";

        var handler = Handler(Ndjson(
            row.Replace("__OUT__", string.Empty, StringComparison.Ordinal),
            row.Replace("__OUT__", wrapped, StringComparison.Ordinal),
            """{"event":"token","data":{"chunk":"{\"mode\":\"chat\",\"reply\":\"Added"}}""",
            """{"event":"token","data":{"chunk":" it — 3 PM.\",\"tasks\":[],"}}""",
            """{"event":"token","data":{"chunk":"\"clarifications\":[]}"}}""",
            """{"event":"end","data":{}}"""));

        var events = await DrainAsync(Provider(handler), ObjectId.GenerateNewId());

        LangflowTurnInvariants.AssertTurnShape(events);

        Assert.Equal(
            "6a7a6dc8522aceb15af17b33",
            LangflowTurnInvariants.AssertToolResultCarriesTaskAtTopLevel(events, "createTask"));

        // The row arrived twice; the user must see one pill, and the store would have
        // gained one matter.
        LangflowTurnInvariants.AssertOneFramePerToolInvocation(events);
        LangflowTurnInvariants.AssertToolCallCountMatchesSideEffects(events, "createTask", 1);

        // Three chunks of envelope, one sentence of prose.
        Assert.Equal(
            "Added it — 3 PM.",
            LangflowTurnInvariants.AssertTokensCarryProseNotTheEnvelope(events));
    }

    [Fact]
    public async Task declines_a_pending_call_that_outlived_the_stale_window()
    {
        var database = Database;
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiConversationRepository(database);

        await repository.AppendTurnAsync(
            userId,
            AiConversationVocabulary.PersonalScope,
            null,
            new AiConversationMessageDocument
            {
                Role = "assistant",
                Text = string.Empty,
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                ToolCalls = new List<AiConversationToolCallDocument>
                {
                    new()
                    {
                        CallId = "old-call",
                        Name = "deleteAllTasks",
                        Args = new BsonDocument(),
                        Status = AiConversationVocabulary.PendingConfirmation,
                    },
                },
            });

        await DrainAsync(Provider(Handler(Ndjson("""{"event":"end","data":{}}""")), conversations: repository), userId);

        // The sweep runs at the top of every turn. Without it, treating the durable
        // record as the source of truth would make a "Delete everything?" card from
        // last month confirmable forever.
        var call = await repository.FindToolCallAsync(userId, AiConversationVocabulary.PersonalScope, "old-call");
        Assert.Equal("declined", call!.Status);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly ObjectId UserId = ObjectId.GenerateNewId();

    /// <summary>The v4 flow's shape: no ChatInput, prompt delivered as a tweak.</summary>
    private static readonly Dictionary<string, string?> InputNodeBound = new()
    {
        ["Ai:Langflow:InputNode"] = "PlanningInput-v4",
    };

    private static LangflowAiProvider Provider(
        HttpMessageHandler handler,
        IDictionary<string, string?>? settings = null,
        AiConversationRepository? conversations = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Ai:Langflow:BaseUrl"] = "http://langflow.test",
            ["Ai:Langflow:FlowId"] = "flow-1",
            ["Ai:Langflow:ApiKey"] = "key-1",
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = Configuration(values);

        return new LangflowAiProvider(
            new SingleHandlerHttpClientFactory(handler),
            LangflowOptions.FromConfiguration(configuration),
            LangflowInputBinding.FromConfiguration(configuration),
            conversations ?? new AiConversationRepository(Database!),
            new AiGroundingRepository(Database!));
    }

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static StubHttpMessageHandler Handler(string body) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        });

    private static string Ndjson(params string[] lines) => string.Join("\n", lines) + "\n";

    private static async Task<List<AiStreamEvent>> DrainAsync(
        LangflowAiProvider provider,
        ObjectId? userId = null,
        string? accessToken = null,
        string? timezone = null)
    {
        var events = new List<AiStreamEvent>();

        var request = new AiAskRequest((userId ?? UserId).ToString(), "What is due?", timezone)
        {
            AccessToken = accessToken,
        };

        await foreach (var value in provider.AskAsync(request))
        {
            events.Add(value);
        }

        return events;
    }

    /// <summary>
    /// <b>Every test here needs Mongo, including the ones that look like pure HTTP
    /// tests.</b> A turn begins by sweeping stale pending calls, so the provider
    /// dials the database before it ever opens a socket to Langflow — there is no
    /// "just the HTTP half" path, and pretending otherwise with an unreachable handle
    /// only produced a confusing connection-refused deep inside the driver.
    ///
    /// <para>Resolved once. Null means the parity instance is down, and every test
    /// returns early rather than failing, following
    /// <c>AiConversationRepositoryTests</c>.</para>
    /// </summary>
    private static readonly IMongoDatabase? Database = TryGetDatabase();

    private static IMongoDatabase? TryGetDatabase()
    {
        try
        {
            // MUST come before the first collection is resolved. Without it the class
            // map is built with default PascalCase element names, so the document is
            // written as `Messages` while every hand-written update path says
            // `messages` — and the sweep fails with "the path 'messages' must exist".
            // Every direct-Mongo test class in this suite opens with this line.
            MongoKernelConventions.Register();

            var client = new MongoClient(
                $"{KernelWebApplicationFactory.ParityMongoUri}/?serverSelectionTimeoutMS=800");
            var database = client.GetDatabase("kitto_parity_dotnet_m_provider");
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>Serves its bytes, then blocks until released — a server still thinking.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Task _release;
        private int _position;

        public BlockingStream(byte[] prefix, Task release)
        {
            _prefix = prefix;
            _release = release;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await _release.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
