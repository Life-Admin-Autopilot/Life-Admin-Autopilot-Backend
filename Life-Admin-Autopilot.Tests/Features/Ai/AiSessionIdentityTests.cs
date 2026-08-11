using System.Net;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <b>Session identity: what the agent is told this conversation is called.</b>
///
/// <para>
/// The defect these pin: the adapter sent <c>session_id = userId</c>, a PERMANENT
/// per-user session. Langflow keeps its own memory against that key, and
/// <c>POST /ai/conversation/reset</c> clears only our Mongo copy — so the reset
/// button cleared the visible history and left the agent answering from the invisible
/// one. Measured live before the fix: re-asking a question inside the immortal session
/// came back as a memorised envelope with NO tool call, the agent replaying an old
/// answer instead of acting.
/// </para>
///
/// <para>
/// Three properties have to hold together, and each of them can be broken without
/// breaking the others — hence a test apiece:
/// stable within a conversation (or a plan forgets itself mid-confirmation),
/// different after a reset (or the reset lies),
/// never shared between users (or one account reads another's memory).
/// </para>
///
/// <para>Mongo-backed tests skip when the parity instance is down, following
/// <c>AiConversationRepositoryTests</c>.</para>
/// </summary>
public sealed class AiSessionIdentityTests
{
    private const string Personal = AiConversationVocabulary.PersonalScope;

    // ---- the key itself -----------------------------------------------------

    [Fact]
    public void the_session_key_names_the_owner_and_the_generation()
    {
        var userId = ObjectId.GenerateNewId();
        var generation = ObjectId.GenerateNewId();

        Assert.Equal($"{userId}:{generation}", AgentSessionId.For(userId, generation));
    }

    // ---- the generation, in the store ---------------------------------------

    [Fact]
    public async Task a_conversation_that_has_never_been_reset_stores_no_session_key_at_all()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = ObjectId.GenerateNewId();

        var conversation = await repository.LoadAsync(userId, Personal, null);

        // The generation falls back to the document's own id, so no insert path has
        // to remember to stamp one — and the stored document stays byte-identical to
        // the one Mongoose writes until a reset actually happens.
        Assert.Equal(conversation.Id, conversation.SessionGeneration);
        Assert.Null(conversation.SessionKey);
        Assert.False((await RawAsync(database, userId)).Contains("sessionKey"));
    }

    [Fact]
    public async Task appending_turns_never_moves_the_generation()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = ObjectId.GenerateNewId();

        var before = (await repository.LoadAsync(userId, Personal, null)).SessionGeneration;

        await repository.AppendTurnAsync(userId, Personal, null, Turn("user", "book the vet"));
        await repository.AppendTurnAsync(userId, Personal, null, Turn("assistant", "Booked."));

        // If this ever drifts, a plan forgets itself between the question and the
        // confirmation — the agent would be handed a session it has never seen
        // halfway through its own multi-step turn.
        Assert.Equal(before, (await repository.LoadAsync(userId, Personal, null)).SessionGeneration);
    }

    [Fact]
    public async Task reset_rotates_the_generation_and_hands_back_the_retired_one()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = ObjectId.GenerateNewId();

        var before = (await repository.LoadAsync(userId, Personal, null)).SessionGeneration;

        var retired = await repository.ResetAsync(userId, Personal, null);

        var after = (await repository.LoadAsync(userId, Personal, null)).SessionGeneration;

        Assert.Equal(before, retired);
        Assert.NotEqual(before, after);

        // Still one document, still the same one — the rotation is a field, not a
        // delete-and-recreate, so createdAt and the unique key are untouched.
        Assert.Equal(
            1,
            await database
                .GetCollection<AiConversationDocument>(MongoCollections.AiConversations)
                .CountDocumentsAsync(Builders<AiConversationDocument>.Filter.Eq(c => c.UserId, userId)));
    }

    [Fact]
    public async Task resetting_a_conversation_that_never_existed_retires_nothing()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = ObjectId.GenerateNewId();

        // The upsert-insert path: there is no previous generation to forget, and
        // reporting one would make the reset ask the agent to drop a session that
        // belongs to nobody.
        Assert.Null(await repository.ResetAsync(userId, Personal, null));
        Assert.NotNull((await repository.LoadAsync(userId, Personal, null)).SessionKey);
    }

    // ---- the reset, as the service performs it ------------------------------

    [Fact]
    public async Task reset_asks_the_agent_to_forget_the_session_it_just_retired()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var memory = new RecordingSessionMemory();
        var service = new AiConversationService(repository, memory);
        var userId = ObjectId.GenerateNewId();

        var retired = (await repository.LoadAsync(userId, Personal, null)).SessionGeneration;

        await service.ResetAsync(userId);

        // The OLD key, not the new one: telling the agent to forget the session the
        // user is about to talk into would clear each new conversation as it started.
        Assert.Equal(new[] { AgentSessionId.For(userId, retired) }, memory.Forgotten);
    }

    [Fact]
    public async Task a_reset_with_nothing_to_retire_does_not_call_the_agent()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var memory = new RecordingSessionMemory();
        var service = new AiConversationService(new AiConversationRepository(database), memory);

        await service.ResetAsync(ObjectId.GenerateNewId());

        Assert.Empty(memory.Forgotten);
    }

    [Fact]
    public async Task an_agent_that_refuses_to_forget_does_not_fail_the_users_reset()
    {
        var database = AiConversationRepositoryTests.TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var service = new AiConversationService(repository, new ThrowingSessionMemory());
        var userId = ObjectId.GenerateNewId();

        await repository.AppendTurnAsync(userId, Personal, null, Turn("user", "hello"));
        var before = (await repository.LoadAsync(userId, Personal, null)).SessionGeneration;

        var response = await service.ResetAsync(userId);

        // Deleting data on somebody else's system is the LAST thing reset does and
        // the only optional one. The local half — messages gone, generation rotated —
        // must be complete and reported as success regardless.
        Assert.Empty(response.Messages);

        var reloaded = await repository.LoadAsync(userId, Personal, null);
        Assert.Empty(reloaded.Messages);
        Assert.NotEqual(before, reloaded.SessionGeneration);
    }

    // ---- what actually reaches Langflow -------------------------------------

    [Fact]
    public async Task every_turn_of_one_conversation_runs_under_the_same_session()
    {
        if (Database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var handler = EndOfTurn();
        var provider = Provider(handler);

        await AskAsync(provider, userId);
        var first = SessionIdOf(handler);

        await AskAsync(provider, userId);
        var second = SessionIdOf(handler);

        Assert.Equal(first, second);

        // And it is NOT the bare user id, which is the permanent session this whole
        // file exists to prevent coming back.
        Assert.NotEqual(userId.ToString(), first);
        Assert.StartsWith(userId + ":", first);
    }

    [Fact]
    public async Task the_continuation_after_a_confirmation_stays_in_the_asks_session()
    {
        if (Database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var handler = EndOfTurn();
        var provider = Provider(handler);

        await AskAsync(provider, userId);
        var ask = SessionIdOf(handler);

        await DrainAsync(provider.ContinueAfterConfirmAsync(new AiContinuationRequest(
            userId.ToString(), "call-1", "deleteAllTasks", null, new { deletedCount = 2 }, null, null)));

        // A confirmation arrives as a second run against the same agent. In a
        // different session it would be a stranger reporting a tool result for a plan
        // nobody remembers making.
        Assert.Equal(ask, SessionIdOf(handler));
    }

    [Fact]
    public async Task the_first_turn_after_a_reset_runs_under_a_session_the_agent_has_never_seen()
    {
        if (Database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var handler = EndOfTurn();
        var repository = new AiConversationRepository(Database);
        var provider = Provider(handler, repository);

        await AskAsync(provider, userId);
        var before = SessionIdOf(handler);

        await new AiConversationService(repository).ResetAsync(userId);

        await AskAsync(provider, userId);

        // THE regression. With the old per-user session id these two were equal, and
        // the agent answered the next question out of the conversation the user had
        // just cleared.
        Assert.NotEqual(before, SessionIdOf(handler));
    }

    [Fact]
    public async Task two_users_never_share_a_session()
    {
        if (Database is null)
        {
            return;
        }

        var handler = EndOfTurn();
        var provider = Provider(handler);

        await AskAsync(provider, ObjectId.GenerateNewId());
        var first = SessionIdOf(handler);

        await AskAsync(provider, ObjectId.GenerateNewId());

        // The agent has no notion of our tenancy: the session key is the only thing
        // keeping one account's memory out of another's answers.
        Assert.NotEqual(first, SessionIdOf(handler));
    }

    // ---- dropping the retired transcript ------------------------------------

    [Fact]
    public async Task forgetting_a_session_deletes_its_messages_from_langflows_own_store()
    {
        HttpMethod? method = null;
        string? apiKey = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            method = request.Method;
            apiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.FirstOrDefault() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Memory(handler).ForgetAsync("user-1:gen-1");

        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("key-1", apiKey);
        Assert.Equal(
            "http://langflow.test/api/v1/monitor/messages/session/user-1%3Agen-1",
            handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task a_refused_deletion_is_raised_so_the_caller_can_log_it()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "nope");

        // The caller's policy is "log it and carry on" — which only works if the
        // failure reaches the caller at all. Swallowing it here would let an agent
        // that has stopped accepting deletions accumulate dead sessions in silence.
        await Assert.ThrowsAsync<HttpRequestException>(() => Memory(handler).ForgetAsync("user-1:gen-1"));
    }

    // ---- helpers ------------------------------------------------------------

    private static AiConversationMessageDocument Turn(string role, string text) =>
        new() { Role = role, Text = text, CreatedAt = DateTime.UtcNow };

    private static async Task<BsonDocument> RawAsync(IMongoDatabase database, ObjectId userId) =>
        await database
            .GetCollection<BsonDocument>(MongoCollections.AiConversations)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .FirstAsync();

    private static StubHttpMessageHandler EndOfTurn() =>
        StubbedLangflow.Handler("""{"event":"end","data":{}}""");

    /// <summary>The <c>session_id</c> of the run the stub last received.</summary>
    private static string SessionIdOf(StubHttpMessageHandler handler) =>
        JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("session_id").GetString()!;

    private static Task AskAsync(LangflowAiProvider provider, ObjectId userId) =>
        StubbedLangflow.AskAsync(provider, userId);

    private static async Task DrainAsync(IAsyncEnumerable<AiStreamEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static LangflowAiProvider Provider(
        HttpMessageHandler handler,
        AiConversationRepository? conversations = null) =>
        StubbedLangflow.Provider(handler, Database!, conversations);

    private static LangflowSessionMemory Memory(HttpMessageHandler handler) =>
        StubbedLangflow.SessionMemory(handler);

    /// <summary>
    /// Its own database, so the suites running against one mongod cannot see each
    /// other's conversations (KERNEL.md §12).
    /// </summary>
    private static readonly IMongoDatabase? Database = StubbedLangflow.Database("session");

    private sealed class RecordingSessionMemory : IAgentSessionMemory
    {
        public List<string> Forgotten { get; } = new();

        public Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Forgotten.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSessionMemory : IAgentSessionMemory
    {
        public Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("Connection refused");
    }
}
