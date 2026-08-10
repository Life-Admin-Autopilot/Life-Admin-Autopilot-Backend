using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <c>POST /ai/tools/confirm/{callId}</c> against a REAL pending record in Mongo.
///
/// <para>
/// The record is what makes this route work across a restart, so these tests seed a
/// real one rather than a fake: the confirmation the user is looking at was written
/// by a previous process, and the server that resolves it has no memory of the turn
/// that created it. Skipped when the parity instance is down.
/// </para>
/// </summary>
public sealed class AiConfirmStreamTests
{
    // ---- decline ------------------------------------------------------------

    [Fact]
    public async Task decline_emits_tool_result_then_done_and_nothing_else()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-decline");
        using var factory = Factory();

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-decline", "decline"));

        Assert.Equal(new[] { "tool_result", "done" }, frames.Select(Type).ToArray());

        Assert.Equal("call-decline", frames[0].GetProperty("callId").GetString());
        Assert.Equal(JsonValueKind.Null, frames[0].GetProperty("result").ValueKind);
        Assert.Equal("declined", frames[0].GetProperty("error").GetString());

        // `usage:{}` — nothing was spent, so there is nothing to report.
        Assert.Equal("{}", frames[1].GetProperty("usage").GetRawText());
    }

    [Fact]
    public async Task decline_sends_no_quota_frame()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-decline-quota");
        using var factory = Factory();

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-decline-quota", "decline"));

        // Node returns before the line that would send one. The turn cost nothing and
        // the meter has not moved, so publishing it would be noise.
        Assert.DoesNotContain(frames, f => Type(f) == "quota");
    }

    [Fact]
    public async Task decline_flips_the_durable_record_so_it_cannot_be_replayed()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-replay");
        using var factory = Factory();

        await ConfirmAsync(factory, userId, "call-replay", "decline");

        var call = await Repository.FindToolCallAsync(userId, AiConversationVocabulary.PersonalScope, "call-replay");
        Assert.Equal("declined", call!.Status);

        // A second attempt is the OTHER 404 message — same code, different text, and
        // the client distinguishes them on the message.
        var second = await ConfirmAsync(factory, userId, "call-replay", "decline");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var error = JsonDocument.Parse(await second.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error");
        Assert.Equal("pending_call_not_found", error.GetProperty("code").GetString());
        Assert.Equal("This confirmation has already been handled.", error.GetProperty("message").GetString());
    }

    // ---- confirm ------------------------------------------------------------

    [Fact]
    public async Task confirm_runs_the_tool_then_re_enters_the_agent()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-confirm");
        var provider = new ScriptedAiProvider(new[]
        {
            AiStreamEvents.Sources(Array.Empty<AiStreamSource>()),
            AiStreamEvents.Token("Cleared them."),
            AiStreamEvents.Done(),
        });
        using var factory = Factory(provider);

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-confirm", "confirm"));

        // The tool result lands FIRST, then the agent's reaction to it, then the
        // meter. The continuation exists so Kitto can finish the remaining steps of
        // the user's original multi-step request.
        Assert.Equal(
            new[] { "tool_result", "sources", "token", "done", "quota" },
            frames.Select(Type).ToArray());

        var continuation = Assert.Single(provider.Continuations);
        Assert.Equal("deleteAllTasks", continuation.ToolName);
        Assert.Equal("call-confirm", continuation.CallId);
    }

    [Fact]
    public async Task confirm_reports_the_tool_result_with_error_null()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-result");
        using var factory = Factory();

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-result", "confirm"));
        var result = frames[0].GetProperty("result");

        Assert.Equal(JsonValueKind.Null, frames[0].GetProperty("error").ValueKind);
        Assert.True(result.GetProperty("deleted").GetBoolean());

        // No tasks were seeded, so the wipe is a no-op — but it is still a journaled,
        // reversible one.
        Assert.Equal(0, result.GetProperty("deletedCount").GetInt32());
        Assert.Equal("car", result.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task confirm_marks_the_record_executed()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-executed");
        using var factory = Factory();

        await ConfirmAsync(factory, userId, "call-executed", "confirm");

        var call = await Repository.FindToolCallAsync(
            userId, AiConversationVocabulary.PersonalScope, "call-executed");
        Assert.Equal("executed", call!.Status);
    }

    [Fact]
    public async Task confirm_counts_the_continuation_but_never_refuses_it()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync("call-counted");
        var store = new InMemoryUsageQuotaStore();
        using var factory = Factory(store: store);

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-counted", "confirm"));

        // Ungated but counted: it is the same logical turn the user already paid for,
        // so it is never refused — but it is a fresh model round, so the visible meter
        // moves before the quota frame goes out.
        Assert.Equal(0, store.Denials);
        Assert.Equal(1, store.UsedFor(Bucket(userId)));
        Assert.Equal(1, frames.Single(f => Type(f) == "quota")
            .GetProperty("quotas")[0].GetProperty("used").GetInt32());
    }

    [Fact]
    public async Task a_tool_that_fails_becomes_the_error_on_the_tool_result_not_a_dead_stream()
    {
        if (Database is null)
        {
            return;
        }

        // A record naming an inline tool cannot legitimately be pending. Node answers
        // `tool_not_destructive` from inside the stream and still resumes the agent,
        // so the user is told what happened rather than watching it hang.
        var userId = await SeedPendingCallAsync("call-inline", toolName: "queryTasks");
        using var factory = Factory();

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-inline", "confirm"));

        Assert.Equal("tool_result", Type(frames[0]));
        Assert.Equal(JsonValueKind.Null, frames[0].GetProperty("result").ValueKind);
        Assert.Contains("no confirmation step", frames[0].GetProperty("error").GetString());

        var call = await Repository.FindToolCallAsync(
            userId, AiConversationVocabulary.PersonalScope, "call-inline");
        Assert.Equal("failed", call!.Status);
    }

    // ---- re-validation happens BEFORE the flush -----------------------------

    [Fact]
    public async Task a_stale_record_is_a_json_400_not_an_error_frame()
    {
        if (Database is null)
        {
            return;
        }

        // Args that no longer satisfy the tool's schema — an older build, a renamed
        // enum, a hand-edited row. Re-validation runs before the headers go out, so
        // this is an ordinary HTTP error a client can branch on by status.
        var userId = await SeedPendingCallAsync(
            "call-stale",
            args: new BsonDocument { ["domain"] = "atlantis" });
        using var factory = Factory();

        var response = await ConfirmAsync(factory, userId, "call-stale", "confirm");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "invalid_tool_args",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task narrows_by_the_status_filter_spelling_a_stored_record_may_use()
    {
        if (Database is null)
        {
            return;
        }

        var userId = await SeedPendingCallAsync(
            "call-status-filter",
            args: new BsonDocument { ["status_filter"] = "done" });
        using var factory = Factory();

        var frames = await FramesAsync(await ConfirmAsync(factory, userId, "call-status-filter", "confirm"));

        // The narrowing SURVIVES recovery. Dropping it would turn "delete my done
        // tasks" into "delete everything" at the moment the user pressed Confirm.
        Assert.Equal("done", frames[0].GetProperty("result").GetProperty("status").GetString());
    }

    // ---- helpers ------------------------------------------------------------

    private const string ConfirmDatabase = "kitto_parity_dotnet_m_confirm";

    private static readonly IMongoDatabase? Database = TryGetDatabase();

    private static AiConversationRepository Repository => new(Database!);

    private static UsageQuotaBucket Bucket(ObjectId userId) => new(
        MongoCollections.AiUsageCounters,
        userId,
        new Dictionary<string, string>
        {
            ["date"] = UsageQuotaBuckets.UtcDate(),
            ["kind"] = "message",
        },
        AiQuotaService.DefaultFreeDaily);

    /// <summary>
    /// Writes a pending tool call the way a previous turn would have, then forgets
    /// everything about it — which is the situation the route is built for.
    /// </summary>
    private static async Task<ObjectId> SeedPendingCallAsync(
        string callId,
        string toolName = AiToolCatalog.DeleteAllTasks,
        BsonDocument? args = null)
    {
        var userId = ObjectId.GenerateNewId();

        await Repository.AppendTurnAsync(
            userId,
            AiConversationVocabulary.PersonalScope,
            null,
            new AiConversationMessageDocument
            {
                Role = "assistant",
                Text = string.Empty,
                CreatedAt = DateTime.UtcNow,
                ToolCalls = new List<AiConversationToolCallDocument>
                {
                    new()
                    {
                        CallId = callId,
                        Name = toolName,
                        Args = args ?? new BsonDocument { ["domain"] = "car" },
                        Status = AiConversationVocabulary.PendingConfirmation,
                    },
                },
            });

        return userId;
    }

    private static ConfirmFactory Factory(
        ScriptedAiProvider? provider = null,
        InMemoryUsageQuotaStore? store = null)
    {
        var quotaStore = store ?? new InMemoryUsageQuotaStore();
        var ai = provider ?? new ScriptedAiProvider(ScriptedAiProvider.HappyTurn());

        return new ConfirmFactory(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IAiProvider>(_ => ai));
            services.Replace(ServiceDescriptor.Singleton<IUsageQuotaStore>(_ => quotaStore));
        });
    }

    private static Task<HttpResponseMessage> ConfirmAsync(
        KernelWebApplicationFactory factory,
        ObjectId userId,
        string callId,
        string action)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ai/tools/confirm/{callId}")
        {
            Content = new StringContent(
                $$"""{"action":"{{action}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", KernelPipelineTests.NodeShapedToken(userId.ToString(), "ai@probe.com"));

        return factory.CreateApiClient().SendAsync(request);
    }

    private static string? Type(JsonElement frame) => frame.GetProperty("type").GetString();

    private static async Task<IReadOnlyList<JsonElement>> FramesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        return body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(record => record.StartsWith("data: ", StringComparison.Ordinal))
            .Select(record => JsonDocument.Parse(record["data: ".Length..]).RootElement)
            .ToList();
    }

    private static IMongoDatabase? TryGetDatabase()
    {
        try
        {
            // Before the first collection is resolved — see LangflowProviderTests.
            MongoKernelConventions.Register();

            var client = new MongoClient(
                $"{KernelWebApplicationFactory.ParityMongoUri}/?serverSelectionTimeoutMS=800");
            var database = client.GetDatabase(ConfirmDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ConfirmFactory : KernelWebApplicationFactory
    {
        private readonly Action<IServiceCollection> _configure;

        public ConfirmFactory(Action<IServiceCollection> configure)
        {
            _configure = configure;
            With("MongoDbSettings:DatabaseName", ConfirmDatabase);
            With("GEMINI_API_KEY", string.Empty);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(_configure);
        }
    }
}
