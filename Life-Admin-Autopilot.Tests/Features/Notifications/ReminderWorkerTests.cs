using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
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

    // ---- delivery ----------------------------------------------------------
    //
    // The tick used to write the notification row and stop. That row is only read
    // when the user opens the app, so a reminder was delivered exactly to the people
    // who were already looking — which is the one outcome the product exists to
    // prevent.

    [Fact]
    public async Task delivers_a_fired_reminder_to_the_users_devices()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "Africa/Cairo");
        var now = DateTime.UtcNow;
        var dueAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        var taskId = await SeedTaskAsync(db, userId, "Renew the car licence", dueAt, Entry(now.AddMinutes(-1), "due"));

        var harness = NewTick(db);
        harness.Devices.Seed(Device(userId, "device-token-1"));

        Assert.Equal(1, await harness.Tick.RunAsync(now));

        var sent = Assert.Single(harness.Push.Requests);
        Assert.Equal("device-token-1", sent.DeviceToken);
        Assert.Equal("Renew the car licence", sent.Title);

        // The matter it is about travels with it, so opening the notification can
        // land on that matter rather than on the dashboard.
        Assert.Equal("reminder", sent.Data!["kind"]);
        Assert.Equal(taskId.ToString(), sent.Data["taskId"]);
    }

    // Turning push off silences the phone, not the app: the row is the record and
    // the in-app list still has to show it.
    [Fact]
    public async Task writes_the_row_but_sends_nothing_when_push_is_declined()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "Africa/Cairo", wantsPush: false);
        var now = DateTime.UtcNow;
        await SeedTaskAsync(db, userId, "Quiet one", now.AddDays(1), Entry(now.AddMinutes(-1), "due"));

        var harness = NewTick(db);
        harness.Devices.Seed(Device(userId, "device-token-1"));

        Assert.Equal(1, await harness.Tick.RunAsync(now));
        Assert.Empty(harness.Push.Requests);

        var written = await Notifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .SingleAsync();
        Assert.Equal("Quiet one", written["title"].AsString);
    }

    // A provider outage must not cost the reminder: the claim is already taken and
    // the row already written, so failing the tick would lose it for good.
    [Fact]
    public async Task a_failed_send_still_leaves_the_reminder_fired_and_recorded()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "Africa/Cairo");
        var now = DateTime.UtcNow;
        var taskId = await SeedTaskAsync(db, userId, "Still recorded", now.AddDays(1), Entry(now.AddMinutes(-1), "due"));

        var tick = new ReminderTick(
            new ReminderTaskRepository(db),
            new NotificationRepository(db),
            new ReminderUserTimezoneReader(db),
            new StaleClarificationSettler(db),
            new NotificationService(
                new InMemoryDeviceTokenRepository(),
                StubPushNotificationService.AlwaysFails(PushErrorCodes.Unavailable),
                NullLogger<NotificationService>.Instance),
            new DocumentScanNotifications(db),
            NullLogger<ReminderTick>.Instance);

        Assert.Equal(1, await tick.RunAsync(now));

        var reloaded = await Tasks(db).Find(Builders<BsonDocument>.Filter.Eq("_id", taskId)).SingleAsync();
        Assert.NotEqual(BsonNull.Value, reloaded["reminders"][0]["firedAt"]);
        Assert.Equal(1, await Notifications(db).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("userId", userId)));
    }

    // ---- helpers -----------------------------------------------------------

    // ---- urgency ordering (spec Phase 1) ---------------------------------------

    [Fact]
    public async Task writes_a_batch_so_the_most_urgent_matter_ends_up_on_top_of_the_feed()
    {
        // Before this, a tick that fired five reminders at once handed them over in
        // whatever order Mongo returned — FindDueBatchAsync carries no sort at all.
        //
        // The assertion runs against the SAME sort the feed endpoint uses
        // ({createdAt: -1}), because that is the only place the write order becomes
        // observable. Each iteration of the tick is at least two round trips, so no
        // two rows in one batch share a millisecond.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;
        var due = now.AddMinutes(5);

        // Identical in every respect except the priority the user gave them, so
        // nothing but the ranking can explain the order.
        await SeedTaskAsync(db, userId, "Calm matter", due, new[] { Entry(now, "due") }, priority: "low");
        await SeedTaskAsync(db, userId, "Loud matter", due, new[] { Entry(now, "due") }, priority: "urgent");
        await SeedTaskAsync(db, userId, "Middling matter", due, new[] { Entry(now, "due") }, priority: "normal");

        Assert.Equal(3, await Tick(db).RunAsync(now));

        Assert.Equal(
            new[] { "Loud matter", "Middling matter", "Calm matter" },
            await FeedTitlesAsync(db, userId));
    }

    [Fact]
    public async Task ranks_the_nearer_deadline_higher_among_equally_important_matters()
    {
        // The deadline half of the score, end to end. Both are 'normal', both are in
        // the 'home' domain (a 5-day warning window), so only how far each has
        // travelled through that window separates them.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;

        await SeedTaskAsync(db, userId, "Due in four days", now.AddDays(4), new[] { Entry(now, "due") });
        await SeedTaskAsync(db, userId, "Due in one day", now.AddDays(1), new[] { Entry(now, "due") });

        Assert.Equal(2, await Tick(db).RunAsync(now));

        Assert.Equal(
            new[] { "Due in one day", "Due in four days" },
            await FeedTitlesAsync(db, userId));
    }

    [Fact]
    public async Task keeps_a_stated_priority_above_a_merely_nearer_deadline()
    {
        // The judgement call the score encodes, pinned so a future reweighting is a
        // deliberate act: what the user CALLED urgent outranks what we inferred is
        // pressing. Flip the weighting and this is the test that goes red.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;

        await SeedTaskAsync(db, userId, "Trivial but imminent", now, new[] { Entry(now, "due") }, priority: "low");
        await SeedTaskAsync(
            db,
            userId,
            "Urgent but distant",
            now.AddDays(5),
            new[] { Entry(now, "due") },
            priority: "urgent");

        Assert.Equal(2, await Tick(db).RunAsync(now));

        Assert.Equal(
            new[] { "Urgent but distant", "Trivial but imminent" },
            await FeedTitlesAsync(db, userId));
    }

    [Fact]
    public async Task gives_every_row_in_a_batch_its_own_millisecond()
    {
        // The defect this pins was found by a flaky ordering test, not by reading the
        // code: against a local Mongo, consecutive claims-and-writes finish inside one
        // millisecond, BSON stores no finer, and {createdAt: -1} has no tie-break — so
        // the ranking survived or not depending on machine speed.
        //
        // Asserting distinctness rather than order makes the failure legible: an
        // ordering assertion alone goes red intermittently and reads as "the ranking
        // is wrong" when the real fault is that the rows are indistinguishable.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await SeedUserAsync(db, "UTC");
        var now = DateTime.UtcNow;

        for (var i = 0; i < 12; i++)
        {
            await SeedTaskAsync(db, userId, $"Matter {i}", now.AddMinutes(5), new[] { Entry(now, "due") });
        }

        Assert.Equal(12, await Tick(db).RunAsync(now));

        var stamps = await Notifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .Project(Builders<BsonDocument>.Projection.Include("createdAt"))
            .ToListAsync();

        var distinct = stamps.Select(s => s["createdAt"].ToUniversalTime()).Distinct().Count();

        Assert.Equal(12, distinct);
    }

    /// <summary>
    /// The feed's own ordering — <c>{createdAt: -1}</c>, newest first, exactly as
    /// <c>GET /me/notifications</c> reads it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FeedTitlesAsync(IMongoDatabase db, ObjectId userId)
    {
        var rows = await Notifications(db)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .Sort(Builders<BsonDocument>.Sort.Descending("createdAt"))
            .ToListAsync();

        return rows.Select(r => r["title"].AsString).ToList();
    }

    private static ReminderTick Tick(IMongoDatabase db) => NewTick(db).Tick;

    /// <summary>
    /// A tick wired to a real <see cref="NotificationService"/> over an in-memory
    /// device store, so a test can see what a fired reminder actually sent. The
    /// delivery half used not to exist at all — a null double here would let it go
    /// missing again without a single test turning red.
    /// </summary>
    private sealed record Harness(
        ReminderTick Tick,
        StubPushNotificationService Push,
        InMemoryDeviceTokenRepository Devices);

    private static Harness NewTick(IMongoDatabase db)
    {
        var push = StubPushNotificationService.AlwaysSucceeds();
        var devices = new InMemoryDeviceTokenRepository();

        var tick = new ReminderTick(
            new ReminderTaskRepository(db),
            new NotificationRepository(db),
            new ReminderUserTimezoneReader(db),
            new StaleClarificationSettler(db),
            new NotificationService(devices, push, NullLogger<NotificationService>.Instance),
            new DocumentScanNotifications(db),
            NullLogger<ReminderTick>.Instance);

        return new Harness(tick, push, devices);
    }

    private static DeviceToken Device(ObjectId userId, string token) => new()
    {
        // Keyed by the JWT subject, which SessionService signs as the Mongo user id.
        UserId = userId.ToString(),
        Token = token,
        Platform = DevicePlatform.Android,
        RegisteredAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        IsActive = true,
    };

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
    private static async Task<ObjectId> SeedUserAsync(IMongoDatabase db, string? timezone, bool wantsPush = true)
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

        if (!wantsPush)
        {
            user["notifications"] = new BsonDocument { ["push"] = false };
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
        DateTime? deletedAt = null,
        string priority = "normal")
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
            ["priority"] = priority,
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
