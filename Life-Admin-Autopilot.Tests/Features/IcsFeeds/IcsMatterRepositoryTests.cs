using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>
/// <c>RetireFutureOpenAsync</c> — the sweep behind "stop this calendar".
///
/// <para>
/// The scoping rules had coverage through the endpoint; the timestamp did not, and
/// that is the one this file exists for. See KERNEL.md §7.0.
/// </para>
/// </summary>
public sealed class IcsMatterRepositoryTests
{
    private const string RetireDatabase = "kitto_parity_dotnet_f_retire_tests";

    private static readonly DateTime Now = new(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Stale = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task retiring_a_feed_stamps_updated_at_as_mongoose_would()
    {
        // The reference is `Task.updateMany(..., { $set: { deletedAt: now } })`
        // (routes/me.icsFeeds.ts:147) on a `timestamps: true` model, so Mongoose puts
        // `updatedAt` into that same `$set` and the Node source never names it. A
        // line-by-line port sets only `deletedAt` and leaves the row's `updatedAt`
        // at whatever it was — invisible until a later read, which is why no harness
        // row catches it.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        var feedId = ObjectId.GenerateNewId().ToString();

        await SeedAsync(db, userId, feedId, "evt-1", dueAt: Now.AddDays(30));

        var retired = await new IcsMatterRepository(db).RetireFutureOpenAsync(userId, feedId, Now);

        Assert.Equal(1, retired);

        var stored = await SingleAsync(db, userId);
        Assert.Equal(Now, stored.DeletedAt);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task leaves_past_and_completed_matters_alone()
    {
        // Past and done matters are a record of what happened; "stop this calendar"
        // does not mean "delete my history".
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        var feedId = ObjectId.GenerateNewId().ToString();

        await SeedAsync(db, userId, feedId, "past", dueAt: Now.AddDays(-1));
        await SeedAsync(db, userId, feedId, "done", dueAt: Now.AddDays(30), status: "done");
        await SeedAsync(db, userId, feedId, "future", dueAt: Now.AddDays(30));

        var retired = await new IcsMatterRepository(db).RetireFutureOpenAsync(userId, feedId, Now);

        Assert.Equal(1, retired);

        var untouched = await Tasks(db)
            .Find(Builders<TaskDocument>.Filter.Eq(t => t.UpdatedAt, Stale))
            .ToListAsync();

        Assert.Equal(2, untouched.Count);
        Assert.All(untouched, t => Assert.Null(t.DeletedAt));
    }

    [Fact]
    public async Task is_scoped_to_the_one_subscription()
    {
        // An unscoped sweep would retire the matters of every other feed the user
        // still subscribes to.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        var mine = ObjectId.GenerateNewId().ToString();
        var theirs = ObjectId.GenerateNewId().ToString();

        await SeedAsync(db, userId, mine, "evt-1", dueAt: Now.AddDays(30));
        await SeedAsync(db, userId, theirs, "evt-1", dueAt: Now.AddDays(30));

        var retired = await new IcsMatterRepository(db).RetireFutureOpenAsync(userId, mine, Now);

        Assert.Equal(1, retired);

        var survivor = await Tasks(db)
            .Find(Builders<TaskDocument>.Filter.Eq(t => t.ExternalId, IcsMatterRepository.ExternalIdFor(theirs, "evt-1")))
            .SingleAsync();

        Assert.Null(survivor.DeletedAt);
        Assert.Equal(Stale, survivor.UpdatedAt);
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task SeedAsync(
        IMongoDatabase db,
        ObjectId userId,
        string feedId,
        string occurrenceId,
        DateTime dueAt,
        string status = "open") =>
        await Tasks(db).InsertOneAsync(new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Title = occurrenceId,
            Domain = "home",
            Kind = "reminder",
            Status = status,
            Priority = "normal",
            Subtasks = new List<SubtaskDocument>(),
            Tags = new List<string>(),
            DueAt = dueAt,
            ExternalSource = IcsFeedVocabulary.ExternalSource,
            ExternalId = IcsMatterRepository.ExternalIdFor(feedId, occurrenceId),
            Reminders = new List<ReminderEntryDocument>(),
            RescheduleCount = 0,

            // Deliberately old, so "did the sweep touch this row?" is unambiguous.
            CreatedAt = Stale,
            UpdatedAt = Stale,
        });

    private static IMongoCollection<TaskDocument> Tasks(IMongoDatabase db) =>
        db.GetCollection<TaskDocument>(MongoCollections.Tasks);

    private static Task<TaskDocument> SingleAsync(IMongoDatabase db, ObjectId userId) =>
        Tasks(db).Find(Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId)).SingleAsync();

    private static async Task<ObjectId> FreshUserAsync(IMongoDatabase db)
    {
        await Tasks(db).DeleteManyAsync(Builders<TaskDocument>.Filter.Empty);
        return ObjectId.GenerateNewId();
    }

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(RetireDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
