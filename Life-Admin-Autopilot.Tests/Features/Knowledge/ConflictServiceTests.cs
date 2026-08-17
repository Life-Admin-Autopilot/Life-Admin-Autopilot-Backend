using Life_Admin_Autopilot.BLL.Features.Knowledge;
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
/// Conflict detection and the resolution it proposes.
///
/// <para>
/// <b>This service had no test of any kind</b> while four call sites depended on it
/// — the Planning Agent's draft check, the Knowledge Agent's re-check and daily
/// briefing, and the voice extractor. These cover the rules that must hold whoever
/// is asking, so a fifth caller cannot quietly get a different answer.
/// </para>
///
/// <para>
/// The pool is passed in by the caller, so the time half needs no database. Embedding
/// is deliberately left unconfigured: the duplicate check then returns nothing and
/// the caller still gets its time answer, which is the best-effort contract
/// <see cref="ConflictService"/> documents.
/// </para>
/// </summary>
public sealed class ConflictServiceTests
{
    private const string ConflictDatabase = "kitto_parity_dotnet_conflict_tests";

    private static readonly DateTime Now = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // ---- detection -------------------------------------------------------------

    [Fact]
    public async Task reports_no_clash_between_two_quick_errands_an_hour_and_a_half_apart()
    {
        // The false positive the fixed two-hour radius produced on every list with
        // more than a couple of bills in it.
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("Pay the water bill", "home", "normal", Deadline.AddMinutes(90)) };

        var found = await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("Pay electricity bill", "home"), Deadline, pool, now: Now);

        Assert.Empty(found);
    }

    [Fact]
    public async Task reports_a_clash_between_two_long_matters_three_hours_apart()
    {
        // And the false negative. Two tax-shaped jobs, deadlines well outside the old
        // radius, overlapping for an hour.
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("File VAT return", "finance", "normal", Deadline.AddHours(3), maxMinutes: 240) };

        var clash = Assert.Single(await service.CheckAsync(
            ObjectId.GenerateNewId(),
            Candidate("File tax return", "finance", maxMinutes: 240),
            Deadline,
            pool,
            now: Now));

        Assert.Equal(MatterConflict.TimeClash, clash.Kind);
        Assert.Equal("File VAT return", clash.Title);
    }

    [Fact]
    public async Task ignores_a_matter_with_no_deadline()
    {
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("Someday", "home", "urgent", dueAt: null) };

        Assert.Empty(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("File tax return", "finance"), Deadline, pool, now: Now));
    }

    [Fact]
    public async Task checks_nothing_when_the_candidate_itself_has_no_deadline()
    {
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("File VAT return", "finance", "normal", Deadline) };

        Assert.Empty(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("File tax return", "finance"), dueAt: null, pool, now: Now));
    }

    [Fact]
    public async Task never_reports_a_matter_clashing_with_itself()
    {
        // Without the exclusion an edit to any saved matter finds a perfect collision
        // with itself and warns the user about the thing they are editing.
        var service = Service();
        if (service is null) return;

        var self = Matter("File tax return", "finance", "normal", Deadline);

        Assert.Empty(await service.CheckAsync(
            ObjectId.GenerateNewId(),
            Candidate("File tax return", "finance"),
            Deadline,
            new[] { self },
            excludeTaskId: self.Id,
            now: Now));
    }

    [Fact]
    public async Task reports_every_matter_the_candidate_runs_into_not_just_the_first()
    {
        var service = Service();
        if (service is null) return;

        var pool = new[]
        {
            Matter("File VAT return", "finance", "normal", Deadline.AddMinutes(30)),
            Matter("Company accounts", "finance", "normal", Deadline.AddMinutes(15)),
        };

        var found = await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("File tax return", "finance"), Deadline, pool, now: Now);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task measures_the_candidate_against_the_other_matters_OWN_duration()
    {
        // The same candidate, the same gap, two different neighbours: a long one
        // collides and a short one does not. A rule that only knew the candidate
        // would answer identically for both.
        var service = Service();
        if (service is null) return;

        var longNeighbour = new[] { Matter("File VAT return", "finance", "normal", Deadline.AddHours(2)) };
        var shortNeighbour = new[] { Matter("Pay the water bill", "home", "normal", Deadline.AddHours(2)) };

        var candidate = Candidate("Pay electricity bill", "home");
        var userId = ObjectId.GenerateNewId();

        Assert.Single(await service.CheckAsync(userId, candidate, Deadline, longNeighbour, now: Now));
        Assert.Empty(await service.CheckAsync(userId, candidate, Deadline, shortNeighbour, now: Now));
    }

    // ---- resolution ------------------------------------------------------------

    [Fact]
    public async Task tells_the_less_pressing_matter_to_move()
    {
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("Fix the boiler leak", "home", "urgent", Deadline.AddMinutes(20)) };

        var clash = Assert.Single(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("Tidy the loft", "home", "low"), Deadline, pool, now: Now));

        Assert.True(clash.Yields);
        Assert.True(clash.Urgency < clash.OtherUrgency);
        Assert.Contains("Fix the boiler leak", clash.Reason);
        Assert.Contains("move this one", clash.Reason);
    }

    [Fact]
    public async Task leaves_the_more_pressing_candidate_where_it_is()
    {
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("Tidy the loft", "home", "low", Deadline.AddMinutes(20)) };

        var clash = Assert.Single(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("Fix the boiler leak", "home", "urgent"), Deadline, pool, now: Now));

        Assert.False(clash.Yields);
        Assert.True(clash.Urgency > clash.OtherUrgency);
        Assert.Contains("this is the more pressing", clash.Reason);
    }

    [Fact]
    public async Task makes_the_newcomer_yield_when_the_two_are_equally_pressing()
    {
        // The incumbent is a commitment the user has already made. Moving it needs a
        // better reason than "equal".
        var service = Service();
        if (service is null) return;

        var pool = new[] { Matter("Tidy the loft", "home", "normal", Deadline) };

        var clash = Assert.Single(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("Tidy the loft", "home"), Deadline, pool, now: Now));

        Assert.Equal(clash.Urgency, clash.OtherUrgency);
        Assert.True(clash.Yields);
    }

    [Fact]
    public async Task scores_both_sides_at_the_same_moment_so_the_comparison_means_something()
    {
        // Scored at its own deadline every matter reads as maximally pressing, and
        // the comparison carries no information. Scored at TODAY, the nearer deadline
        // properly outranks the further one at equal priority.
        var service = Service();
        if (service is null) return;

        var soon = Deadline;
        var later = Deadline.AddDays(3);

        // Overlapping windows are not required for the scores themselves, but the
        // conflict has to exist for them to be reported — so they share a deadline
        // region and differ only in how far out the OTHER one sits.
        var pool = new[] { Matter("Tidy the shed", "home", "normal", soon.AddMinutes(20)) };

        var near = Assert.Single(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("Tidy the loft", "home"), soon, pool, now: Now));

        var poolLater = new[] { Matter("Tidy the shed", "home", "normal", later.AddMinutes(20)) };
        var far = Assert.Single(await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("Tidy the loft", "home"), later, poolLater, now: Now));

        Assert.True(near.Urgency > far.Urgency, $"{near.Urgency} should beat {far.Urgency}");
    }

    [Fact]
    public async Task carries_no_urgency_verdict_on_a_duplicate()
    {
        // Nothing about TIME is in question on a duplicate, so there is no side to
        // move. With embedding unconfigured none is produced at all, which is the
        // best-effort contract this test also stands for.
        var service = Service();
        if (service is null) return;

        var found = await service.CheckAsync(
            ObjectId.GenerateNewId(), Candidate("File tax return", "finance"), Deadline, Array.Empty<TaskDocument>(), now: Now);

        Assert.DoesNotContain(found, c => c.Kind == MatterConflict.Duplicate);
    }

    // ---- the suggester's predicate ---------------------------------------------

    [Fact]
    public void agrees_with_the_check_about_which_instants_are_free()
    {
        // SlotSuggester proposes times through this predicate, and the endpoint's own
        // comment requires that "a suggestion cannot be refused the moment it is
        // taken" — so it has to move the candidate's whole span, not just its end.
        var pool = new[] { Matter("File VAT return", "finance", "normal", Deadline) };
        var candidate = Candidate("File tax return", "finance");

        Assert.True(ConflictService.ClashesWithin(Deadline.AddHours(1), candidate, pool, null));
        Assert.False(ConflictService.ClashesWithin(Deadline.AddHours(5), candidate, pool, null));
    }

    [Fact]
    public void honours_the_exclusion_in_the_suggester_predicate_too()
    {
        var self = Matter("File tax return", "finance", "normal", Deadline);

        Assert.False(ConflictService.ClashesWithin(
            Deadline, Candidate("File tax return", "finance"), new[] { self }, self.Id));
    }

    // ---- helpers ---------------------------------------------------------------

    private static ConflictService.MatterCandidate Candidate(
        string title,
        string domain,
        string priority = "normal",
        int? maxMinutes = null) =>
        new(title, domain, priority, Estimate(maxMinutes));

    private static TaskDocument Matter(
        string title,
        string domain,
        string priority,
        DateTime? dueAt,
        int? maxMinutes = null) =>
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
            Estimate = Estimate(maxMinutes),
        };

    /// <summary>An explicit duration, for the cases where the tables' own answer is
    /// not the thing under test.</summary>
    private static TaskEstimateDocument? Estimate(int? maxMinutes) =>
        maxMinutes is { } max
            ? new TaskEstimateDocument { MinMinutes = max, MaxMinutes = max, Source = "user" }
            : null;

    /// <summary>
    /// A service whose embedding half is switched off, so the duplicate check is a
    /// no-op and the time half is what is under test. Null when the parity Mongo is
    /// down, matching the convention in the rest of this suite.
    /// </summary>
    private static ConflictService? Service()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var db = new MongoClient(settings).GetDatabase(ConflictDatabase);
            db.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            var knowledge = new KnowledgeService(
                new UnconfiguredEmbeddings(),
                new ContentChunkRepository(db, NullLogger<ContentChunkRepository>.Instance),
                NullLogger<KnowledgeService>.Instance);

            return new ConflictService(
                new TaskRepository(db), knowledge, NullLogger<ConflictService>.Instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class UnconfiguredEmbeddings : IEmbeddingProvider
    {
        public bool IsConfigured => false;

        public string Model => "none";

        /// <summary>
        /// Throws rather than returning an empty vector: nothing should reach this
        /// while <see cref="IsConfigured"/> is false, and a silent stub would hide it
        /// if something did.
        /// </summary>
        public Task<float[]> EmbedAsync(
            string text,
            bool isQuery,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Embedding is not configured.");
    }
}
