using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.GoogleIntegration;

/// <summary>
/// The closed enum lists from <c>server/src/models/Integration.ts</c>.
/// </summary>
public static class IntegrationVocabulary
{
    public const string Google = "google";

    public const string StatusActive = "active";
    public const string StatusNeedsReauth = "needs_reauth";
    public const string StatusRevoked = "revoked";

    public static readonly IReadOnlyList<string> Providers = new[] { Google };

    public static readonly IReadOnlyList<string> Statuses = new[]
    {
        StatusActive, StatusNeedsReauth, StatusRevoked,
    };

    /// <summary>Mongoose default for <c>importDomain</c>.</summary>
    public const string DefaultImportDomain = "home";
}

/// <summary>
/// Port of <c>server/src/models/Integration.ts</c> — a connected third-party
/// account, one row per (user, provider).
///
/// <para>
/// <b>The refresh token is the only durable secret here and it is stored
/// ENCRYPTED</b> (<see cref="RefreshTokenEnc"/>). A plaintext column would be worse
/// than a leaked password hash: a Google refresh token is a long-lived bearer
/// credential for someone's calendar and tasks, and unlike a hash there is nothing
/// one-way about it, because we must be able to use it.
/// </para>
///
/// <para>
/// Access tokens are stored too, but only as a CACHE — they expire in about an
/// hour and losing one costs a refresh, nothing more.
/// </para>
///
/// <para>
/// Three fields never reach a client: <see cref="RefreshTokenEnc"/>,
/// <see cref="AccessTokenEnc"/> and <see cref="AccessTokenExpiresAt"/>. The
/// <c>toJSON</c> transform strips all three, and the .NET equivalent is the mapper
/// in <c>BLL/Features/GoogleIntegration</c> — never serialize this type directly.
/// </para>
/// </summary>
public sealed class IntegrationDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    public string Provider { get; set; } = IntegrationVocabulary.Google;

    /// <summary>The provider's own account id (Google <c>sub</c>). Stable across email changes.</summary>
    public string ExternalAccountId { get; set; } = string.Empty;

    /// <summary>Shown so the user can tell which of their accounts is connected.</summary>
    public string? ExternalAccountEmail { get; set; }

    /// <summary>Ciphertext from the token cipher. NEVER a raw token.</summary>
    public string RefreshTokenEnc { get; set; } = string.Empty;

    /// <summary>Ciphertext. A cache — safe to drop, costs one refresh to rebuild.</summary>
    public string? AccessTokenEnc { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>
    /// Scopes Google ACTUALLY granted, which is not always what we asked for — the
    /// consent screen lets a user tick some and not others. Every caller must check
    /// this rather than assuming, or a user who declined Tasks gets a sync that 403s
    /// on every tick with no explanation.
    /// </summary>
    public List<string> GrantedScopes { get; set; } = new();

    public string Status { get; set; } = IntegrationVocabulary.StatusActive;

    /// <summary>
    /// Plain-language reason the connection stopped working. Surfaced to the user: a
    /// silently dead integration looks exactly like one with nothing to sync, and
    /// they would keep trusting reminders that stopped arriving.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Google Calendar incremental-sync cursor. Google invalidates these on its own
    /// schedule and answers the next request with <b>410 Gone</b>, which is not an
    /// error condition but an instruction: wipe the cursor and do a full sync.
    /// </summary>
    public string? CalendarSyncToken { get; set; }

    public DateTime? CalendarSyncedAt { get; set; }

    /// <summary>Google Tasks has no cursor — it is polled with <c>updatedMin</c>.</summary>
    public DateTime? TasksSyncedAt { get; set; }

    /// <summary>
    /// Which life domain imported items are filed under. Neither Google Tasks nor a
    /// bare calendar event carries a life-domain hint, and classifying each one with
    /// a model would put a per-item AI call on the margin — so the user picks once.
    /// </summary>
    public string ImportDomain { get; set; } = IntegrationVocabulary.DefaultImportDomain;

    public DateTime ConnectedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
