using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.Digest;

/// <summary>
/// Port of <c>server/src/models/DailyDigest.ts</c>.
///
/// <para>
/// One row per <c>(userId, localDate)</c>. The dashboard loads the digest on EVERY
/// visit, so recomputing it per request — which in the Node original means a Gemini
/// call per request — is not an option. The row holds the whole rendered payload
/// plus a <c>sourceHash</c> fingerprint of the user's matter state; a request whose
/// hash still matches is served from here without recomputing anything.
/// </para>
///
/// <para>
/// The payload is stored WHOLE rather than recomputed from parts, because the prose
/// half (headline, theme labels) cannot be recomputed cheaply — that is the entire
/// reason this collection exists.
/// </para>
/// </summary>
public sealed class DailyDigestDocument
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

    /// <summary>Local calendar date this digest describes, <c>YYYY-MM-DD</c>.</summary>
    public string LocalDate { get; set; } = string.Empty;

    /// <summary>Fingerprint of the matter state the payload was computed from.</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>
    /// The language the prose in <see cref="Payload"/> is written in. Part of the
    /// ROW rather than only the hash because themes are deliberately carried forward
    /// between builds — and a carried Arabic label under a freshly English headline
    /// is the one stale state that looks like a bug rather than like wording.
    /// </summary>
    public string? Locale { get; set; }

    /// <summary>
    /// The instant the digest was COMPUTED. TTL-indexed at 7 days — a digest is
    /// worthless the day after it describes.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    public DailyDigestPayloadDocument Payload { get; set; } = new();

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// The stored (and returned) digest body. Mirrors <c>DailyDigestPayload</c> in the
/// Node model field for field.
/// </summary>
public sealed class DailyDigestPayloadDocument
{
    public string LocalDate { get; set; } = string.Empty;

    /// <summary>
    /// A STRING, not a date — Node stores <c>now.toISOString()</c> here. Keeping it
    /// a string is what makes a cache hit byte-identical to the write that produced
    /// it, with no re-formatting in between.
    /// </summary>
    public string GeneratedAt { get; set; } = string.Empty;

    /// <summary>One sentence. The only free prose in the payload.</summary>
    public string Headline { get; set; } = string.Empty;

    public DailyDigestCountsDocument Counts { get; set; } = new();

    public DailyDigestEstimateDocument EstimatedMinutesToday { get; set; } = new();

    public List<DailyDigestThemeDocument> Themes { get; set; } = new();

    /// <summary>
    /// Stored as an explicit <c>null</c> rather than omitted, which is what Mongoose
    /// does for a subdocument path with <c>default: null</c>. The kernel's
    /// <c>IgnoreIfNullConvention</c> would otherwise drop the key, leaving a raw row
    /// that does not compare equal to the reference server's.
    /// </summary>
    [BsonIgnoreIfNull(false)]
    public DailyDigestBusiestDayDocument? BusiestDay { get; set; }

    public List<DailyDigestDuplicateDocument> Duplicates { get; set; } = new();
}

public sealed class DailyDigestCountsDocument
{
    public int DueToday { get; set; }

    public int CompletedToday { get; set; }

    public int OpenTotal { get; set; }

    public int Slipping { get; set; }

    /// <summary>Open clarifications awaiting an answer.</summary>
    public int NeedsInput { get; set; }

    /// <summary>ScannedDocument rows sitting in <c>ready_for_review</c>.</summary>
    public int ScansAwaitingReview { get; set; }
}

public sealed class DailyDigestEstimateDocument
{
    public double Min { get; set; }

    public double Max { get; set; }
}

public sealed class DailyDigestThemeDocument
{
    public string Label { get; set; } = string.Empty;

    public int Count { get; set; }

    public List<string> TaskIds { get; set; } = new();
}

public sealed class DailyDigestBusiestDayDocument
{
    public string Date { get; set; } = string.Empty;

    public int Count { get; set; }
}

public sealed class DailyDigestDuplicateDocument
{
    public string Title { get; set; } = string.Empty;

    public int Count { get; set; }

    public List<string> TaskIds { get; set; } = new();
}
