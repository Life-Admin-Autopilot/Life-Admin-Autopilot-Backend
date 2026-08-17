using Life_Admin_Autopilot.BLL.Kernel.Reminders;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// <see cref="ReminderPlanner"/> — the seam that turns a matter into the rows the
/// worker and the device both read.
///
/// <para>
/// <b>It had no test of its own.</b> The tables either side of it are pure and now
/// well covered, but the planner is where a duration is resolved, handed to the
/// schedule, and written to Mongo — and a mistake in that wiring produces a
/// perfectly plausible schedule at the wrong times.
/// </para>
///
/// <para>
/// Needs a real Mongo, so every case skips when the parity instance is down, as the
/// rest of this suite does.
/// </para>
/// </summary>
public sealed class ReminderPlannerTests
{
    private const string PlannerDatabase = "kitto_parity_dotnet_planner_tests";

    private static readonly DateTime Now = new(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task reserves_the_matters_own_estimate_before_the_deadline()
    {
        // The end-to-end Phase 2 claim: a matter that says it takes 90 minutes gets
        // its final nudge 90 minutes early, in the row the device will read.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var due = Now.AddDays(20);
        var task = await SeedAsync(db, "Sort out the paperwork", "home", due, Estimate(30, 90));

        await Planner(db).SetRulesRemindersAsync(task, Now);

        var saved = await ReloadAsync(db, task.Id);
        Assert.Equal(due.AddMinutes(-90), saved.Reminders[^1].At);
        Assert.Equal("due", saved.Reminders[^1].Kind);
    }

    [Fact]
    public async Task falls_back_to_the_table_when_the_matter_carries_no_estimate()
    {
        // Which is almost every matter today: neither the chat agent nor the voice
        // extractor produces an estimate, so the derived default is the normal path
        // rather than the exceptional one.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var due = Now.AddDays(20);
        var task = await SeedAsync(db, "File tax return", "finance", due, estimate: null);

        await Planner(db).SetRulesRemindersAsync(task, Now);

        var saved = await ReloadAsync(db, task.Id);
        Assert.Equal(due.AddMinutes(-120), saved.Reminders[^1].At);
    }

    [Fact]
    public async Task plans_the_heads_up_from_the_deadline_and_the_final_nudge_from_the_window()
    {
        // The two halves are measured from different things and must not be confused:
        // the heads-up is 14 days before the DEADLINE, the final nudge 120 minutes
        // before it.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var due = Now.AddDays(20);
        var task = await SeedAsync(db, "File tax return", "finance", due, estimate: null);

        await Planner(db).SetRulesRemindersAsync(task, Now);

        var saved = await ReloadAsync(db, task.Id);
        Assert.Equal(2, saved.Reminders.Count);
        Assert.Equal(due.AddDays(-14), saved.Reminders[0].At);
        Assert.Equal("lead", saved.Reminders[0].Kind);
        Assert.Equal(due.AddMinutes(-120), saved.Reminders[1].At);
    }

    [Fact]
    public async Task writes_the_schedule_with_fired_at_absent_so_the_claim_filter_matches()
    {
        // The worker claims on {firedAt: null}, which in Mongo also matches a MISSING
        // field. Storing an explicit null would work; storing anything else would
        // make every planned reminder unclaimable and silent.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var task = await SeedAsync(db, "File tax return", "finance", Now.AddDays(20), null);

        await Planner(db).SetRulesRemindersAsync(task, Now);

        var raw = await db.GetCollection<BsonDocument>(MongoCollections.Tasks)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", task.Id))
            .SingleAsync();

        foreach (var entry in raw["reminders"].AsBsonArray)
        {
            Assert.False(entry.AsBsonDocument.Contains("firedAt"));
        }
    }

    [Fact]
    public async Task plans_nothing_for_a_list_item()
    {
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var task = await SeedAsync(db, "File tax return", "finance", Now.AddDays(20), null, kind: "list");

        await Planner(db).SetRulesRemindersAsync(task, Now);

        Assert.Empty((await ReloadAsync(db, task.Id)).Reminders);
    }

    [Fact]
    public async Task fires_a_snooze_once_at_the_snooze_moment_with_no_window_applied()
    {
        // Snooze is the user naming a time. Subtracting an estimate from it would
        // move a moment they chose, which is the one instant the system must not
        // second-guess.
        var db = TryGetDatabase();
        if (db is null)
        {
            return;
        }

        var task = await SeedAsync(db, "File tax return", "finance", Now.AddDays(20), Estimate(60, 120));
        var until = Now.AddHours(6);
        task.SnoozedUntil = until;

        await Planner(db).SetSnoozeReminderAsync(task);

        var saved = await ReloadAsync(db, task.Id);
        var only = Assert.Single(saved.Reminders);
        Assert.Equal(until, only.At);
        Assert.Equal("due", only.Kind);
    }

    // ---- helpers ---------------------------------------------------------------

    private static ReminderPlanner Planner(IMongoDatabase db) =>
        new(db, new NullReminderRefiner(), NullLogger<ReminderPlanner>.Instance);

    private static TaskEstimateDocument Estimate(int min, int max) =>
        new() { MinMinutes = min, MaxMinutes = max, Source = "ai" };

    private static async Task<TaskDocument> SeedAsync(
        IMongoDatabase db,
        string title,
        string domain,
        DateTime dueAt,
        TaskEstimateDocument? estimate,
        string kind = "reminder")
    {
        var task = new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = ObjectId.GenerateNewId(),
            Title = title,
            Domain = domain,
            Kind = kind,
            Status = "open",
            Priority = "normal",
            DueAt = dueAt,
            Estimate = estimate,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        await db.GetCollection<TaskDocument>(MongoCollections.Tasks).InsertOneAsync(task);
        return task;
    }

    private static Task<TaskDocument> ReloadAsync(IMongoDatabase db, ObjectId id) =>
        db.GetCollection<TaskDocument>(MongoCollections.Tasks)
            .Find(Builders<TaskDocument>.Filter.Eq(t => t.Id, id))
            .SingleAsync();

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(PlannerDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
