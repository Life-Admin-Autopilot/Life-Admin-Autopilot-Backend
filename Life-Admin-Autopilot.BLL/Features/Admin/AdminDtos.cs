using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>
/// Every usage figure the console renders, in one shape.
///
/// <para>
/// <b><see cref="UnpricedCalls"/> travels with every total, everywhere.</b> It is
/// what lets the UI caveat a number instead of presenting an under-count as fact —
/// a call whose model had no price contributes real tokens and zero dollars, and a
/// cost figure that hides that is quietly wrong.
/// </para>
/// </summary>
public sealed class UsageTotalsDto
{
    [JsonPropertyName("calls")]
    public int Calls { get; init; }

    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("estimatedCostUsd")]
    public decimal EstimatedCostUsd { get; init; }

    [JsonPropertyName("unpricedCalls")]
    public int UnpricedCalls { get; init; }
}

/// <summary>One point of any grouped series — a day, a feature, a model.</summary>
public sealed class UsageBucketDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("totals")]
    public UsageTotalsDto Totals { get; init; } = new();
}

/// <summary>The Pulse screen, in one request.</summary>
public sealed class AdminPulseDto
{
    [JsonPropertyName("window")]
    public string Window { get; init; } = string.Empty;

    [JsonPropertyName("today")]
    public UsageTotalsDto Today { get; init; } = new();

    [JsonPropertyName("monthToDate")]
    public UsageTotalsDto MonthToDate { get; init; } = new();

    /// <summary>
    /// Month-end spend, extrapolated from the days elapsed so far.
    ///
    /// <para>
    /// Straight-line, and stated as such. A smarter model would be less honest: with
    /// a handful of days of data the error bar is enormous, and dressing it up as a
    /// forecast invites someone to plan against it.
    /// </para>
    /// </summary>
    [JsonPropertyName("projectedMonthUsd")]
    public decimal ProjectedMonthUsd { get; init; }

    [JsonPropertyName("signupsToday")]
    public int SignupsToday { get; init; }

    [JsonPropertyName("totalCustomers")]
    public long TotalCustomers { get; init; }

    /// <summary>Active users in the window — anyone who made at least one AI call.</summary>
    [JsonPropertyName("activeUsers")]
    public int ActiveUsers { get; init; }

    [JsonPropertyName("dailySeries")]
    public IReadOnlyList<UsageBucketDto> DailySeries { get; init; } = Array.Empty<UsageBucketDto>();

    [JsonPropertyName("byFeature")]
    public IReadOnlyList<UsageBucketDto> ByFeature { get; init; } = Array.Empty<UsageBucketDto>();

    [JsonPropertyName("signupSeries")]
    public IReadOnlyList<CountPointDto> SignupSeries { get; init; } = Array.Empty<CountPointDto>();
}

public sealed class CountPointDto
{
    [JsonPropertyName("day")]
    public string Day { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary>A row of the top-spenders table.</summary>
public sealed class SpenderDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("totals")]
    public UsageTotalsDto Totals { get; init; } = new();
}

/// <summary>
/// The cost-per-user histogram, plus the line that gives it meaning.
/// </summary>
public sealed class CostDistributionDto
{
    [JsonPropertyName("buckets")]
    public IReadOnlyList<HistogramBucketDto> Buckets { get; init; } = Array.Empty<HistogramBucketDto>();

    /// <summary>
    /// Monthly net revenue per subscriber at the current intended price, drawn on the
    /// histogram. Every user to the right of it costs more than they would pay.
    /// </summary>
    [JsonPropertyName("breakEvenUsd")]
    public decimal BreakEvenUsd { get; init; }

    [JsonPropertyName("usersAboveBreakEven")]
    public int UsersAboveBreakEven { get; init; }

    [JsonPropertyName("medianUsd")]
    public decimal MedianUsd { get; init; }

    [JsonPropertyName("meanUsd")]
    public decimal MeanUsd { get; init; }
}

public sealed class HistogramBucketDto
{
    /// <summary>Inclusive lower bound, USD.</summary>
    [JsonPropertyName("fromUsd")]
    public decimal FromUsd { get; init; }

    /// <summary>Exclusive upper bound. Null on the final open-ended bucket.</summary>
    [JsonPropertyName("toUsd")]
    public decimal? ToUsd { get; init; }

    [JsonPropertyName("users")]
    public int Users { get; init; }
}

/// <summary>A row of the customer table.</summary>
public sealed class AdminCustomerRowDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("lastActiveAt")]
    public DateTime? LastActiveAt { get; init; }

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; init; }

    [JsonPropertyName("suspendedAt")]
    public DateTime? SuspendedAt { get; init; }

    [JsonPropertyName("suspendedReason")]
    public string? SuspendedReason { get; init; }

    [JsonPropertyName("hasOnboarded")]
    public bool HasOnboarded { get; init; }

    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "free";

    [JsonPropertyName("locale")]
    public string? Locale { get; init; }

    /// <summary>Trailing-30-day usage, joined from the rollups.</summary>
    [JsonPropertyName("usage30d")]
    public UsageTotalsDto Usage30d { get; init; } = new();
}

public sealed class AdminCustomerPageDto
{
    [JsonPropertyName("rows")]
    public IReadOnlyList<AdminCustomerRowDto> Rows { get; init; } = Array.Empty<AdminCustomerRowDto>();

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("skip")]
    public int Skip { get; init; }

    [JsonPropertyName("take")]
    public int Take { get; init; }
}

/// <summary>Everything the customer-detail page needs, in one request.</summary>
public sealed class AdminCustomerDetailDto
{
    [JsonPropertyName("customer")]
    public AdminCustomerRowDto Customer { get; init; } = new();

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    [JsonPropertyName("matters")]
    public int Matters { get; init; }

    [JsonPropertyName("openMatters")]
    public int OpenMatters { get; init; }

    [JsonPropertyName("documents")]
    public int Documents { get; init; }

    [JsonPropertyName("conversations")]
    public int Conversations { get; init; }

    [JsonPropertyName("quotas")]
    public IReadOnlyList<AdminQuotaMeterDto> Quotas { get; init; } = Array.Empty<AdminQuotaMeterDto>();

    [JsonPropertyName("costByFeature")]
    public IReadOnlyList<UsageBucketDto> CostByFeature { get; init; } = Array.Empty<UsageBucketDto>();

    [JsonPropertyName("dailySeries")]
    public IReadOnlyList<UsageBucketDto> DailySeries { get; init; } = Array.Empty<UsageBucketDto>();
}

public sealed class AdminQuotaMeterDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("used")]
    public int Used { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("resetAt")]
    public string ResetAt { get; init; } = string.Empty;
}

/// <summary>
/// The body every mutating admin action takes.
///
/// <para>
/// <c>reason</c> is required and minimum-length checked. See
/// <c>AdminAuditEventDocument.Reason</c> for why a reason box is a control and not
/// a formality.
/// </para>
/// </summary>
public sealed class AdminActionRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Only read by the tier grant.</summary>
    [JsonPropertyName("tier")]
    public string? Tier { get; init; }

    /// <summary>Only read by the tier grant. Days from now; null grants indefinitely.</summary>
    [JsonPropertyName("days")]
    public int? Days { get; init; }
}

/// <summary>What the console shows after a successful action.</summary>
public sealed class AdminActionResultDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>The console session, returned by sign-in and by <c>/admin/auth/me</c>.</summary>
public sealed class AdminSessionDto
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; init; }

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;
}
