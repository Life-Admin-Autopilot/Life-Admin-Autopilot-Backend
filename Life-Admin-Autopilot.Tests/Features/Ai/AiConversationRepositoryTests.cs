using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The conversation persistence, against the parity Mongo. Skipped (not failed)
/// when that instance is not running, following <c>UsageQuotaTests</c>.
/// </summary>
public sealed class AiConversationRepositoryTests
{
    private const string Personal = AiConversationVocabulary.PersonalScope;

    [Fact]
    public async Task load_creates_an_empty_conversation_shaped_like_mongooses()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = await FreshUserAsync(database);

        var loaded = await repository.LoadAsync(userId, Personal, null);

        Assert.Empty(loaded.Messages);

        // Field-for-field the document Mongoose's create() writes, measured against
        // a live reference row: the explicit scopeId null and the __v: 0 both matter
        // because both servers write into the same parity database.
        var raw = await RawAsync(database, userId);
        Assert.Equal(Personal, raw["scope"].AsString);
        Assert.Equal(BsonNull.Value, raw["scopeId"]);
        Assert.Equal(0, raw["__v"].AsInt32);
        Assert.True(raw.Contains("createdAt"));
        Assert.True(raw.Contains("updatedAt"));
    }

    [Fact]
    public async Task load_is_idempotent_and_never_creates_a_second_document()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = await FreshUserAsync(database);

        var first = await repository.LoadAsync(userId, Personal, null);
        var second = await repository.LoadAsync(userId, Personal, null);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(
            1,
            await Conversations(database).CountDocumentsAsync(
                Builders<AiConversationDocument>.Filter.Eq(c => c.UserId, userId)));
    }

    [Fact]
    public async Task reset_upserts_and_clears_without_a_prior_read()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = await FreshUserAsync(database);

        await repository.AppendTurnAsync(userId, Personal, null, Turn("user", "hello"));
        await repository.ResetAsync(userId, Personal, null);

        Assert.Empty((await repository.LoadAsync(userId, Personal, null)).Messages);

        // The upsert-insert path stamps the same three fields Mongoose does.
        var fresh = await FreshUserAsync(database);
        await repository.ResetAsync(fresh, Personal, null);

        var raw = await RawAsync(database, fresh);
        Assert.Equal(0, raw["__v"].AsInt32);
        Assert.Equal(BsonNull.Value, raw["scopeId"]);
        Assert.Empty(raw["messages"].AsBsonArray);
    }

    [Fact]
    public async Task every_push_slices_the_history_to_the_last_fifty_turns()
    {
        // `$slice: -AI_CONVERSATION_MAX_TURNS` on EVERY push is what keeps the
        // document bounded with no background sweep. Dropping it is a silent
        // unbounded-growth bug that nothing else would catch.
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = await FreshUserAsync(database);

        for (var i = 0; i < AiConversationVocabulary.MaxTurns + 12; i++)
        {
            await repository.AppendTurnAsync(userId, Personal, null, Turn("user", $"turn {i}"));
        }

        var loaded = await repository.LoadAsync(userId, Personal, null);

        Assert.Equal(AiConversationVocabulary.MaxTurns, loaded.Messages.Count);

        // The window keeps the NEWEST turns — a positive slice would keep the oldest.
        Assert.Equal("turn 12", loaded.Messages.First().Text);
        Assert.Equal($"turn {AiConversationVocabulary.MaxTurns + 11}", loaded.Messages.Last().Text);
    }

    [Fact]
    public async Task round_trips_sources_and_tool_calls()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = await FreshUserAsync(database);

        var turn = Turn("assistant", "here you go");
        turn.Sources = new List<AiConversationSourceDocument>
        {
            new() { Kind = "task", SourceId = "6a7900000000000000000011", Title = "Renew passport" },
        };
        turn.ToolCalls = new List<AiConversationToolCallDocument>
        {
            new()
            {
                CallId = "c1",
                Name = "queryTasks",
                Args = new BsonDocument { ["limit"] = 20 },
                Status = AiConversationVocabulary.PendingConfirmation,
            },
        };

        await repository.AppendTurnAsync(userId, Personal, null, turn);

        var message = (await repository.LoadAsync(userId, Personal, null)).Messages.Single();

        // The `id` field, not `_id` — see AiConversationSourceDocument.SourceId.
        Assert.Equal("6a7900000000000000000011", message.Sources!.Single().SourceId);
        Assert.Equal(20, message.ToolCalls!.Single().Args["limit"].AsInt32);
    }

    [Fact]
    public async Task finds_and_resolves_a_tool_call_by_id()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var userId = await FreshUserAsync(database);

        await repository.AppendTurnAsync(userId, Personal, null, Turn("user", "delete everything"));

        var withCall = Turn("assistant", string.Empty);
        withCall.ToolCalls = new List<AiConversationToolCallDocument>
        {
            new()
            {
                CallId = "call-1",
                Name = "deleteAllTasks",
                Args = new BsonDocument { ["scope"] = "all" },
                Status = AiConversationVocabulary.PendingConfirmation,
            },
        };
        await repository.AppendTurnAsync(userId, Personal, null, withCall);

        var found = await repository.FindToolCallAsync(userId, Personal, "call-1");
        Assert.NotNull(found);
        Assert.Equal("deleteAllTasks", found!.Name);

        Assert.Null(await repository.FindToolCallAsync(userId, Personal, "no-such-call"));

        var modified = await repository.ResolveToolCallAsync(
            userId, Personal, "call-1", "executed",
            result: new BsonDocument { ["deleted"] = 3 }, writeResult: true);

        Assert.Equal(1, modified);

        var resolved = await repository.FindToolCallAsync(userId, Personal, "call-1");
        Assert.Equal("executed", resolved!.Status);
        Assert.Equal(3, resolved.Result!.AsBsonDocument["deleted"].AsInt32);
    }

    [Fact]
    public async Task a_tool_call_belonging_to_another_user_is_invisible()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var owner = await FreshUserAsync(database);
        var stranger = await FreshUserAsync(database);

        var withCall = Turn("assistant", string.Empty);
        withCall.ToolCalls = new List<AiConversationToolCallDocument>
        {
            new()
            {
                CallId = "call-2",
                Name = "deleteAllTasks",
                Args = new BsonDocument(),
                Status = AiConversationVocabulary.PendingConfirmation,
            },
        };
        await repository.AppendTurnAsync(owner, Personal, null, withCall);

        Assert.Null(await repository.FindToolCallAsync(stranger, Personal, "call-2"));
    }

    // ---- the two 404 messages ---------------------------------------------

    [Fact]
    public async Task an_unknown_call_is_expired_and_a_resolved_one_is_already_handled()
    {
        // SAME code, DIFFERENT message. Both verified against the reference by
        // seeding an identical conversation into both parity databases.
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var repository = new AiConversationRepository(database);
        var service = new AiConversationService(repository);
        var userId = await FreshUserAsync(database);

        var expired = await Assert.ThrowsAsync<AppException>(
            () => service.RequirePendingToolCallAsync(userId, "nope"));
        Assert.Equal(404, expired.Status);
        Assert.Equal("pending_call_not_found", expired.Code);
        Assert.Equal("This confirmation has expired.", expired.Message);

        var withCall = Turn("assistant", string.Empty);
        withCall.ToolCalls = new List<AiConversationToolCallDocument>
        {
            new()
            {
                CallId = "call-3",
                Name = "deleteAllTasks",
                Args = new BsonDocument(),
                Status = "declined",
            },
        };
        await repository.AppendTurnAsync(userId, Personal, null, withCall);

        var handled = await Assert.ThrowsAsync<AppException>(
            () => service.RequirePendingToolCallAsync(userId, "call-3"));
        Assert.Equal(404, handled.Status);
        Assert.Equal("pending_call_not_found", handled.Code);
        Assert.Equal("This confirmation has already been handled.", handled.Message);
    }

    // ---- helpers -----------------------------------------------------------

    private static AiConversationMessageDocument Turn(string role, string text) =>
        new() { Role = role, Text = text, CreatedAt = DateTime.UtcNow };

    private static IMongoCollection<AiConversationDocument> Conversations(IMongoDatabase database) =>
        database.GetCollection<AiConversationDocument>(MongoCollections.AiConversations);

    private static async Task<ObjectId> FreshUserAsync(IMongoDatabase database)
    {
        var userId = ObjectId.GenerateNewId();
        await Conversations(database).DeleteManyAsync(
            Builders<AiConversationDocument>.Filter.Eq(c => c.UserId, userId));

        return userId;
    }

    private static async Task<BsonDocument> RawAsync(IMongoDatabase database, ObjectId userId) =>
        await database
            .GetCollection<BsonDocument>(MongoCollections.AiConversations)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .FirstAsync();

    internal static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(AiWebApplicationFactory.AiDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
