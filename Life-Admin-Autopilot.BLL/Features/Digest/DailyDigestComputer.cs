using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Features.Digest;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>Everything a rebuild computes from documents. No prose.</summary>
/// <param name="Counts">The six headline numbers.</param>
/// <param name="EstimatedMinutesToday">Summed over today's estimated matters.</param>
/// <param name="BusiestDay">Heaviest day in the week ahead, or null.</param>
/// <param name="Duplicates">Same-title bins in the pool.</param>
/// <param name="PoolIds">Ids of the matters a theme is allowed to name.</param>
public sealed record ComputedDigest(
    DailyDigestCountsDocument Counts,
    DailyDigestEstimateDocument EstimatedMinutesToday,
    DailyDigestBusiestDayDocument? BusiestDay,
    List<DailyDigestDuplicateDocument> Duplicates,
    HashSet<string> PoolIds);

/// <summary>
/// Port of <c>computeDigest</c> from <c>server/src/modules/tasks/dailyDigest.ts</c>.
///
/// <para>
/// <b>EVERY number here is computed in code from real documents.</b> That rule is
/// the whole design of the digest, not a stylistic preference: a figure that came
/// out of a language model is a surface for confident wrong answers the user cannot
/// check, and this one is on screen the moment they open the app. The model — when
/// there is one — names themes and writes one sentence, and touches nothing else.
/// </para>
/// </summary>
public sealed class DailyDigestComputer
{
    /// <summary>
    /// The model never sees more than this many matters. Past it the themes stop
    /// being about anything. Also the ceiling on the duplicate pool.
    /// </summary>
    private const int MaxTasksToModel = 120;

    /// <summary>
    /// Cap on the rows read to sum today's estimates. A personal backlog does not
    /// put hundreds of matters on one day; if it somehow does, the estimate
    /// undercounts rather than the query going unbounded. <c>dueToday</c> is counted
    /// separately and stays exact either way.
    /// </summary>
    private const int MaxTodayRows = 200;

    /// <summary>
    /// Overdue by more than a fortnight, or moved three times: the same slipping
    /// rule <c>/me/tasks/counts</c> uses, so the digest and the Matters header
    /// cannot disagree.
    /// </summary>
    private const int SlipDays = 14;

    private const int SlipMoves = 3;

    private readonly IMongoDatabase _database;

    public DailyDigestComputer(IMongoDatabase database)
    {
        _database = database;
    }

    public async Task<ComputedDigest> ComputeAsync(
        ObjectId userId,
        DateTime now,
        string? timezone,
        DailyDigestSourceState source,
        CancellationToken cancellationToken = default)
    {
        // The day-boundary authority. `timezone` has already been through
        // DigestClock.SafeTimezone, so this cannot throw here even though it does
        // everywhere else.
        var day = TaskQuery.GetDayBoundaries(now, timezone);

        var tasks = _database.GetCollection<BsonDocument>(MongoCollections.Tasks);

        var scope = new BsonDocument
        {
            ["userId"] = userId,
            ["deletedAt"] = new BsonDocument("$exists", false),
        };

        // "Live" is open-or-snoozed: every bucket describes work still in front of
        // the user.
        var live = new BsonDocument(scope)
        {
            ["status"] = new BsonDocument("$in", new BsonArray { "open", "snoozed" }),
        };

        var facetPipeline = new[]
        {
            new BsonDocument("$match", scope),
            new BsonDocument("$facet", new BsonDocument
            {
                ["dueToday"] = Counted(With(live, "dueAt", Range(day.TodayStart, day.TomorrowStart))),

                ["completedToday"] = Counted(new BsonDocument
                {
                    ["userId"] = userId,
                    ["status"] = "done",
                    ["completedAt"] = Range(day.TodayStart, day.TomorrowStart),
                }),

                // Every OPEN matter ever, not a today-scoped number, and NOT
                // including snoozed ones. Trashed matters are excluded because the
                // facet inherits the outer $match.
                ["openTotal"] = Counted(new BsonDocument
                {
                    ["userId"] = userId,
                    ["status"] = "open",
                }),

                ["slipping"] = Counted(With(live, "$or", new BsonArray
                {
                    new BsonDocument("rescheduleCount", new BsonDocument("$gte", SlipMoves)),
                    new BsonDocument("dueAt", new BsonDocument("$lt", day.TodayStart.AddDays(-SlipDays))),
                })),

                // Heaviest day in the week ahead — the input to "want me to spread
                // these out?". Grouped by MONGO in the caller's zone so it agrees
                // with the local dates the rest of the digest is built on. Ties break
                // on the earlier date so the answer is stable between rebuilds.
                ["busiestDay"] = new BsonArray
                {
                    new BsonDocument("$match", With(live, "dueAt", Range(day.TodayStart, day.WeekEnd))),
                    new BsonDocument("$group", new BsonDocument
                    {
                        ["_id"] = new BsonDocument("$dateToString", new BsonDocument
                        {
                            ["date"] = "$dueAt",
                            ["format"] = "%Y-%m-%d",

                            // Explicitly UTC when absent. Note this does NOT match
                            // what localDate does with an absent zone — see
                            // DigestClock.LocalDateKey.
                            ["timezone"] = timezone ?? "UTC",
                        }),
                        ["n"] = new BsonDocument("$sum", 1),
                    }),
                    new BsonDocument("$sort", new BsonDocument { ["n"] = -1, ["_id"] = 1 }),
                    new BsonDocument("$limit", 1),
                },
            }),
        };

        var facetTask = tasks
            .Aggregate<BsonDocument>(facetPipeline, cancellationToken: cancellationToken)
            .FirstOrDefaultAsync(cancellationToken);

        var todayRowsTask = tasks
            .Find(With(live, "dueAt", Range(day.TodayStart, day.TomorrowStart)))
            .Limit(MaxTodayRows)
            .ToListAsync(cancellationToken);

        // Everything live with a deadline inside the horizon, OVERDUE INCLUDED. The
        // themes, the duplicate check and the model's view all come from this one
        // set, so they cannot describe different days. An undated matter is absent:
        // `$lt` does not match a missing field.
        var poolRowsTask = tasks
            .Find(With(live, "dueAt", new BsonDocument("$lt", day.WeekEnd)))
            .Sort(new BsonDocument("dueAt", 1))
            .Limit(MaxTasksToModel)
            .ToListAsync(cancellationToken);

        await Task.WhenAll(facetTask, todayRowsTask, poolRowsTask).ConfigureAwait(false);

        var facet = await facetTask.ConfigureAwait(false);
        var todayRows = await todayRowsTask.ConfigureAwait(false);
        var poolRows = await poolRowsTask.ConfigureAwait(false);

        var estimate = new DailyDigestEstimateDocument();
        foreach (var row in todayRows)
        {
            var (min, max) = ReadEstimate(row);
            estimate.Min += min;
            estimate.Max += max;
        }

        var pool = poolRows
            .Select(row => new DuplicateCandidate(
                row.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty,
                row.TryGetValue("title", out var title) && title.IsString ? title.AsString : string.Empty))
            .ToList();

        var busiest = FirstOf(facet, "busiestDay");

        return new ComputedDigest(
            new DailyDigestCountsDocument
            {
                DueToday = Scalar(facet, "dueToday"),
                CompletedToday = Scalar(facet, "completedToday"),
                OpenTotal = Scalar(facet, "openTotal"),
                Slipping = Scalar(facet, "slipping"),
                NeedsInput = source.NeedsInput,
                ScansAwaitingReview = source.ScansAwaitingReview,
            },
            estimate,
            busiest is null
                ? null
                : new DailyDigestBusiestDayDocument
                {
                    Date = busiest.GetValue("_id", string.Empty).AsString,
                    Count = busiest.GetValue("n", 0).ToInt32(),
                },
            DigestDuplicates.Find(pool),
            pool.Select(p => p.Id).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Port of <c>readEstimate</c>. <c>estimate</c> is optional on every matter and
    /// absent from every matter created before it existed, so it is VALIDATED on the
    /// way in rather than assumed — the rows are read raw (Node's <c>.lean()</c>),
    /// which returns what Mongo stored rather than what the schema promises.
    ///
    /// <para>
    /// A matter with no usable estimate contributes ZERO, never a guess: a fabricated
    /// number in a digest whose whole premise is that it has none is worse than a
    /// low total.
    /// </para>
    /// </summary>
    public static (double Min, double Max) ReadEstimate(BsonDocument row)
    {
        if (!row.TryGetValue("estimate", out var raw) || !raw.IsBsonDocument)
        {
            return (0, 0);
        }

        var estimate = raw.AsBsonDocument;
        if (!TryReadNonNegative(estimate, "minMinutes", out var min) ||
            !TryReadNonNegative(estimate, "maxMinutes", out var max))
        {
            return (0, 0);
        }

        // Defend the invariant rather than trusting it — a max below min would render
        // as a backwards range.
        return (min, Math.Max(min, max));
    }

    private static bool TryReadNonNegative(BsonDocument document, string field, out double value)
    {
        value = 0;

        if (!document.TryGetValue(field, out var raw) || !raw.IsNumeric)
        {
            return false;
        }

        value = raw.ToDouble();
        return value >= 0;
    }

    private static BsonArray Counted(BsonDocument match) =>
        new() { new BsonDocument("$match", match), new BsonDocument("$count", "n") };

    private static BsonDocument Range(DateTime fromInclusive, DateTime toExclusive) =>
        new() { ["$gte"] = fromInclusive, ["$lt"] = toExclusive };

    /// <summary>A copy of <paramref name="baseMatch"/> with one extra clause.</summary>
    private static BsonDocument With(BsonDocument baseMatch, string field, BsonValue clause) =>
        new BsonDocument(baseMatch) { [field] = clause };

    private static BsonDocument? FirstOf(BsonDocument? facet, string key)
    {
        if (facet is null || !facet.TryGetValue(key, out var raw) || raw.AsBsonArray.Count == 0)
        {
            return null;
        }

        return raw.AsBsonArray[0].AsBsonDocument;
    }

    private static int Scalar(BsonDocument? facet, string key) =>
        FirstOf(facet, key)?.GetValue("n", 0).ToInt32() ?? 0;
}
