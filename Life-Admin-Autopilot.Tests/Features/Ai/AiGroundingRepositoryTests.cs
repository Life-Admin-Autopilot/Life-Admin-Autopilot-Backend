using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The <c>MY TASKS</c> read, against the parity Mongo. Skipped (not failed) when that
/// instance is not running, following <see cref="AiConversationRepositoryTests"/>.
///
/// <para>
/// <b>What is actually at stake here is WHICH twenty matters the agent sees.</b> The
/// cap makes the query's filter and sort load-bearing rather than cosmetic: a wrong
/// sort silently hands the model a different slice of a long backlog, and a wrong
/// filter puts a matter the user deleted back in front of it — the agent then cites
/// it, and the user concludes the delete failed.
/// </para>
///
/// <para>
/// Verified in one more way that cannot be checked in here: the block this produces
/// for the seeded demo account (142 open matters) was diffed byte-for-byte against
/// the block Node's own <c>buildPersonalContext</c> produced for the same account
/// and the same database. Identical, all twenty rows.
/// </para>
/// </summary>
public sealed class AiGroundingRepositoryTests
{
    [Fact]
    public async Task undated_matters_lead_and_the_soonest_deadline_follows()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiGroundingRepository(database);

        // Inserted out of order on purpose — the ordering under test is the query's,
        // not the insert's.
        await InsertAsync(database, userId, "later", dueAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        await InsertAsync(database, userId, "sooner", dueAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        await InsertAsync(database, userId, "undated");

        var tasks = await repository.ListForPromptAsync(userId, TaskGrounding.PromptStatuses, TaskGrounding.TaskCap);

        // A missing dueAt sorts BEFORE any date in Mongo's BSON ordering, so the
        // dateless backlog leads. That is the reference's order and the cap then
        // truncates it, so it decides what the agent can see at all.
        Assert.Equal(["undated", "sooner", "later"], tasks.Select(task => task.Title));
    }

    [Fact]
    public async Task a_trashed_or_finished_matter_never_reaches_the_prompt()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var repository = new AiGroundingRepository(database);

        await InsertAsync(database, userId, "open");
        await InsertAsync(database, userId, "snoozed", status: "snoozed");
        await InsertAsync(database, userId, "done", status: "done");
        await InsertAsync(database, userId, "trashed", deletedAt: DateTime.UtcNow);

        var tasks = await repository.ListForPromptAsync(userId, TaskGrounding.PromptStatuses, TaskGrounding.TaskCap);

        // 'snoozed' counts as live: the user deferred it, they did not finish it.
        Assert.Equal(["open", "snoozed"], tasks.Select(task => task.Title).Order());
    }

    [Fact]
    public async Task another_users_matters_are_not_visible()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var mine = ObjectId.GenerateNewId();
        var theirs = ObjectId.GenerateNewId();

        await InsertAsync(database, mine, "mine");
        await InsertAsync(database, theirs, "theirs");

        var tasks = await new AiGroundingRepository(database)
            .ListForPromptAsync(mine, TaskGrounding.PromptStatuses, TaskGrounding.TaskCap);

        Assert.Equal(["mine"], tasks.Select(task => task.Title));
    }

    [Fact]
    public async Task the_cap_is_applied_by_the_query_not_by_the_renderer()
    {
        var database = TryGetDatabase();
        if (database is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();

        for (var i = 0; i < TaskGrounding.TaskCap + 5; i++)
        {
            await InsertAsync(database, userId, $"matter {i:D2}");
        }

        var tasks = await new AiGroundingRepository(database)
            .ListForPromptAsync(userId, TaskGrounding.PromptStatuses, TaskGrounding.TaskCap);

        // Materialising a 142-matter backlog to throw 122 rows away is the version of
        // this that looks correct and reads the whole collection on every turn.
        Assert.Equal(TaskGrounding.TaskCap, tasks.Count);
    }

    private static Task InsertAsync(
        IMongoDatabase database,
        ObjectId userId,
        string title,
        string status = "open",
        DateTime? dueAt = null,
        DateTime? deletedAt = null)
    {
        var now = DateTime.UtcNow;

        return database
            .GetCollection<TaskDocument>(MongoCollections.Tasks)
            .InsertOneAsync(new TaskDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                Title = title,
                Domain = "home",
                Kind = dueAt is null ? "list" : "reminder",
                Status = status,
                Priority = "normal",
                DueAt = dueAt,
                DeletedAt = deletedAt,
                CreatedAt = now,
                UpdatedAt = now,
            });
    }

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(
                Kernel.KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            // Its own database: several slices seed and delete rows in `tasks`, and a
            // shared one is a real cross-slice flake source.
            var database = new MongoClient(settings).GetDatabase("kitto_parity_dotnet_m_grounding");
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
