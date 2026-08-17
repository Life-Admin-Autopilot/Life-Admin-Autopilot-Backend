using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Telemetry;

/// <summary>
/// Collections this slice owns. Declared here rather than on
/// <c>MongoCollections</c> because that class is explicitly closed to slice
/// additions — see its own summary.
/// </summary>
public static class TelemetryCollections
{
    /// <summary>One document per model call. TTL'd; never read by a dashboard.</summary>
    public const string AiUsageEvents = "aiusageevents";

    /// <summary>Per-user-per-day aggregates. Kept forever; this is what the console reads.</summary>
    public const string AiUsageRollups = "aiusagerollups";
}

/// <summary>
/// Which product surface spent the money. The console's "cost by feature" chart is
/// a group-by on this field, so the vocabulary is closed and the strings are stable
/// — renaming one silently splits a series in two across the rename date.
/// </summary>
public static class AiUsageFeature
{
    /// <summary>A turn of <c>POST /ai/ask</c> or its post-confirmation continuation.</summary>
    public const string Chat = "chat";

    /// <summary>Gemini vision extraction behind a document scan.</summary>
    public const string DocumentScan = "document_scan";

    /// <summary>Speech-to-text.</summary>
    public const string Transcription = "transcription";

    public static readonly IReadOnlyList<string> All = new[] { Chat, DocumentScan, Transcription };
}

/// <summary>How the call ended. Errors still cost money, so they are still recorded.</summary>
public static class AiUsageOutcome
{
    public const string Ok = "ok";

    /// <summary>The provider answered with an error, or the stream died mid-turn.</summary>
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All = new[] { Ok, Error };
}

/// <summary>
/// One model call, priced at the moment it happened.
///
/// <para>
/// <b><see cref="EstimatedCostUsd"/> is computed on write, never on read.</b> Model
/// prices change; a historical figure recomputed against today's price table would
/// silently rewrite last quarter's cost. The stored number is what we believed the
/// call cost when it was made, and that is the number that has to stay put.
/// </para>
///
/// <para>
/// <b><see cref="Day"/> and <see cref="Month"/> are denormalised on purpose.</b>
/// The rollup groups by them, and a <c>$dateToString</c> over a timestamp cannot use
/// an index. They are UTC bucket keys in the same format the quota primitive already
/// uses (<c>UsageQuotaBuckets.UtcDate</c> / <c>UtcMonth</c>), so a usage row and a
/// quota row for the same call always agree about which day they landed in.
/// </para>
/// </summary>
public sealed class AiUsageEventDocument
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    public DateTime At { get; set; }

    /// <summary><c>YYYY-MM-DD</c>, UTC.</summary>
    public string Day { get; set; } = string.Empty;

    /// <summary><c>YYYY-MM</c>, UTC.</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiUsageFeature"/>.</summary>
    public string Feature { get; set; } = string.Empty;

    /// <summary><c>langflow</c> | <c>gemini</c> | the ASR vendor. Who we paid.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The model id as the provider named it. Nullable because Langflow does not
    /// report which model its Agent node called — see <c>LangflowUsage</c>.
    /// </summary>
    public string? Model { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int TotalTokens { get; set; }

    /// <summary>
    /// USD, as Decimal128 so the arithmetic is exact. A per-call figure is often
    /// below a hundredth of a cent, which is precisely where a double starts lying.
    /// </summary>
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>
    /// False when no price was known for <see cref="Model"/> and the cost is a
    /// fallback rather than a real quote. The console must label these — an
    /// unpriced call silently costing $0 is how a cost dashboard starts lying.
    /// </summary>
    public bool Priced { get; set; }

    public long LatencyMs { get; set; }

    /// <summary>One of <see cref="AiUsageOutcome"/>.</summary>
    public string Outcome { get; set; } = AiUsageOutcome.Ok;

    /// <summary>Set when <see cref="Outcome"/> is an error. Groups the reliability view.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The conversation or scan this belongs to, for tracing one expensive turn back
    /// to its cause. Never surfaced in aggregate views.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// When Mongo may drop the row. The TTL index watches this field rather than
    /// <see cref="At"/> so the retention window can be changed for one write without
    /// a migration.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// A day of one user's spending, folded flat. The console reads only these — the
/// raw event collection is for tracing, not for dashboards, and a chart that scans
/// it gets slower every week until it times out.
/// </summary>
public sealed class AiUsageRollupDocument
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    /// <summary><c>YYYY-MM-DD</c>, UTC. Unique with <see cref="UserId"/> and <see cref="Feature"/>.</summary>
    public string Day { get; set; } = string.Empty;

    public string Month { get; set; } = string.Empty;

    public string Feature { get; set; } = string.Empty;

    public int Calls { get; set; }

    public int Errors { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long TotalTokens { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>Calls inside this bucket that had no known price. Carried up so the console can caveat a total.</summary>
    public int UnpricedCalls { get; set; }

    public long TotalLatencyMs { get; set; }

    /// <summary>When the rollup job last rewrote this row.</summary>
    public DateTime ComputedAt { get; set; }
}
