using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Notifications;

/// <summary>
/// <c>reminderWorker.ts</c>'s <c>runOnce()</c>, driven directly rather than
/// through its 30-second timer.
///
/// <para>
/// The two behaviours that have to survive the port are here: the per-entry
/// atomic claim that makes a double-send structurally impossible, and the
/// clarification settlement that runs at the tail of every tick. Both need a real
/// Mongo — <c>findOneAndUpdate</c> with a positional <c>$</c> has no in-memory
/// stand-in — so all of these skip when the parity instance is down.
/// </para>
/// </summary>
public sealed class ReminderWorkerTests
{
    private const string WorkerDatabase = "kitto_parity_dotnet_d_worker_tests";

    [Fact]
    public async Task fires_one_notification_per_due_reminder_and_stamps_fired_at()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "America/New_York");
        var now = DateTime.UtcNow;

        // 2026-03-05T01:00Z is Mar 5 in UTC but Mar 4 in New York.
        var dueAt = new DateTime(2026, 3, 5, 1, 0, 0, DateTimeKind.Utc);
        var taskId = await SeedTaskAsync(db, userId, "Renew passport", dueAt, Entry(now.AddMinutes(-1), "lead"));

        var fired = await Tick(db).RunAsync(now);

        Assert.Equal(1, fired);

        var notification = await Notifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .SingleAsync();

        Assert.Equal("reminder", notification["kind"].AsString);
        Assert.Equal("Renew passport", notification["title"].AsString);
        Assert.Equal(taskId, notification["taskId"].AsObjectId);

        // Named in the USER's zone, not the server's.
        Assert.Equal("Coming up — due Mar 4.", notification["body"].AsString);

        var reloaded = await Tasks(db).Find(Builders<BsonDocument>.Filter.Eq("_id", taskId)).SingleAsync();
        Assert.NotEqual(BsonNull.Value, reloaded["reminders"][0]["firedAt"]);
    }

    [Fact]
    public async Task never_sends_the_same_reminder_twice()
    {
        // The guard is the claim, not the scheduler: a second tick over the same
        // task finds firedAt already stamped and writes nothing.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;
        await SeedTaskAsync(db, userId, "Twice?", now.AddDays(1), Entry(now.AddMinutes(-1), "due"));

        Assert.Equal(1, await Tick(db).RunAsync(now));
        Assert.Equal(0, await Tick(db).RunAsync(now.AddSeconds(30)));

        Assert.Equal(
            1,
            await Notifications(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    [Fact]
    public async Task two_concurrent_ticks_still_send_exactly_once()
    {
        // The real race the claim exists for: a slow tick overlapping the next one.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;
        await SeedTaskAsync(db, userId, "Race", now.AddDays(1), Entry(now.AddMinutes(-1), "due"));

        var results = await System.Threading.Tasks.Task.WhenAll(
            Tick(db).RunAsync(now),
            Tick(db).RunAsync(now));

        Assert.Equal(1, results.Sum());
        Assert.Equal(
            1,
            await Notifications(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    [Fact]
    public async Task claims_each_entry_of_a_task_separately()
    {
        // The positional `reminders.$` updates exactly the matched entry, so a task
        // holding two due reminders sends two notifications, not one.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;
        await SeedTaskAsync(
            db,
            userId,
            "Two due",
            now.AddDays(1),
            Entry(now.AddMinutes(-2), "lead"),
            Entry(now.AddMinutes(-1), "due"));

        Assert.Equal(2, await Tick(db).RunAsync(now));

        var bodies = (await Notifications(db)
                .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
                .ToListAsync())
            .Select(n => n["body"].AsString)
            .OrderBy(b => b)
            .ToArray();

        Assert.Equal(new[] { $"Coming up — due {Day(now.AddDays(1))}.", $"Due {Day(now.AddDays(1))}." }, bodies);
    }

    [Fact]
    public async Task stays_silent_for_a_soft_deleted_or_finished_matter()
    {
        // Firing for a matter the user just deleted is the worst possible way to
        // learn the delete did not stick.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;

        await SeedTaskAsync(db, userId, "Deleted", now.AddDays(1), new[] { Entry(now.AddMinutes(-1), "due") }, deletedAt: now);
        await SeedTaskAsync(db, userId, "Done", now.AddDays(1), new[] { Entry(now.AddMinutes(-1), "due") }, status: "done");

        Assert.Equal(0, await Tick(db).RunAsync(now));
    }

    [Fact]
    public async Task says_only_reminder_when_the_matter_has_no_due_date()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;
        await SeedTaskAsync(db, userId, "Undated", null, Entry(now.AddMinutes(-1), "due"));

        await Tick(db).RunAsync(now);

        var notification = await Notifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .SingleAsync();

        Assert.Equal("Reminder", notification["body"].AsString);
    }

    [Fact]
    public async Task falls_back_to_utc_only_when_the_account_never_set_a_zone()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, timezone: null);
        var now = DateTime.UtcNow;
        await SeedTaskAsync(db, userId, "Zoneless", new DateTime(2026, 3, 5, 1, 0, 0, DateTimeKind.Utc), Entry(now.AddMinutes(-1), "due"));

        await Tick(db).RunAsync(now);

        var notification = await Notifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .SingleAsync();

        // UTC, so "Mar 5" — the field is optional by design and an unset zone is not
        // an error. An UNRECOGNISED one still throws; see ReminderNotificationTextTests.
        Assert.Equal("Due Mar 5.", notification["body"].AsString);
    }

    // ---- settleStaleClarifications ----------------------------------------

    [Fact]
    public async Task settles_a_question_that_stood_unanswered_for_a_week()
    {
        // A question the user never answered SETTLES on the AI's guess. It does not
        // nag: an unresolved counter the user cannot clear turns into guilt, and
        // re-surfacing it on a timer is a shame mechanism, not a reminder.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        var stale = await SeedClarificationAsync(db, userId, "open", now.AddDays(-8));
        var fresh = await SeedClarificationAsync(db, userId, "open", now.AddDays(-6));

        await Tick(db).RunAsync(now);

        var settled = await Clarifications(db).Find(Builders<BsonDocument>.Filter.Eq("_id", stale)).SingleAsync();
        Assert.Equal("dropped", settled["status"].AsString);
        Assert.Equal("Settled on the original guess.", settled["answer"].AsString);
        Assert.True(settled.Contains("resolvedAt"));

        var untouched = await Clarifications(db).Find(Builders<BsonDocument>.Filter.Eq("_id", fresh)).SingleAsync();
        Assert.Equal("open", untouched["status"].AsString);
    }

    [Fact]
    public async Task settles_a_deferred_question_too()
    {
        // Deliberately a bare `status: 'open'` test and NOT VisibleOpen() — composing
        // the kernel's visibility predicate here would leave deferred questions open
        // forever.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        var deferred = await SeedClarificationAsync(db, userId, "open", now.AddDays(-8), deferredUntil: now.AddDays(3));

        await Tick(db).RunAsync(now);

        var settled = await Clarifications(db).Find(Builders<BsonDocument>.Filter.Eq("_id", deferred)).SingleAsync();
        Assert.Equal("dropped", settled["status"].AsString);
    }

    [Fact]
    public async Task leaves_an_already_resolved_question_alone()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        var answered = await SeedClarificationAsync(db, userId, "answered", now.AddDays(-30));

        await Tick(db).RunAsync(now);

        var reloaded = await Clarifications(db).Find(Builders<BsonDocument>.Filter.Eq("_id", answered)).SingleAsync();
        Assert.Equal("answered", reloaded["status"].AsString);
        Assert.False(reloaded.Contains("answer"));
    }

    // ---- helpers -----------------------------------------------------------

    private static ReminderTick Tick(IMongoDatabase db) =>
        new(
            new ReminderTaskRepository(db),
            new NotificationRepository(db),
            new ReminderUserTimezoneReader(db),
            new StaleClarificationSettler(db));

    private static string Day(DateTime instant) => ReminderNotificationText.ShortDate(instant, "UTC");

    /// <summary>
    /// A planned reminder entry. <c>firedAt</c> is ABSENT rather than null — that
    /// is how <c>ReminderPlanner</c> writes one, and the claim filter has to match
    /// a missing field, not just an explicit null.
    /// </summary>
    private static BsonDocument Entry(DateTime at, string kind) =>
        new() { ["at"] = at, ["kind"] = kind };

    private static IMongoCollection<BsonDocument> Tasks(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>(MongoCollections.Tasks);

    private static IMongoCollection<BsonDocument> Notifications(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>(MongoCollections.Notifications);

    private static IMongoCollection<BsonDocument> Clarifications(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>(MongoCollections.Clarifications);

    /// <summary>
    /// The worker sweeps EVERY account, so each case gets a fresh owner and the
    /// collections are cleared — otherwise one case's leftovers land in the next
    /// one's batch.
    /// </summary>
    private static async Task<ObjectId> SeedUserAsync(IMongoDatabase db, string? timezone)
    {
        await Tasks(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Empty);
        await Notifications(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Empty);
        await Clarifications(db).DeleteManyAsync(Builders<BsonDocument>.Filter.Empty);

        var userId = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = userId,
            ["email"] = $"{userId}@probe.test",

            // `users` carries a UNIQUE index on identityUserId. Omitting it stores
            // null, and the second seeded user in the database's lifetime collides.
            ["identityUserId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
        };

        if (timezone is not null)
        {
            user["timezone"] = timezone;
        }

        await db.GetCollection<BsonDocument>(MongoCollections.Users).InsertOneAsync(user);

        return userId;
    }

    private static Task<ObjectId> SeedTaskAsync(
        IMongoDatabase db,
        ObjectId userId,
        string title,
        DateTime? dueAt,
        params BsonDocument[] reminders) =>
        SeedTaskAsync(db, userId, title, dueAt, reminders, status: "open", deletedAt: null);

    private static async Task<ObjectId> SeedTaskAsync(
        IMongoDatabase db,
        ObjectId userId,
        string title,
        DateTime? dueAt,
        IEnumerable<BsonDocument> reminders,
        string status = "open",
        DateTime? deletedAt = null)
    {
        var id = ObjectId.GenerateNewId();
        var task = new BsonDocument
        {
            ["_id"] = id,
            ["userId"] = userId,
            ["title"] = title,
            ["domain"] = "home",
            ["kind"] = "task",
            ["status"] = status,
            ["priority"] = "normal",
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(reminders),
            ["rescheduleCount"] = 0,
            ["createdAt"] = DateTime.UtcNow,
            ["updatedAt"] = DateTime.UtcNow,
            ["__v"] = 0,
        };

        if (dueAt is { } due)
        {
            task["dueAt"] = due;
        }

        if (deletedAt is { } deleted)
        {
            task["deletedAt"] = deleted;
        }

        await Tasks(db).InsertOneAsync(task);

        return id;
    }

    private static async Task<ObjectId> SeedClarificationAsync(
        IMongoDatabase db,
        ObjectId userId,
        string status,
        DateTime createdAt,
        DateTime? deferredUntil = null)
    {
        var id = ObjectId.GenerateNewId();
        var clarification = new BsonDocument
        {
            ["_id"] = id,
            ["userId"] = userId,
            ["taskId"] = ObjectId.GenerateNewId(),
            ["kind"] = "choice",
            ["status"] = status,
            ["question"] = "Which one?",
            ["options"] = new BsonArray(),
            ["createdAt"] = createdAt,
            ["updatedAt"] = createdAt,
            ["__v"] = 0,
        };

        if (deferredUntil is { } deferred)
        {
            clarification["deferredUntil"] = deferred;
        }

        await Clarifications(db).InsertOneAsync(clarification);

        return id;
    }

    /// <summary>
    /// A private database for the worker suite — its queries are NOT user-scoped,
    /// so it must not share a collection with the endpoint tests.
    /// </summary>
    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(WorkerDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
