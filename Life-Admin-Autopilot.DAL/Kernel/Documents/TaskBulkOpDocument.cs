using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

public static class BulkOpVocabulary
{
    public static readonly IReadOnlyList<string> Kinds = new[] { "bulk", "categorize" };
    public static readonly IReadOnlyList<string> Statuses = new[] { "proposed", "applied", "undone", "discarded" };
    public static readonly IReadOnlyList<string> Actions = new[]
    {
        "delete", "complete", "snooze", "setDomain", "addTags", "categorize",
    };

    /// <summary>
    /// How long an op stays undoable. The toast is a 5-second affordance; this
    /// record is the real safety net and must outlive the session.
    /// </summary>
    public const int TtlDays = 30;

    public static DateTime Expiry(DateTime from) => from.AddDays(TtlDays);
}

/// <summary>
/// Per-task before/after. Both are SPARSE patches holding only the fields the op
/// touched.
///
/// <para>
/// <b>The null convention is load-bearing.</b> Inside <see cref="Prior"/> an
/// explicit BSON null means "this field did not exist before" and must be
/// <c>$unset</c> on undo; a MISSING key means "not touched". Without the
/// distinction, undoing a snooze on a task that never had a <c>snoozedUntil</c>
/// leaves the field stranded at its post-op value. <c>BulkService.ToMongoOps</c>
/// is the only code allowed to translate it.
/// </para>
/// </summary>
public sealed class BulkOpEntryDocument
{
    public ObjectId TaskId { get; set; }

    public BsonDocument Prior { get; set; } = new();

    public BsonDocument Next { get; set; } = new();

    /// <summary>Populated for <c>categorize</c> entries only.</summary>
    public string? Confidence { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Port of <c>server/src/models/TaskBulkOp.ts</c> — one record per multi-task
/// mutation, and the unit of undo.
/// </summary>
public sealed class TaskBulkOpDocument
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

    public string Kind { get; set; } = "bulk";

    public string Action { get; set; } = string.Empty;

    public string Status { get; set; } = "applied";

    public List<BulkOpEntryDocument> Entries { get; set; } = new();

    /// <summary>Natural-language range/query that produced the op, for the run history.</summary>
    public string? Label { get; set; }

    public DateTime? AppliedAt { get; set; }

    public DateTime? UndoneAt { get; set; }

    /// <summary>TTL-indexed in Mongo: the document is reaped once this passes.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
