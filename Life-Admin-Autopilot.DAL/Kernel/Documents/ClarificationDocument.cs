using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

public static class ClarificationVocabulary
{
    public static readonly IReadOnlyList<string> Statuses = new[] { "open", "answered", "skipped", "dropped", "settled" };
    public static readonly IReadOnlyList<string> Kinds = new[] { "date", "amount", "choice", "confirm" };
    public static readonly IReadOnlyList<string> Costs = new[] { "high", "low" };
}

public sealed class ClarificationOptionDocument
{
    public string Label { get; set; } = string.Empty;

    public DateTime? DueAt { get; set; }

    public string? Title { get; set; }

    public string? Notes { get; set; }
}

public sealed class ClarificationDraftDocument
{
    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Priority { get; set; } = "normal";

    public string? Notes { get; set; }

    public List<string> Tags { get; set; } = new();

    public DateTime? DueAt { get; set; }
}

/// <summary>
/// Port of <c>server/src/models/Clarification.ts</c>.
///
/// Reads must compose <c>MongoRepositoryBase&lt;T&gt;.VisibleOpen(now)</c>, never a
/// bare <c>status == "open"</c> — see the note there.
/// </summary>
public sealed class ClarificationDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    public ObjectId TaskId { get; set; }

    public string Status { get; set; } = "open";

    public ClarificationDraftDocument Draft { get; set; } = new();

    public string Question { get; set; } = string.Empty;

    public string Kind { get; set; } = "date";

    public string CostOfWrong { get; set; } = "high";

    public List<ClarificationOptionDocument> Options { get; set; } = new();

    public string? SourceText { get; set; }

    /// <summary>Internal idempotency key. NEVER sent to a client — the mapper drops it.</summary>
    public string? SourceKey { get; set; }

    /// <summary>Cooling-off window after a Skip. Part of the <c>VisibleOpen</c> predicate.</summary>
    public DateTime? DeferredUntil { get; set; }

    public string? Answer { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
