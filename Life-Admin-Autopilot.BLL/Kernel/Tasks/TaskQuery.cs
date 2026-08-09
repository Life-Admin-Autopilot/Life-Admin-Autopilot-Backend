using System.Text;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Kernel.Tasks;

/// <summary>
/// Port of <c>server/src/modules/tasks/taskQuery.ts</c> — the ONE place a Matters
/// query is defined, and the timezone/day-boundary authority for the whole
/// server.
///
/// <para>
/// Seven modules share this: the REST list, the chat agent's <c>queryTasks</c>
/// tool, the NL search compiler, bulk targeting, counts, the digest and the
/// reminder worker. They MUST stay in lockstep — if the UI can express a filter
/// the agent cannot, "show me what you just showed me" stops working. Adding a
/// filter here adds it everywhere at once; adding it in a slice does not.
/// </para>
/// </summary>
public static partial class TaskQuery
{
    /// <summary>
    /// Sentinels that push date-less tasks to the END of a due-date sort in BOTH
    /// directions. Mongo orders missing fields first ascending, which would bury a
    /// user's dated work under an undated backlog.
    /// </summary>
    public static readonly DateTime FarFuture = new(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    public static readonly DateTime FarPast = new(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly IReadOnlyList<string> Sorts = new[]
    {
        "due-asc", "due-desc", "created-desc", "created-asc", "priority-desc", "title-asc",
    };

    public const string DefaultSort = "due-asc";

    // ---- Filter -----------------------------------------------------------

    /// <summary>
    /// The validated filter, one property per query parameter. A slice binds its
    /// query string into this and hands it straight to
    /// <see cref="BuildFilter"/> — it never reassembles the Mongo clauses.
    /// </summary>
    public sealed class TaskFilter
    {
        /// <summary>Free-text over title + notes. Regex-escaped before use.</summary>
        public string? Q { get; init; }

        public IReadOnlyList<string>? Status { get; init; }

        public IReadOnlyList<string>? Domain { get; init; }

        public IReadOnlyList<string>? Priority { get; init; }

        public IReadOnlyList<string>? Kind { get; init; }

        /// <summary>Comma-separated raw tags. Multi-tag is OR, not AND.</summary>
        public string? Tag { get; init; }

        public DateTime? DueBefore { get; init; }

        public DateTime? DueAfter { get; init; }

        public DateTime? CreatedBefore { get; init; }

        public DateTime? CreatedAfter { get; init; }

        public DateTime? CompletedBefore { get; init; }

        public DateTime? CompletedAfter { get; init; }

        public bool? Overdue { get; init; }

        public bool? Undated { get; init; }

        public bool? Untagged { get; init; }
    }

    /// <summary>
    /// Escape user input before it reaches a regex. Without this a stray <c>(</c>
    /// or <c>*</c> in the search box is either a 500 or a catastrophic-backtracking
    /// hazard. Character class matches Node's exactly.
    /// </summary>
    public static string EscapeRegex(string input) =>
        System.Text.RegularExpressions.Regex.Replace(input, @"[.*+?^${}()|[\]\\]", "\\$&");

    /// <summary>
    /// Translate a validated filter into a Mongo query, always scoped to one user
    /// and always excluding soft-deleted rows.
    /// </summary>
    public static BsonDocument BuildFilter(ObjectId userId, TaskFilter f, DateTime now)
    {
        var filter = new BsonDocument
        {
            ["userId"] = userId,
            ["deletedAt"] = new BsonDocument("$exists", false),
        };

        if (f.Status is { Count: > 0 })
        {
            filter["status"] = new BsonDocument("$in", new BsonArray(f.Status));
        }

        if (f.Domain is { Count: > 0 })
        {
            filter["domain"] = new BsonDocument("$in", new BsonArray(f.Domain));
        }

        if (f.Priority is { Count: > 0 })
        {
            filter["priority"] = new BsonDocument("$in", new BsonArray(f.Priority));
        }

        if (f.Kind is { Count: > 0 })
        {
            filter["kind"] = new BsonDocument("$in", new BsonArray(f.Kind));
        }

        if (!string.IsNullOrEmpty(f.Tag))
        {
            var tags = f.Tag
                .Split(',')
                .Select(TaskVocabulary.NormalizeTag)
                .Where(t => t is not null)
                .Select(t => t!)
                .ToList();

            if (tags.Count > 0)
            {
                filter["tags"] = new BsonDocument("$in", new BsonArray(tags));
            }
        }

        // Deliberately AFTER the tag clause: `untagged` overwrites it, matching
        // Node's assignment order.
        if (f.Untagged == true)
        {
            filter["tags"] = new BsonDocument("$size", 0);
        }

        if (!string.IsNullOrEmpty(f.Q))
        {
            var rx = new BsonRegularExpression(EscapeRegex(f.Q), "i");
            filter["$or"] = new BsonArray
            {
                new BsonDocument("title", rx),
                new BsonDocument("notes", rx),
            };
        }

        // `overdue` and `undated` are mutually exclusive views of dueAt, and either
        // may combine with an explicit range — resolve them into one clause.
        var dueAt = new BsonDocument();
        if (f.DueBefore.HasValue)
        {
            dueAt["$lte"] = f.DueBefore.Value;
        }

        if (f.DueAfter.HasValue)
        {
            dueAt["$gte"] = f.DueAfter.Value;
        }

        if (f.Overdue == true)
        {
            var lte = dueAt.TryGetValue("$lte", out var raw) ? raw.ToUniversalTime() : (DateTime?)null;
            dueAt["$lt"] = lte.HasValue && lte.Value < now ? lte.Value : now;
            dueAt.Remove("$lte");

            // Something already done isn't overdue, however stale its date.
            if (f.Status is null or { Count: 0 })
            {
                filter["status"] = new BsonDocument("$in", new BsonArray(new[] { "open", "snoozed" }));
            }
        }

        if (f.Undated == true)
        {
            filter["dueAt"] = new BsonDocument("$exists", false);
        }
        else if (dueAt.ElementCount > 0)
        {
            filter["dueAt"] = dueAt;
        }

        var createdAt = new BsonDocument();
        if (f.CreatedBefore.HasValue)
        {
            createdAt["$lte"] = f.CreatedBefore.Value;
        }

        if (f.CreatedAfter.HasValue)
        {
            createdAt["$gte"] = f.CreatedAfter.Value;
        }

        if (createdAt.ElementCount > 0)
        {
            filter["createdAt"] = createdAt;
        }

        var completedAt = new BsonDocument();
        if (f.CompletedBefore.HasValue)
        {
            completedAt["$lte"] = f.CompletedBefore.Value;
        }

        if (f.CompletedAfter.HasValue)
        {
            completedAt["$gte"] = f.CompletedAfter.Value;
        }

        if (completedAt.ElementCount > 0)
        {
            filter["completedAt"] = completedAt;
        }

        return filter;
    }

    // ---- Sort + cursor ----------------------------------------------------

    public static BsonDocument SortStage(string sort) => sort switch
    {
        "due-desc" => new BsonDocument { ["_dueSort"] = -1, ["createdAt"] = -1 },
        "created-desc" => new BsonDocument { ["createdAt"] = -1 },
        "created-asc" => new BsonDocument { ["createdAt"] = 1 },
        "priority-desc" => new BsonDocument { ["_priSort"] = -1, ["_dueSort"] = 1 },
        "title-asc" => new BsonDocument { ["title"] = 1 },
        _ => new BsonDocument { ["_dueSort"] = 1, ["createdAt"] = -1 },
    };

    /// <summary>
    /// The opaque page token. It encodes an OFFSET — honest at this scale (a
    /// personal backlog, not a feed) and correct under every sort above, which a
    /// keyset cursor would not be without a unique tiebreaker per sort. Opaque so
    /// it can become keyset later without touching the client.
    ///
    /// base64url, matching Node's <c>Buffer.toString('base64url')</c>: no padding,
    /// <c>-</c> and <c>_</c> instead of <c>+</c> and <c>/</c>.
    /// </summary>
    public static string EncodeCursor(int offset)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));
        return raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Lenient by design, exactly like Node: anything unparseable or negative
    /// decodes to 0 rather than erroring. A stale token must not 500.
    /// </summary>
    public static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return int.TryParse(raw, out var n) && n >= 0 ? n : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    // ---- Listing ----------------------------------------------------------

    public sealed record ListTasksResult(IReadOnlyList<TaskDocument> Tasks, int Total, string? NextCursor);

    /// <summary>
    /// One round trip for both the page and the total via <c>$facet</c>, so the
    /// header count can never disagree with the rows beneath it.
    /// </summary>
    public static async Task<ListTasksResult> ListAsync(
        IMongoCollection<TaskDocument> tasks,
        ObjectId userId,
        TaskFilter filter,
        string? sort = null,
        int limit = 50,
        string? cursor = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var at = now ?? DateTime.UtcNow;
        var effectiveSort = sort ?? DefaultSort;
        var offset = DecodeCursor(cursor);
        var match = BuildFilter(userId, filter, at);

        var priBranches = new BsonArray(
            TaskVocabulary.PriorityRank.Select(kv => new BsonDocument
            {
                ["case"] = new BsonDocument("$eq", new BsonArray { "$priority", kv.Key }),
                ["then"] = kv.Value,
            }));

        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$addFields", new BsonDocument
            {
                ["_dueSort"] = new BsonDocument(
                    "$ifNull",
                    new BsonArray { "$dueAt", effectiveSort == "due-desc" ? FarPast : FarFuture }),
                ["_priSort"] = new BsonDocument("$switch", new BsonDocument
                {
                    ["branches"] = priBranches,
                    ["default"] = TaskVocabulary.PriorityRank["normal"],
                }),
            }),
            new BsonDocument("$facet", new BsonDocument
            {
                ["rows"] = new BsonArray
                {
                    new BsonDocument("$sort", SortStage(effectiveSort)),
                    new BsonDocument("$skip", offset),
                    new BsonDocument("$limit", limit),
                    new BsonDocument("$unset", new BsonArray { "_dueSort", "_priSort" }),
                },
                ["total"] = new BsonArray { new BsonDocument("$count", "n") },
            }),
        };

        var options = new AggregateOptions
        {
            // Case-insensitive title sort; harmless for the other sorts.
            Collation = new Collation("en", strength: CollationStrength.Secondary),
        };

        var facet = await tasks
            .Aggregate<BsonDocument>(pipeline, options, cancellationToken)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = facet is not null && facet.TryGetValue("rows", out var rawRows)
            ? rawRows.AsBsonArray
                .Select(v => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<TaskDocument>(v.AsBsonDocument))
                .ToList()
            : new List<TaskDocument>();

        var total = 0;
        if (facet is not null && facet.TryGetValue("total", out var rawTotal) && rawTotal.AsBsonArray.Count > 0)
        {
            total = rawTotal.AsBsonArray[0].AsBsonDocument.GetValue("n", 0).ToInt32();
        }

        var consumed = offset + rows.Count;
        return new ListTasksResult(rows, total, consumed < total ? EncodeCursor(consumed) : null);
    }
}
