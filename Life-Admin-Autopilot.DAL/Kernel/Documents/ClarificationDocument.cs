using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

/// <summary>
/// The closed vocabularies a Clarification is allowed to carry.
///
/// <para>
/// <b><see cref="Statuses"/> was fiction until it was reconciled.</b> It declared
/// <c>answered</c>, <c>skipped</c> and <c>settled</c> — none of which any writer in
/// the codebase has ever produced — and omitted <c>resolved</c>, which is what
/// <c>POST /me/clarifications/{id}/resolve</c> actually stores. Nothing referenced
/// the list, so nothing caught it: a vocabulary that is never read cannot be wrong
/// out loud. Anyone writing a filter, a fixture or a migration off it got three
/// statuses that never occur and missed the one that occurs most.
/// </para>
///
/// <para>
/// So the list is now the truth, and the three constants below are referenced at
/// every site that assigns or filters a status. That is the part that keeps it
/// true — a member no call site names drifts again the moment the routes move.
/// </para>
///
/// <para>
/// <b>Skip is deliberately absent, and that is not an omission.</b> Deferring a
/// question leaves the row <c>open</c> and moves <c>deferredUntil</c> instead; it
/// drops out of <c>VisibleOpen()</c> for the cooling-off window and comes back.
/// A skipped question is still a question, and still occupies a slot against the
/// open-queue cap.
/// </para>
/// </summary>
public static class ClarificationVocabulary
{
    /// <summary>Raised and awaiting an answer. The only status a write route acts on.</summary>
    public const string Open = "open";

    /// <summary>The user answered. Written by <c>POST /me/clarifications/{id}/resolve</c>.</summary>
    public const string Resolved = "resolved";

    /// <summary>
    /// Closed without an answer — discarded by the user, orphaned by the deletion of
    /// its task, or timed out by the reminder tick's stale settler.
    /// </summary>
    public const string Dropped = "dropped";

    public static readonly IReadOnlyList<string> Statuses = new[] { Open, Resolved, Dropped };

    public static readonly IReadOnlyList<string> Kinds = new[] { "date", "amount", "choice", "confirm" };

    /// <summary>
    /// How expensive a wrong guess is. <c>high</c> is the default everywhere: if the
    /// model thought the item was worth asking about, the guess must not be acted on.
    /// </summary>
    public const string CostHigh = "high";

    public const string CostLow = "low";

    public static readonly IReadOnlyList<string> Costs = new[] { CostHigh, CostLow };
}

public sealed class ClarificationOptionDocument
{
    public string Label { get; set; } = string.Empty;

    public DateTime? DueAt { get; set; }

    public string? Title { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// i18n key for <see cref="Label"/> when the SERVER composed it. Always null on
    /// the chat lane, where the model writes chips in the user's own language, and
    /// on every row written before this existed.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? LabelKey { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, string>? LabelParams { get; set; }
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

    /// <summary>
    /// Mongoose stamps <c>__v: 0</c> on insert for every model except User, which
    /// is the only one setting <c>versionKey: false</c>
    /// (<c>server/src/models/User.ts:217</c>). The .NET driver adds nothing, so a
    /// document written here was missing a field the reference stores. Observable
    /// today through <c>GET /me/export</c>, which returns raw stored rows.
    /// </summary>
    [BsonElement("__v")]
    public int SchemaVersion { get; set; }

    public ObjectId UserId { get; set; }

    /// <summary>
    /// Nullable despite the Mongoose schema marking it required. Legacy rows
    /// predate that constraint and have no <c>taskId</c> at all, and the reference
    /// simply OMITS the key for them rather than emitting a zero id. A
    /// non-nullable <see cref="ObjectId"/> deserialises those to
    /// <c>000000000000000000000000</c> and the response then diverges on every
    /// surface that returns such a row.
    /// </summary>
    public ObjectId? TaskId { get; set; }

    public string Status { get; set; } = ClarificationVocabulary.Open;

    public ClarificationDraftDocument Draft { get; set; } = new();

    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// i18n key for <see cref="Question"/> when the SERVER composed it — the voice
    /// lane, where the AI never asks and the sentence was therefore written in
    /// English regardless of what the user speaks. Null on the chat lane, whose
    /// questions come from the model already in the user's language.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? QuestionKey { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, string>? QuestionParams { get; set; }

    public string Kind { get; set; } = "date";

    public string CostOfWrong { get; set; } = ClarificationVocabulary.CostHigh;

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
