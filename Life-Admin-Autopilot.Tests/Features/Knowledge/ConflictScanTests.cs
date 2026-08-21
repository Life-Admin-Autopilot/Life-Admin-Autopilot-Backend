using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.DAL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Knowledge;

/// <summary>
/// The account-wide clash scan behind <c>GET /me/conflicts</c> and the briefing.
///
/// <para>
/// It is one method with two callers that disagree about scope, which is exactly
/// the shape that gets copied and then drifts: the briefing bounds it to the end
/// of the user's today, the conflicts list does not bound it at all. These pin
/// the parts a copy would get subtly wrong — that a pair is reported once rather
/// than from both ends, that the bound is honoured, and that the side offered to
/// move is the one the urgency rule chose.
/// </para>
///
/// <para>
/// Skips itself when the parity Mongo instance is unreachable, like every other
/// Mongo-backed suite here — the pool is passed in, but the duplicate half of
/// <see cref="ConflictService.CheckAsync"/> still reaches for a repository.
/// </para>
/// </summary>
public sealed class ConflictScanTests
{
    private const string ScanDatabase = "kitto_parity_dotnet_conflictscan_tests";

    private static readonly DateTime Now = new(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Midday = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task reports_a_clashing_pair_once_rather_than_from_both_ends()
    {
        var agent = Agent();
        if (agent is null) return;

        // Two hour-long matters thirty minutes apart: each overlaps the other, so a
        // walk that did not dedupe would meet the same fact twice.
        var pool = new[]
        {
            Matter("File the tax return", "finance", Midday, 60),
            Matter("Dentist appointment", "health", Midday.AddMinutes(30), 60),
        };

        var clashes = await agent.ScanAsync(ObjectId.GenerateNewId(), pool: pool, at: Now);

        Assert.Single(clashes);
    }

    [Fact]
    public async Task names_both_sides_of_the_clash()
    {
        var agent = Agent();
        if (agent is null) return;

        var pool = new[]
        {
            Matter("File the tax return", "finance", Midday, 60),
            Matter("Dentist appointment", "health", Midday.AddMinutes(30), 60),
        };

        var clash = Assert.Single(await agent.ScanAsync(ObjectId.GenerateNewId(), pool: pool, at: Now));

        // The list screen has no context of its own, so a row that named only one
        // matter would leave the user to guess what it collided with.
        var titles = new[] { clash.Title, clash.Other.Title };
        Assert.Contains("File the tax return", titles);
        Assert.Contains("Dentist appointment", titles);
    }

    [Fact]
    public async Task offers_to_move_the_lower_priority_side()
    {
        var agent = Agent();
        if (agent is null) return;

        var urgent = Matter("Renew the policy before it lapses", "finance", Midday, 60, "urgent");
        var casual = Matter("Book a haircut", "home", Midday.AddMinutes(30), 60, "low");

        var clash = Assert.Single(
            await agent.ScanAsync(ObjectId.GenerateNewId(), pool: new[] { urgent, casual }, at: Now));

        // Whichever end the walk met first, the haircut is the one that gives way.
        Assert.Equal(casual.Id, clash.YieldsTaskId);
    }

    [Fact]
    public async Task honours_the_until_bound_that_the_briefing_passes()
    {
        var agent = Agent();
        if (agent is null) return;

        // The same clash, moved to next week. Unbounded it is found; bounded to the
        // end of today it is not — which is the whole difference between the two
        // callers of this method.
        var nextWeek = Midday.AddDays(7);
        var pool = new[]
        {
            Matter("File the tax return", "finance", nextWeek, 60),
            Matter("Dentist appointment", "health", nextWeek.AddMinutes(30), 60),
        };

        var userId = ObjectId.GenerateNewId();
        var endOfToday = new DateTime(2026, 8, 20, 23, 59, 59, DateTimeKind.Utc);

        Assert.Single(await agent.ScanAsync(userId, pool: pool, at: Now));
        Assert.Empty(await agent.ScanAsync(userId, until: endOfToday, pool: pool, at: Now));
    }

    [Fact]
    public async Task an_undated_matter_clashes_with_nothing()
    {
        var agent = Agent();
        if (agent is null) return;

        // It occupies no span, so there is nothing for another matter to overlap.
        // Left in the pool regardless, exactly as every other caller passes it.
        var pool = new[]
        {
            Matter("Buy milk", "home", null, 60),
            Matter("Dentist appointment", "health", Midday, 60),
        };

        Assert.Empty(await agent.ScanAsync(ObjectId.GenerateNewId(), pool: pool, at: Now));
    }

    [Fact]
    public async Task an_empty_account_produces_no_clashes()
    {
        var agent = Agent();
        if (agent is null) return;

        Assert.Empty(await agent.ScanAsync(
            ObjectId.GenerateNewId(), pool: Array.Empty<TaskDocument>(), at: Now));
    }

    // ---- helpers ---------------------------------------------------------------

    private static TaskDocument Matter(
        string title,
        string domain,
        DateTime? dueAt,
        int maxMinutes,
        string priority = "normal") =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            UserId = ObjectId.GenerateNewId(),
            Title = title,
            Domain = domain,
            Priority = priority,
            Kind = "reminder",
            Status = "open",
            DueAt = dueAt,
            // Explicit, so these read against the overlap rule rather than against
            // whatever the keyword and domain tables happen to say this week.
            Estimate = new TaskEstimateDocument
            {
                MinMinutes = maxMinutes,
                MaxMinutes = maxMinutes,
                Source = "user",
            },
        };

    /// <summary>
    /// The agent with its model half left unconfigured — <c>ScanAsync</c> never
    /// phrases anything, so the HttpClient and options below are never reached.
    /// </summary>
    private static KnowledgeAgentService? Agent()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(
                KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var db = new MongoClient(settings).GetDatabase(ScanDatabase);
            db.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            var knowledge = new KnowledgeService(
                new UnconfiguredEmbeddings(),
                new ContentChunkRepository(db, NullLogger<ContentChunkRepository>.Instance),
                NullLogger<KnowledgeService>.Instance);

            var conflicts = new ConflictService(
                new TaskRepository(db), knowledge, NullLogger<ConflictService>.Instance);

            return new KnowledgeAgentService(
                new HttpClient(),
                new PlanningOptions(),
                conflicts,
                NullLogger<KnowledgeAgentService>.Instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Its own copy rather than a share with <see cref="ConflictServiceTests"/>:
    /// a test double is not a rule, and hoisting one stub into shared scope to save
    /// eight lines couples two suites that should be able to move separately.
    /// Throws rather than returning an empty vector — nothing should reach it while
    /// <see cref="IsConfigured"/> is false, and a silent stub would hide it.
    /// </summary>
    private sealed class UnconfiguredEmbeddings : IEmbeddingProvider
    {
        public bool IsConfigured => false;

        public string Model => "none";

        public Task<float[]> EmbedAsync(
            string text,
            bool isQuery,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Embedding is not configured.");
    }
}
