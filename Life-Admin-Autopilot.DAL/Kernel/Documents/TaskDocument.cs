using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

/// <summary>Closed vocabularies from <c>server/src/models/Task.ts</c> and <c>User.ts</c>.</summary>
public static class TaskVocabulary
{
    public static readonly IReadOnlyList<string> Statuses = new[] { "open", "done", "snoozed" };
    public static readonly IReadOnlyList<string> Priorities = new[] { "low", "normal", "high", "urgent" };
    public static readonly IReadOnlyList<string> Kinds = new[] { "reminder", "list" };
    public static readonly IReadOnlyList<string> Domains = new[] { "health", "home", "car", "finance", "family", "pets" };
    public static readonly IReadOnlyList<string> ConfidenceBuckets = new[] { "high", "medium", "low" };
    public static readonly IReadOnlyList<string> TimePrecisions = new[] { "exact", "dateOnly", "floating" };
    public static readonly IReadOnlyList<string> ReminderKinds = new[] { "lead", "due", "ai" };

    public static readonly IReadOnlyList<string> ExternalSources = new[]
    {
        "google_calendar", "google_tasks", "apple_calendar", "apple_reminders", "ics_feed", "email_forward",
    };

    /// <summary>
    /// Surfaced to clients as the derived <c>priorityRank</c> so they can sort and
    /// compare without duplicating the table. Order matters — the aggregation
    /// <c>$switch</c> in TaskQuery is generated from it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> PriorityRank = new Dictionary<string, int>
    {
        ["urgent"] = 3,
        ["high"] = 2,
        ["normal"] = 1,
        ["low"] = 0,
    };

    public const int MaxSubtasks = 50;
    public const int MaxSubtaskText = 240;
    public const int MaxTags = 10;
    public const int MaxTagLength = 32;

    /// <summary>
    /// Port of <c>normalizeTag</c>: trim, lowercase, whitespace runs to single
    /// hyphens, strip everything but <c>[a-z0-9-]</c>, collapse hyphen runs, trim
    /// leading/trailing hyphens, cap at <see cref="MaxTagLength"/>. Returns null
    /// when nothing survives.
    /// </summary>
    public static string? NormalizeTag(string raw)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(raw.Trim().ToLowerInvariant(), @"\s+", "-");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "[^a-z0-9-]", string.Empty);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "-+", "-");
        cleaned = cleaned.Trim('-');
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > MaxTagLength ? cleaned[..MaxTagLength] : cleaned;
    }

    public static int RankFor(string? priority) =>
        priority is not null && PriorityRank.TryGetValue(priority, out var rank) ? rank : PriorityRank["normal"];
}

public sealed class SubtaskDocument
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public string Text { get; set; } = string.Empty;

    public bool Done { get; set; }

    public DateTime? CreatedAt { get; set; }
}

public sealed class TaskEstimateDocument
{
    public int MinMinutes { get; set; }

    public int MaxMinutes { get; set; }

    public string Source { get; set; } = string.Empty;
}

public sealed class ReminderEntryDocument
{
    public DateTime At { get; set; }

    /// <summary>The double-send guard. The reminder worker only fires entries where this is null.</summary>
    public DateTime? FiredAt { get; set; }

    public string Kind { get; set; } = "due";
}

public sealed class TaskTranslationDocument
{
    public string? Title { get; set; }

    public string? Notes { get; set; }

    /// <summary>Keyed by the subtask's own <c>_id</c> string — subtask order changes.</summary>
    public Dictionary<string, string>? Subtasks { get; set; }

    public DateTime? At { get; set; }
}

/// <summary>Port of <c>server/src/models/Task.ts</c>.</summary>
public sealed class TaskDocument
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

    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Kind { get; set; } = "list";

    public string Status { get; set; } = "open";

    public string Priority { get; set; } = "normal";

    public List<SubtaskDocument> Subtasks { get; set; } = new();

    public List<string> Tags { get; set; } = new();

    public DateTime? DueAt { get; set; }

    public string? Notes { get; set; }

    /// <summary>The language the canonical text is written in. Never overwritten by a translation.</summary>
    public string? SourceLocale { get; set; }

    /// <summary>Read-only presentation copies, keyed by locale. NEVER searched.</summary>
    public Dictionary<string, TaskTranslationDocument>? I18n { get; set; }

    public ObjectId? SourceVoiceNoteId { get; set; }

    public ObjectId? SourceDocumentId { get; set; }

    public string? SourceTaskKey { get; set; }

    public string? ExternalSource { get; set; }

    public string? ExternalId { get; set; }

    public string? TimePrecision { get; set; }

    public string? Confidence { get; set; }

    public TaskEstimateDocument? Estimate { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? SnoozedUntil { get; set; }

    public List<ReminderEntryDocument> Reminders { get; set; } = new();

    /// <summary>
    /// Soft delete. ALL reads must filter it out — use
    /// <c>MongoRepositoryBase&lt;T&gt;.NotDeleted()</c>, never a hand-written clause.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    public int RescheduleCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
