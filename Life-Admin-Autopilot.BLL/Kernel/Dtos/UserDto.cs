using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Kernel.Dtos;

public sealed class MicPrefsDto
{
    [JsonPropertyName("quality")]
    public string Quality { get; init; } = "standard";
}

public sealed class NotificationPrefsDto
{
    [JsonPropertyName("push")]
    public bool Push { get; init; }

    [JsonPropertyName("emailDigest")]
    public bool EmailDigest { get; init; }

    [JsonPropertyName("marketing")]
    public bool Marketing { get; init; }
}

public sealed class ImportPrefsDto
{
    [JsonPropertyName("defaultTimeOfDay")]
    public string DefaultTimeOfDay { get; init; } = "09:00";
}

public sealed class PrivacyPrefsDto
{
    [JsonPropertyName("analytics")]
    public bool Analytics { get; init; }

    [JsonPropertyName("crashReports")]
    public bool CrashReports { get; init; }
}

public sealed class SubscriptionStateDto
{
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "free";

    [JsonPropertyName("renewsAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? RenewsAt { get; init; }

    [JsonPropertyName("canceledAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CanceledAt { get; init; }
}

public sealed class OnboardingAnswerDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; init; } = string.Empty;

    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;
}

/// <summary>
/// Wire shape of the user — the output of <c>UserSchema.toJSON</c>.
///
/// <para>
/// Two things the transform does that a naive serializer would not:
/// <c>passwordHash</c> is DROPPED, and the boolean <c>hasPassword</c> is derived
/// from it. Whether a password exists is not a secret and the client genuinely
/// needs it — a magic-link account has none, so asking it to re-confirm one
/// before a destructive action would demand something the user cannot give.
/// </para>
///
/// <para>
/// Property order matches the live Node response:
/// the schema fields, then <c>createdAt</c>/<c>updatedAt</c>, then <c>id</c>,
/// then <c>hasPassword</c>. <c>__v</c> is dropped (<c>versionKey: false</c>).
/// </para>
/// </summary>
public sealed class UserDto
{
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("pendingEmail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingEmail { get; init; }

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("hasOnboarded")]
    public bool HasOnboarded { get; init; }

    [JsonPropertyName("emailVerifiedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? EmailVerifiedAt { get; init; }

    [JsonPropertyName("timezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timezone { get; init; }

    [JsonPropertyName("locale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Locale { get; init; }

    [JsonPropertyName("localeFollowsDevice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LocaleFollowsDevice { get; init; }

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "system";

    [JsonPropertyName("textSize")]
    public string TextSize { get; init; } = "md";

    [JsonPropertyName("preferredDomains")]
    public IReadOnlyList<string> PreferredDomains { get; init; } = Array.Empty<string>();

    [JsonPropertyName("onboardingAnswers")]
    public IReadOnlyList<OnboardingAnswerDto> OnboardingAnswers { get; init; } = Array.Empty<OnboardingAnswerDto>();

    [JsonPropertyName("mic")]
    public MicPrefsDto Mic { get; init; } = new();

    [JsonPropertyName("notifications")]
    public NotificationPrefsDto Notifications { get; init; } = new();

    [JsonPropertyName("imports")]
    public ImportPrefsDto Imports { get; init; } = new();

    [JsonPropertyName("privacy")]
    public PrivacyPrefsDto Privacy { get; init; } = new();

    [JsonPropertyName("subscription")]
    public SubscriptionStateDto Subscription { get; init; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Derived from the presence of a password hash. The hash itself never leaves the server.</summary>
    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; init; }
}
