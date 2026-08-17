using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

public static class UserVocabulary
{
    public static readonly IReadOnlyList<string> Themes = new[] { "system", "light", "dark" };
    public static readonly IReadOnlyList<string> TextSizes = new[] { "sm", "md", "lg" };
    public static readonly IReadOnlyList<string> MicQualities = new[] { "standard", "high" };
    public static readonly IReadOnlyList<string> SubscriptionTiers = new[] { "free", "pro" };
    public static readonly IReadOnlyList<string> DevicePlatforms = new[] { "ios", "android", "web" };

    public const string DefaultImportTime = "09:00";
    public const int MaxOnboardingAnswers = 20;
}

public sealed class MicPrefsDocument
{
    public string Quality { get; set; } = "standard";
}

public sealed class NotificationPrefsDocument
{
    public bool Push { get; set; } = true;

    public bool EmailDigest { get; set; } = true;

    public bool Marketing { get; set; }
}

public sealed class ImportPrefsDocument
{
    /// <summary>'HH:mm' on a 24-hour clock, read in the user's <c>Timezone</c>.</summary>
    public string DefaultTimeOfDay { get; set; } = UserVocabulary.DefaultImportTime;
}

public sealed class PrivacyPrefsDocument
{
    public bool Analytics { get; set; } = true;

    public bool CrashReports { get; set; } = true;
}

public sealed class SubscriptionStateDocument
{
    public string Tier { get; set; } = "free";

    public DateTime? RenewsAt { get; set; }

    public DateTime? CanceledAt { get; set; }
}

public sealed class OnboardingAnswerDocument
{
    public string Id { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;
}

/// <summary>
/// Port of <c>server/src/models/User.ts</c> — the PROFILE half of the user.
///
/// <para>
/// Credentials live in ASP.NET Identity on SQL (<c>ApplicationUser</c>); this
/// document holds everything the Node <c>users</c> collection holds and is what
/// every Mongo <c>userId</c> reference points at.
/// </para>
///
/// <para>
/// <b>Two ids, and the distinction is load-bearing.</b>
/// <see cref="Id"/> is the ObjectId the API exposes as <c>user.id</c>, what the
/// JWT <c>sub</c> carries, and what every <c>userId</c> foreign key stores —
/// byte-identical to Node. <see cref="IdentityUserId"/> is the SQL Identity key,
/// used only to reach credentials. Never leak the Guid to a client and never
/// store it in a Mongo reference.
/// </para>
/// </summary>
public sealed class UserProfileDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>Unique. Links this profile to its ASP.NET Identity row.</summary>
    public Guid IdentityUserId { get; set; }

    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Requested-but-unconfirmed address. Deliberately NOT unique — a unique index
    /// would let anyone block an address just by requesting it.
    /// </summary>
    public string? PendingEmail { get; set; }

    /// <summary>
    /// Mirrors Node's <c>passwordHash</c> presence ONLY so <c>hasPassword</c> can be
    /// derived without a SQL round trip. The real credential lives in Identity.
    /// NEVER serialize this — <c>UserMapper</c> drops it.
    /// </summary>
    public string? PasswordHash { get; set; }

    public string? DisplayName { get; set; }

    public List<string> PreferredDomains { get; set; } = new(TaskVocabulary.Domains);

    public bool HasOnboarded { get; set; }

    public List<OnboardingAnswerDocument> OnboardingAnswers { get; set; } = new();

    public DateTime? EmailVerifiedAt { get; set; }

    /// <summary>IANA zone. Absent means "trust the device"; workers fall back to UTC.</summary>
    public string? Timezone { get; set; }

    /// <summary>BCP 47 tag. The EFFECTIVE language, set even when following the device.</summary>
    public string? Locale { get; set; }

    public bool? LocaleFollowsDevice { get; set; }

    public string Theme { get; set; } = "system";

    public string TextSize { get; set; } = "md";

    public MicPrefsDocument Mic { get; set; } = new();

    public NotificationPrefsDocument Notifications { get; set; } = new();

    public ImportPrefsDocument Imports { get; set; } = new();

    public PrivacyPrefsDocument Privacy { get; set; } = new();

    public SubscriptionStateDocument Subscription { get; set; } = new();

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// When an admin suspended this account, or null for the overwhelming majority
    /// that never are.
    ///
    /// <para>
    /// <b>Not in the Node reference, and parity-safe because of the null.</b> Every
    /// guard that reads it is written as "refuse only when this is set", so a
    /// database written by the reference server — where the field is absent
    /// entirely — behaves exactly as it did before this field existed. The
    /// <c>IgnoreExtraElements</c> convention makes the absence a read of null rather
    /// than an error.
    /// </para>
    ///
    /// <para>
    /// <b>Why not Identity's <c>LockoutEnd</c>.</b> That would have been free, but
    /// <c>/auth/signin</c> verifies the password through
    /// <c>IAuthCredentialStore.VerifyPasswordAsync</c> and never consults
    /// <c>SignInManager</c>, so lockout is not read anywhere on the sign-in path. A
    /// suspension implemented there would have looked correct and done nothing.
    /// </para>
    /// </summary>
    public DateTime? SuspendedAt { get; set; }

    /// <summary>The admin's typed reason, mirrored from the audit row so support can see it in context.</summary>
    public string? SuspendedReason { get; set; }
}
