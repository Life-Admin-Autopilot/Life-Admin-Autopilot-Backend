using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The three reconcile rules, driven directly.
///
/// <para>
/// This type is shared by the ICS and Google importers and had NO test coverage
/// while it existed as two divergent copies — which is most of why the copies were
/// able to drift. These are the rules a divergence would break, so they are pinned
/// here rather than in either slice's folder.
/// </para>
///
/// <para>
/// Needs a real Mongo (upsert-by-compound-key and the reminder strip have no
/// in-memory stand-in), so every case skips when the parity instance is down,
/// matching the convention in the rest of this suite.
/// </para>
/// </summary>
public sealed class ExternalMatterReconcilerTests
{
    private const string ReconcilerDatabase = "kitto_parity_dotnet_reconciler_tests";

    private const string Source = "ics_feed";

    private static readonly DateTime Now = new(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Due = new(2026, 3, 20, 14, 0, 0, DateTimeKind.Utc);

    // ---- Rule 1: upsert on (userId, externalSource, externalId) ----------------

    [Fact]
    public async Task creates_a_matter_the_first_time_it_sees_an_external_id()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);

        var outcome = await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        Assert.True(outcome.Created);
        Assert.False(outcome.Updated);

        var stored = await SingleAsync(db, userId);
        Assert.Equal("Dentist", stored.Title);
        Assert.Equal("open", stored.Status);
        Assert.Equal(Due, stored.DueAt);
    }

    [Fact]
    public async Task a_second_poll_of_the_same_item_does_not_create_a_duplicate()
    {
        // The whole reason the module exists: every importer re-reads an
        // overlapping window, so a blind insert is one matter per poll.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);

        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);
        var second = await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now.AddHours(1));

        Assert.False(second.Created);
        Assert.False(second.Updated);
        Assert.Equal(1, await CountAsync(db, userId));
    }

    [Fact]
    public async Task the_same_external_id_under_a_different_source_is_a_different_matter()
    {
        // All THREE parts of the key. One slice's copy hardcoded its own source in
        // the lookup, which is invisible until two importers share an id.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);

        await Reconciler(db).ReconcileAsync(Input(userId, "shared-id"), Now);
        await Reconciler(db).ReconcileAsync(
            Input(userId, "shared-id") with { ExternalSource = "google_calendar" },
            Now);

        Assert.Equal(2, await CountAsync(db, userId));
    }

    // ---- Rule 2: a user-deleted matter stays deleted ---------------------------

    [Fact]
    public async Task never_resurrects_a_matter_the_user_deleted()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Update.Set(t => t.DeletedAt, Now));

        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { DueAt = Due.AddDays(1) },
            Now.AddHours(1));

        Assert.False(outcome.Created);
        Assert.False(outcome.Updated);
        Assert.Equal("user_deleted", outcome.Skipped);

        // And it is still deleted, with its ORIGINAL timing untouched.
        var stored = await SingleAsync(db, userId);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal(Due, stored.DueAt);
    }

    // ---- Rule 3: updates touch timing only -------------------------------------

    [Fact]
    public async Task a_moved_deadline_updates_timing_and_leaves_user_owned_fields_alone()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        // The user renamed it and refiled it. The source must not undo that.
        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Update
                .Set(t => t.Title, "Dentist — bring referral")
                .Set(t => t.Domain, "family")
                .Set(t => t.Notes, "mine"));

        var moved = Due.AddDays(2);
        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { DueAt = moved, Title = "Dentist", Domain = "health", Notes = "theirs" },
            Now.AddHours(1));

        Assert.True(outcome.Updated);

        var stored = await SingleAsync(db, userId);
        Assert.Equal(moved, stored.DueAt);
        Assert.Equal("Dentist — bring referral", stored.Title);
        Assert.Equal("family", stored.Domain);
        Assert.Equal("mine", stored.Notes);
    }

    [Fact]
    public async Task a_shift_under_a_minute_is_the_same_moment_and_writes_nothing()
    {
        // Sources round differently. A re-plan on every poll would clear firedAt and
        // re-fire nudges the user has already had.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { DueAt = Due.AddSeconds(59) },
            Now.AddHours(1));

        Assert.False(outcome.Updated);
        Assert.Equal(Due, (await SingleAsync(db, userId)).DueAt);
    }

    [Fact]
    public async Task a_shift_over_a_minute_is_a_real_move()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        var moved = Due.AddSeconds(61);
        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { DueAt = moved },
            Now.AddHours(1));

        Assert.True(outcome.Updated);
        Assert.Equal(moved, (await SingleAsync(db, userId)).DueAt);
    }

    [Fact]
    public async Task a_kind_change_alone_is_a_real_move()
    {
        // An occurrence that became ambiguous must stop firing even though its
        // deadline did not shift.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { Kind = "list" },
            Now.AddHours(1));

        Assert.True(outcome.Updated);
        Assert.Equal("list", (await SingleAsync(db, userId)).Kind);
    }

    // ---- Completion propagates one way only ------------------------------------

    [Fact]
    public async Task the_source_can_close_a_matter()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { Completed = true },
            Now.AddHours(1));

        Assert.True(outcome.Updated);

        var stored = await SingleAsync(db, userId);
        Assert.Equal("done", stored.Status);
        Assert.Equal(Now.AddHours(1), stored.CompletedAt);
    }

    [Fact]
    public async Task the_source_can_never_reopen_one_the_user_ticked_off()
    {
        // A stale row upstream must not un-tick something the user finished.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Update.Set(t => t.Status, "done").Set(t => t.CompletedAt, Now));

        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now.AddHours(1));

        Assert.Equal("done", (await SingleAsync(db, userId)).Status);
    }

    [Fact]
    public async Task an_item_that_arrives_already_finished_is_not_created()
    {
        // Importing a year of completed items as fresh matters would bury the real
        // list.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);

        var outcome = await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-done") with { Completed = true },
            Now);

        Assert.False(outcome.Created);
        Assert.Equal(0, await CountAsync(db, userId));
    }

    // ---- sourceHasOwnAlerts strips the at-due nudge -----------------------------

    [Fact]
    public async Task keeps_kitto_s_at_due_nudge_when_the_source_does_not_alert()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        Assert.Contains((await SingleAsync(db, userId)).Reminders, e => e.Kind == "due");
    }

    [Fact]
    public async Task strips_the_at_due_nudge_when_the_source_alerts_at_the_deadline()
    {
        // Google says "in 10 minutes"; Kitto's lead-time nudge is additive and stays,
        // its duplicate at-due nudge does not.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);

        await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { SourceHasOwnAlerts = true },
            Now);

        var stored = await SingleAsync(db, userId);
        Assert.DoesNotContain(stored.Reminders, e => e.Kind == "due");
        Assert.NotEmpty(stored.Reminders);
    }

    [Fact]
    public async Task a_timing_update_moves_updated_at_to_the_poll_time()
    {
        // KERNEL.md §7.0 — the reference gets `updatedAt` from Mongoose's
        // `timestamps: true` and never names the field, so a line-by-line port
        // leaves it stale. This pins the update path, which is the one a reader can
        // observe through `GET /me/export`.
        //
        // NOTE it does NOT pin `DropDuplicateDueNudgeAsync`'s own `updatedAt` write:
        // that helper only runs straight after the insert or the timing update, both
        // of which already stamped the same `now`, so its write is unobservable by
        // construction. See the comment on that method.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        var polledAt = Now.AddHours(3);
        await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { DueAt = Due.AddDays(2) },
            polledAt);

        Assert.Equal(polledAt, (await SingleAsync(db, userId)).UpdatedAt);
    }

    [Fact]
    public async Task the_source_closing_a_matter_moves_updated_at_too()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var userId = await FreshUserAsync(db);
        await Reconciler(db).ReconcileAsync(Input(userId, "evt-1"), Now);

        var closedAt = Now.AddHours(5);
        await Reconciler(db).ReconcileAsync(
            Input(userId, "evt-1") with { Completed = true },
            closedAt);

        Assert.Equal(closedAt, (await SingleAsync(db, userId)).UpdatedAt);
    }

    // ---- helpers ----------------------------------------------------------------

    private static ExternalMatterInput Input(ObjectId userId, string externalId) =>
        new(
            userId,
            Source,
            externalId,
            "Dentist",
            "health",
            Due,
            "reminder",
            "exact",
            "high");

    private static ExternalMatterReconciler Reconciler(IMongoDatabase db) =>
        new(db, new ReminderPlanner(db, new NullReminderRefiner(), NullLogger<ReminderPlanner>.Instance));

    private static IMongoCollection<TaskDocument> Tasks(IMongoDatabase db) =>
        db.GetCollection<TaskDocument>(MongoCollections.Tasks);

    private static Task<TaskDocument> SingleAsync(IMongoDatabase db, ObjectId userId) =>
        Tasks(db).Find(Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId)).SingleAsync();

    private static async Task<long> CountAsync(IMongoDatabase db, ObjectId userId) =>
        await Tasks(db).CountDocumentsAsync(Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId));

    /// <summary>
    /// A fresh owner per case, and the collection cleared — the reconciler queries
    /// by user, but a leftover row from a previous case still breaks the
    /// single-document assertions.
    /// </summary>
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

            var database = new MongoClient(settings).GetDatabase(ReconcilerDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
