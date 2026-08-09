using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The journaled write path. Skipped when the parity Mongo is not running.
/// </summary>
public sealed class BulkServiceTests
{
    [Fact]
    public void to_mongo_ops_maps_bson_null_to_unset_and_everything_else_to_set()
    {
        // Arrange — the null convention that makes undo exact.
        var patch = new BsonDocument
        {
            ["deletedAt"] = BsonNull.Value,
            ["status"] = "open",
        };

        // Act
        var ops = BulkService.ToMongoOps(patch);

        // Assert
        Assert.Equal(1, ops["$unset"].AsBsonDocument["deletedAt"].AsInt32);
        Assert.Equal("open", ops["$set"].AsBsonDocument["status"].AsString);
    }

    [Fact]
    public void to_mongo_ops_omits_an_empty_half()
    {
        // Act
        var ops = BulkService.ToMongoOps(new BsonDocument { ["status"] = "done" });

        // Assert — an empty $unset would be a Mongo error, not a no-op.
        Assert.False(ops.Contains("$unset"));
    }

    [Fact]
    public void warnings_flag_document_sourced_tasks_and_fired_reminders()
    {
        // Arrange
        var tasks = new List<TaskDocument>
        {
            new() { SourceDocumentId = ObjectId.GenerateNewId() },
            new()
            {
                Reminders = new List<ReminderEntryDocument>
                {
                    new() { At = DateTime.UtcNow, FiredAt = DateTime.UtcNow, Kind = "due" },
                },
            },
            new(),
        };

        // Act
        var warnings = BulkService.SummarizeWarnings(tasks);

        // Assert
        Assert.Equal(1, warnings.FromDocuments);
        Assert.Equal(1, warnings.RemindersFired);
        Assert.False(warnings.Truncated);
    }

    [Fact]
    public async Task delete_is_soft_journaled_and_cascades_to_clarifications()
    {
        // Arrange
        var context = TryCreate();
        if (context is null)
        {
            return;
        }

        var (service, database, userId) = context.Value;
        var task = await SeedTask(database, userId, "Renew passport");
        var clarification = await SeedClarification(database, userId, task.Id);

        // Act
        var result = await service.ApplyAsync(
            userId,
            new BulkTarget { Ids = new[] { task.Id.ToString() } },
            new BulkActionInput.Delete());

        // Assert — soft delete, not a removal.
        Assert.Equal(1, result.Affected);
        Assert.NotNull(result.UndoToken);

        var stored = await FindTask(database, task.Id);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.DeletedAt);

        // The task is invisible to any NotDeleted() read.
        Assert.Empty(await database
            .GetCollection<TaskDocument>(MongoCollections.Tasks)
            .Find(Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
                MongoRepositoryBase<TaskDocument>.NotDeleted()))
            .ToListAsync());

        // A question about a deleted task is moot, so it is dropped.
        Assert.Equal("dropped", (await FindClarification(database, clarification.Id))!.Status);

        // The journal exists and describes the change.
        var op = await database
            .GetCollection<TaskBulkOpDocument>(MongoCollections.TaskBulkOps)
            .Find(Builders<TaskBulkOpDocument>.Filter.Eq(o => o.Id, ObjectId.Parse(result.UndoToken!)))
            .FirstOrDefaultAsync();
        Assert.Equal("applied", op.Status);
        Assert.Equal("delete", op.Action);
    }

    [Fact]
    public async Task undo_restores_the_task_but_deliberately_leaves_clarifications_dropped()
    {
        // Arrange
        var context = TryCreate();
        if (context is null)
        {
            return;
        }

        var (service, database, userId) = context.Value;
        var task = await SeedTask(database, userId, "Pay rent");
        var clarification = await SeedClarification(database, userId, task.Id);

        var result = await service.ApplyAsync(
            userId,
            new BulkTarget { Ids = new[] { task.Id.ToString() } },
            new BulkActionInput.Delete());

        // Act
        var restored = await service.UndoAsync(userId, result.UndoToken!);

        // Assert — the task comes back, $unset restoring the ABSENT field rather than
        // leaving a null behind.
        Assert.Equal(1, restored);
        Assert.Null((await FindTask(database, task.Id))!.DeletedAt);

        // The asymmetry, ported on purpose: a dropped question is a settled
        // conversation and is NOT resurrected.
        Assert.Equal("dropped", (await FindClarification(database, clarification.Id))!.Status);
    }

    [Fact]
    public async Task undo_is_idempotent()
    {
        // Arrange
        var context = TryCreate();
        if (context is null)
        {
            return;
        }

        var (service, database, userId) = context.Value;
        var task = await SeedTask(database, userId, "Book vet");
        var result = await service.ApplyAsync(
            userId,
            new BulkTarget { Ids = new[] { task.Id.ToString() } },
            new BulkActionInput.Complete());

        // Act
        await service.UndoAsync(userId, result.UndoToken!);
        var second = await service.UndoAsync(userId, result.UndoToken!);

        // Assert — a double-tap on the toast must not surface a failure for work that
        // is already reversed.
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task a_no_op_action_journals_nothing()
    {
        // Arrange
        var context = TryCreate();
        if (context is null)
        {
            return;
        }

        var (service, database, userId) = context.Value;
        var task = await SeedTask(database, userId, "Already done", status: "done");

        // Act — completing a done task changes nothing.
        var result = await service.ApplyAsync(
            userId,
            new BulkTarget { Ids = new[] { task.Id.ToString() } },
            new BulkActionInput.Complete());

        // Assert — no undo token, so undo cannot "restore" a change that never happened.
        Assert.Equal(0, result.Affected);
        Assert.Null(result.UndoToken);
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task<TaskDocument> SeedTask(
        IMongoDatabase database,
        ObjectId userId,
        string title,
        string status = "open")
    {
        var now = DateTime.UtcNow;
        var task = new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Title = title,
            Domain = "home",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await database.GetCollection<TaskDocument>(MongoCollections.Tasks).InsertOneAsync(task);
        return task;
    }

    private static async Task<ClarificationDocument> SeedClarification(
        IMongoDatabase database,
        ObjectId userId,
        ObjectId taskId)
    {
        var now = DateTime.UtcNow;
        var clarification = new ClarificationDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            TaskId = taskId,
            Status = "open",
            Question = "Which date did you mean?",
            Draft = new ClarificationDraftDocument { Title = "x", Domain = "home" },
            CreatedAt = now,
            UpdatedAt = now,
        };

        await database
            .GetCollection<ClarificationDocument>(MongoCollections.Clarifications)
            .InsertOneAsync(clarification);

        return clarification;
    }

    private static Task<TaskDocument?> FindTask(IMongoDatabase database, ObjectId id) =>
        database.GetCollection<TaskDocument>(MongoCollections.Tasks)
            .Find(Builders<TaskDocument>.Filter.Eq(t => t.Id, id))
            .FirstOrDefaultAsync()!;

    private static Task<ClarificationDocument?> FindClarification(IMongoDatabase database, ObjectId id) =>
        database.GetCollection<ClarificationDocument>(MongoCollections.Clarifications)
            .Find(Builders<ClarificationDocument>.Filter.Eq(c => c.Id, id))
            .FirstOrDefaultAsync()!;

    private static (BulkService Service, IMongoDatabase Database, ObjectId UserId)? TryCreate()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var database = new MongoClient(settings).GetDatabase(KernelWebApplicationFactory.ParityDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return (new BulkService(database, new ClarificationCascade(database)), database, ObjectId.GenerateNewId());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
