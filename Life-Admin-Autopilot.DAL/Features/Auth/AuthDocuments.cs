using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

/// <summary>
/// The five <c>purpose</c> values <c>server/src/models/VerificationToken.ts</c>
/// declares. Stored verbatim, and part of the hash preimage for codes — a typo
/// here silently makes every code of that purpose unredeemable.
/// </summary>
public static class VerificationPurposes
{
    public const string PasswordReset = "password_reset";
    public const string EmailVerification = "email_verification";
    public const string EmailVerificationCode = "email_verification_code";
    public const string EmailChange = "email_change";
    public const string MagicLink = "magic_link";
}

/// <summary>
/// Token lifetimes, copied from <c>server/src/lib/authFlows.ts</c> and
/// <c>tokens.ts</c>. They are contract surface only indirectly — an expiry that
/// is too short turns a 200 into a 400 under the harness's own timing.
/// </summary>
public static class AuthTtl
{
    public static readonly TimeSpan PasswordReset = TimeSpan.FromHours(1);
    public static readonly TimeSpan EmailVerificationLink = TimeSpan.FromHours(24);
    public static readonly TimeSpan Code = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MagicLink = TimeSpan.FromMinutes(15);

    /// <summary>Node's <c>JWT_ACCESS_TTL</c> default.</summary>
    public static readonly TimeSpan AccessToken = TimeSpan.FromMinutes(15);

    /// <summary>Node's <c>JWT_REFRESH_TTL</c> default.</summary>
    public static readonly TimeSpan RefreshToken = TimeSpan.FromDays(30);
}

/// <summary>
/// Port of <c>server/src/models/RefreshToken.ts</c> — one row per session.
///
/// <para>
/// The raw token is never stored: only <see cref="TokenHash"/>, the sha256 hex
/// of the 43-character base64url secret. A database leak therefore yields no
/// usable session.
/// </para>
///
/// <para>
/// <b><see cref="LastUsedAt"/> is never updated after issue.</b> Despite the name
/// no route in this domain touches it — it is effectively a second creation
/// timestamp. That is Node's behaviour and it is replicated deliberately; the
/// bug is logged separately, not fixed here.
/// </para>
/// </summary>
public sealed class RefreshTokenDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    /// <summary>sha256 hex of the raw token. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Absent while the session is live — <b>absent, not null</b>.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>The hash of the row that superseded this one during rotation.</summary>
    public string? ReplacedBy { get; set; }

    /// <summary>The <c>User-Agent</c> at issue time, truncated to 256 characters.</summary>
    public string? UserAgent { get; set; }

    /// <summary>The raw socket peer address at issue time (<c>trust proxy</c> is off).</summary>
    public string? Ip { get; set; }

    public DateTime LastUsedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Port of <c>server/src/models/VerificationToken.ts</c>. Backs password reset,
/// email verification (both the mailed link and the 6-digit code), email change
/// and magic link.
///
/// <para>
/// Two different preimages feed <see cref="TokenHash"/>, and mixing them up is a
/// silent failure: a LINK token hashes the raw secret alone, while a CODE hashes
/// <c>"&lt;userId&gt;:&lt;purpose&gt;:&lt;code&gt;"</c> so that six digits minted
/// for one account are inert against another. See <c>AuthTokenHashing</c>.
/// </para>
/// </summary>
public sealed class VerificationTokenDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    /// <summary>See <c>AuthTokenHashing</c> — the preimage differs by kind.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>One of <see cref="VerificationPurposes"/>.</summary>
    public string Purpose { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Stamped on first redemption. Absent means unused.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
