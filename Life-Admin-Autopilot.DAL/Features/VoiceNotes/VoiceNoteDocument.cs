using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.VoiceNotes;

/// <summary>
/// Closed vocabularies and budgets from <c>server/src/models/VoiceNote.ts</c>.
/// </summary>
public static class VoiceNoteVocabulary
{
    public static readonly IReadOnlyList<string> Statuses = new[]
    {
        "pending", "transcribing", "extracting", "ready", "needs_review", "failed",
    };

    /// <summary>Nothing more the worker will do unprompted.</summary>
    public static readonly IReadOnlyList<string> TerminalStatuses = new[] { "ready", "needs_review", "failed" };

    /// <summary>
    /// Claimable INCLUDES the two in-progress states, not just <c>pending</c>.
    /// Node calls this out as CRITICAL: a crash mid-transcribe would otherwise
    /// strand the note in <c>transcribing</c> forever, which is the exact failure
    /// the Mongo-backed worker exists to avoid.
    /// </summary>
    public static readonly IReadOnlyList<string> ClaimableStatuses = new[] { "pending", "transcribing", "extracting" };

    public static readonly IReadOnlyList<string> Sources = new[] { "app", "widget", "dynamic_island", "lock_screen" };

    /// <summary>
    /// Why an item was held (or <c>clear</c> if it was not). Drives the
    /// plain-language "why am I seeing this" line on the review card.
    /// </summary>
    public static readonly IReadOnlyList<string> ReviewReasons = new[]
    {
        // `time_clash` is distinct from `maybe_duplicate` on purpose: one says "you
        // may already have this", the other says "you cannot be in two places". They
        // were collapsed onto the duplicate term while the policy could not tell them
        // apart, which put "possible duplicate" on a matter that was neither.
        "clear", "ambiguous_intent", "vague_date", "maybe_duplicate", "time_clash",
        "incomplete", "low_asr", "unknown_domain",
    };

    /// <summary>Ten minutes, the <c>.max()</c> on <c>x-voice-note-duration-ms</c>.</summary>
    public const int MaxDurationMs = 10 * 60 * 1000;

    public const int TimezoneMinLength = 1;

    public const int TimezoneMaxLength = 64;
}

/// <summary>
/// An item that became (or will become) a Task, kept on the note for audit and
/// undo. <c>taskId</c> back-links to the Task that was created.
/// </summary>
public sealed class VoiceExtractedTaskDocument
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Priority { get; set; } = "normal";

    public string Confidence { get; set; } = "high";

    public string ReviewReason { get; set; } = "clear";

    /// <summary>
    /// Carried from extraction rather than re-derived, so the estimate survives the
    /// review hold and lands on the Task — re-deriving it at accept time would cost
    /// a second AI call for an answer already given.
    /// </summary>
    public TaskEstimateDocument? Estimate { get; set; }

    public DateTime? DueAt { get; set; }

    public string? Notes { get; set; }

    public ObjectId? TaskId { get; set; }
}

/// <summary>
/// Held for the user — NOT yet a Task. Lives on the note until the user accepts
/// (→ Task), edits, or discards it.
/// </summary>
public sealed class VoiceReviewItemDocument
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Priority { get; set; } = "normal";

    public string Confidence { get; set; } = "medium";

    public string ReviewReason { get; set; } = "ambiguous_intent";

    public List<string> Reasons { get; set; } = new();

    public TaskEstimateDocument? Estimate { get; set; }

    /// <summary>The raw date phrase heard, before resolution.</summary>
    public string? DueRaw { get; set; }

    public DateTime? DueAt { get; set; }

    public string? Notes { get; set; }
}

/// <summary>One pre-resolved answer the extractor proposed for a clarifiable item.</summary>
public sealed class VoiceClarifyOptionDocument
{
    public string Label { get; set; } = string.Empty;

    public DateTime? DueAt { get; set; }

    /// <summary>
    /// i18n key for <see cref="Label"/>, which is an English fallback. Null on rows
    /// staged before this existed, and on chips whose text is the user's own words.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? LabelKey { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, string>? LabelParams { get; set; }
}

/// <summary>
/// A clarifiable hold — an answerable question (conflicting date,
/// missing-time-that-matters, unnameable). Staged on the note so a worker reclaim
/// re-creates the SAME held Clarification idempotently, keyed by <c>key</c>.
///
/// <para>
/// DISTINCT from <see cref="VoiceReviewItemDocument"/>: this carries a question
/// plus pre-resolved options and surfaces on the home banner → /clarify slider,
/// not the voice-review screen. It is also the ONE array the
/// <c>toJSON</c> transform deletes — see <c>VoiceNoteDto</c>.
/// </para>
/// </summary>
public sealed class VoiceClarifyItemDocument
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Priority { get; set; } = "normal";

    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// i18n key for <see cref="Question"/>, which is an English fallback. The voice
    /// lane composes its own questions server-side, so unlike the chat lane they
    /// are not already in the user's language — see <c>DraftClarification</c>.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? QuestionKey { get; set; }

    [BsonIgnoreIfNull]
    public Dictionary<string, string>? QuestionParams { get; set; }

    public string Kind { get; set; } = "date";

    /// <summary>
    /// Decides whether the task staged alongside this question gets a live reminder
    /// or one withheld until the user confirms the date.
    /// </summary>
    public string CostOfWrong { get; set; } = "high";

    public List<VoiceClarifyOptionDocument> Options { get; set; } = new();

    /// <summary>
    /// The date the draft was read as having, independent of the options.
    ///
    /// <para>
    /// Staging files the task at <c>Options[0].DueAt</c>, which works for the date
    /// questions — their first option IS the reading the item was filed under. A
    /// question with NO options had no such carrier, so an amount question on a
    /// dated matter filed it with no date at all and silently lost the day the user
    /// gave. This is the fallback that stops it.
    /// </para>
    /// </summary>
    [BsonIgnoreIfNull]
    public DateTime? DueAt { get; set; }

    public string? Notes { get; set; }
}

/// <summary>
/// Port of <c>server/src/models/VoiceNote.ts</c>.
///
/// <para>
/// EIGHT members never reach a client — <c>StorageKey</c>, <c>Attempts</c>,
/// <c>MaxAttempts</c>, <c>LockedUntil</c>, <c>NextRunAt</c>, <c>LastError</c>,
/// <c>NotifiedAt</c> and <c>ClarifyItems</c> are all deleted by the Mongoose
/// <c>toJSON</c> transform. <c>FailureReason</c> SURVIVES it, which is why it
/// exists separately from <c>LastError</c>: one is plain-language product state,
/// the other is job machinery. Go through <c>VoiceNoteMappers.ToDto</c>, never a
/// direct serialization.
/// </para>
///
/// <para>
/// Property order mirrors the order Mongoose FIRST SETS each field — the route's
/// <c>VoiceNote.create({...})</c> literal, then the schema defaults, then the
/// timestamps — because a <c>ReplaceOne</c> rewrites the stored document in class
/// order and <c>GET /me/export</c> returns raw stored rows.
/// </para>
/// </summary>
public sealed class VoiceNoteDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>
    /// Mongoose stamps <c>__v: 0</c> on insert for every model except User, which
    /// is the only one setting <c>versionKey: false</c>. The .NET driver adds
    /// nothing, so a document written here would be missing a field the reference
    /// stores — observable through <c>GET /me/export</c>, which returns raw rows.
    /// </summary>
    [BsonElement("__v")]
    public int SchemaVersion { get; set; }

    public ObjectId UserId { get; set; }

    public string StorageKey { get; set; } = string.Empty;

    public int DurationMs { get; set; }

    /// <summary>
    /// <c>int</c>, not <c>long</c>: the Node BSON serializer writes a JS integer
    /// inside the int32 range as int32, and the ceiling here is 5 MiB. A C#
    /// <c>long</c> would store int64 and give the two servers different BSON types
    /// for the same logical value.
    /// </summary>
    public int ByteSize { get; set; }

    public string Source { get; set; } = "app";

    public string Status { get; set; } = "pending";

    public DateTime ClientCapturedAt { get; set; }

    public string? Timezone { get; set; }

    public string? MimeType { get; set; }

    public string? Transcript { get; set; }

    /// <summary>
    /// Plain-language failure text — and, unlike <see cref="LastError"/>, EXPOSED.
    /// </summary>
    public string? FailureReason { get; set; }

    public List<VoiceExtractedTaskDocument> ExtractedTasks { get; set; } = new();

    public List<VoiceReviewItemDocument> ReviewItems { get; set; } = new();

    /// <summary>Internal staging lane. Stripped by the transform — see the DTO.</summary>
    public List<VoiceClarifyItemDocument> ClarifyItems { get; set; } = new();

    // ---- Job machinery. None of it is ever sent to a client. ----------------

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// Stored as an explicit <c>null</c>, NOT omitted — one of only two fields on
    /// this schema that Mongoose declares <c>default: null</c>, so the reference
    /// always writes the element. The kernel's global
    /// <c>IgnoreIfNullConvention(true)</c> is right for every other optional here
    /// (Mongoose omits an unset <c>undefined</c>), which is exactly why the
    /// exception has to be per-property rather than a convention change.
    /// <c>GET /me/export</c> returns raw stored rows, so the difference is
    /// observable.
    /// </summary>
    [BsonIgnoreIfNull(false)]
    public DateTime? LockedUntil { get; set; }

    public DateTime NextRunAt { get; set; }

    public string? LastError { get; set; }

    public DateTime? ReviewedAt { get; set; }

    /// <summary>The other <c>default: null</c> field. See <see cref="LockedUntil"/>.</summary>
    [BsonIgnoreIfNull(false)]
    public DateTime? NotifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
