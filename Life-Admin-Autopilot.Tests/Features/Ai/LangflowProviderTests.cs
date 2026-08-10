using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
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
        Assert.Equal(UserId.ToString(), body.GetProperty("session_id").GetString());
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
        Assert.Equal("jwt-abc", tweaks.GetProperty("accessToken").GetString());
        Assert.Equal("plan", tweaks.GetProperty("mode").GetString());
        // ISO-8601 WITH an offset, per PLANNING-AGENT.md §6. A bare yyyy-MM-dd is the
        // v3 format the redesign replaced.
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$",
            tweaks.GetProperty("currentDate").GetString()!);
    }

    [Fact]
    public async Task anchors_current_date_in_the_users_zone_not_utc()
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

        await DrainAsync(provider, timezone: "Africa/Cairo");

        var currentDate = JsonDocument.Parse(handler.LastRequestBody!)
            .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4")
            .GetProperty("currentDate").GetString()!;

        // Cairo is UTC+03:00 year-round since 2015. Sending +00:00 here is what made
        // a 9am reminder land at noon Cairo time: the agent resolves the user's
        // relative phrasing against whatever instant it is handed.
        var parsed = DateTimeOffset.Parse(currentDate, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromHours(3), parsed.Offset);
    }

    [Fact]
    public async Task falls_back_to_utc_when_the_zone_is_absent_or_unusable()
    {
        if (Database is null)
        {
            return;
        }

        foreach (var zone in new string?[] { null, "Not/AZone", "Factory" })
        {
            var handler = Handler(Ndjson("""{"event":"end","data":{}}"""));

            var provider = Provider(handler, new Dictionary<string, string?>
            {
                ["Ai:Langflow:InputNode"] = "PlanningInput-v4",
            });

            await DrainAsync(provider, timezone: zone);

            var currentDate = JsonDocument.Parse(handler.LastRequestBody!)
                .RootElement.GetProperty("tweaks").GetProperty("PlanningInput-v4")
                .GetProperty("currentDate").GetString()!;

            var parsed = DateTimeOffset.Parse(currentDate, CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, parsed.Offset);
        }
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
    public async Task refuses_to_transcribe_rather_than_pretending_to_be_unconfigured()
    {
        if (Database is null)
        {
            return;
        }

        var provider = Provider(Handler(string.Empty));

        // Langflow's run endpoint is text-only and the baseline flow has no speech
        // input. Answering 503 ai_not_configured would misreport a real gap as
        // "switched off".
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.TranscribeAsync(new AiTranscriptionRequest(Array.Empty<byte>(), "audio/m4a", "en")));
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
            conversations ?? new AiConversationRepository(Database!));
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
