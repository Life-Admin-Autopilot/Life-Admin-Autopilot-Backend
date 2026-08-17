using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>Who is acting, resolved from the console token and carried into every audit row.</summary>
public readonly record struct AdminActor(Guid Id, string Email, string Role, string? Ip, string? UserAgent);

/// <summary>The saved segments, as the console names them.</summary>
public static class AdminSegment
{
    public const string All = "all";
    public const string CostOutliers = "cost-outliers";
    public const string HeavyAi = "heavy-ai";
    public const string Dormant = "dormant";
    public const string NeverActivated = "never-activated";
    public const string Unverified = "unverified";
    public const string Suspended = "suspended";

    public static readonly IReadOnlyList<string> All_ = new[]
    {
        All, CostOutliers, HeavyAi, Dormant, NeverActivated, Unverified, Suspended,
    };
}

/// <summary>
/// The customer surfaces: the list, one customer, and the account-state actions.
///
/// <para>
/// <b>Every mutation writes its audit row BEFORE it acts</b> (see
/// <see cref="AuditedAsync"/>). That ordering is the point: an action that succeeded
/// without leaving a trace is strictly worse than an action that was refused because
/// the trace could not be written.
/// </para>
/// </summary>
public sealed class AdminCustomerService
{
    /// <summary>How long without activity counts as dormant.</summary>
    public const int DormantDays = 14;

    /// <summary>Trailing window the list's usage column reports.</summary>
    public const int UsageWindowDays = 30;

    /// <summary>Minimum characters of typed justification. Short enough to be quick, long enough to be a sentence.</summary>
    public const int MinReasonLength = 6;

    /// <summary>
    /// The furthest a segment resolution will ever scan.
    ///
    /// <para>
    /// Not a page size — an absolute ceiling on how much of the customer base one
    /// segment query is allowed to materialise as ids. It sits far above the
    /// broadcast cap so the cap is what refuses an oversized send, and this only
    /// exists so a pathological database cannot exhaust memory.
    /// </para>
    /// </summary>
    public const int MaxSegmentScan = 10_000;

    private readonly IAdminCustomerRepository _customers;
    private readonly IAiUsageStore _usage;
    private readonly IAdminAuditStore _audit;
    private readonly ISessionRepository _sessions;
    private readonly AiQuotaService _quota;
    private readonly TimeProvider _time;

    public AdminCustomerService(
        IAdminCustomerRepository customers,
        IAiUsageStore usage,
        IAdminAuditStore audit,
        ISessionRepository sessions,
        AiQuotaService quota,
        TimeProvider? time = null)
    {
        _customers = customers;
        _usage = usage;
        _audit = audit;
        _sessions = sessions;
        _quota = quota;
        _time = time ?? TimeProvider.System;
    }

    // ---- reads -------------------------------------------------------------

    public async Task<AdminCustomerPageDto> SearchAsync(
        string? search,
        string? segment,
        string? sortBy,
        bool descending,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var window = AdminInsightService.WindowEndingToday(now, UsageWindowDays);

        var query = new AdminCustomerQuery(
            Search: search,
            SortBy: AdminCustomerSort.Normalize(sortBy),
            Descending: descending,
            Skip: skip,
            Take: take);

        query = await ApplySegmentAsync(query, segment, window, now, cancellationToken).ConfigureAwait(false);

        var page = await _customers.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        // One usage query for the whole page, keyed by id — not one per row.
        var perUser = (await _usage.PerUserTotalsAsync(window, cancellationToken).ConfigureAwait(false))
            .ToDictionary(u => u.UserId, u => u.Totals);

        return new AdminCustomerPageDto
        {
            Rows = page.Rows.Select(r => ToDto(r, perUser.GetValueOrDefault(r.Id, UsageTotals.Zero))).ToList(),
            Total = page.Total,
            Skip = Math.Max(0, skip),
            Take = Math.Clamp(take, 1, AdminCustomerRepository.MaxTake),
        };
    }

    /// <summary>
    /// Turn a segment name into query constraints.
    ///
    /// <para>
    /// The two usage-derived segments resolve to an id set first, because spend lives
    /// in the rollups and not on the user document. An empty set is passed down as an
    /// empty set rather than as null — <c>RestrictTo</c> distinguishes "nothing
    /// matched" from "no restriction", and conflating them would render the whole
    /// customer base for a segment that found nobody.
    /// </para>
    /// </summary>
    private async Task<AdminCustomerQuery> ApplySegmentAsync(
        AdminCustomerQuery query,
        string? segment,
        UsageWindow window,
        DateTime now,
        CancellationToken cancellationToken)
    {
        switch (segment)
        {
            case AdminSegment.Suspended:
                return query with { OnlySuspended = true };

            case AdminSegment.Unverified:
                return query with { OnlyUnverified = true };

            case AdminSegment.NeverActivated:
                return query with { OnlyNeverOnboarded = true };

            case AdminSegment.Dormant:
            {
                // Approximated as "signed up more than N days ago and made no AI call
                // in the window". Honest for what it is: someone who only ever uses
                // manual matters reads as dormant here, which is worth knowing when
                // reading the number.
                var active = (await _usage.PerUserTotalsAsync(window, cancellationToken).ConfigureAwait(false))
                    .Where(u => u.Totals.Calls > 0)
                    .Select(u => u.UserId)
                    .ToHashSet();

                // IdsAsync, NOT SearchAsync: the paged search would cap this at one
                // page, so the segment would silently describe only the first 200
                // dormant customers — and broadcast would then "reach everyone" by
                // reaching 200 of them.
                var stale = await _customers
                    .IdsAsync(
                        query with { CreatedBefore = now.AddDays(-DormantDays) },
                        MaxSegmentScan,
                        cancellationToken)
                    .ConfigureAwait(false);

                return query with
                {
                    RestrictTo = stale.Where(id => !active.Contains(id)).ToList(),
                };
            }

            case AdminSegment.CostOutliers:
            {
                var spenders = await _usage.TopSpendersAsync(window, 50, cancellationToken).ConfigureAwait(false);
                return query with
                {
                    RestrictTo = spenders
                        .Where(s => s.Totals.EstimatedCostUsd > AdminInsightService.DefaultBreakEvenUsd)
                        .Select(s => s.UserId)
                        .ToList(),
                };
            }

            case AdminSegment.HeavyAi:
            {
                var perUser = await _usage.PerUserTotalsAsync(window, cancellationToken).ConfigureAwait(false);
                var ordered = perUser.OrderByDescending(u => u.Totals.Calls).Take(50).ToList();

                return query with
                {
                    RestrictTo = ordered.Where(u => u.Totals.Calls > 0).Select(u => u.UserId).ToList(),
                };
            }

            default:
                return query;
        }
    }

    /// <summary>
    /// How many customers a segment really contains.
    ///
    /// <para>
    /// <b>This is what the broadcast confirm dialog must show.</b> It counts rather
    /// than pages, because "send to 4,182 people" and "send to 200 people" are
    /// different decisions and the operator only gets one chance to make it.
    /// </para>
    /// </summary>
    public async Task<long> SegmentCountAsync(
        string? segment,
        CancellationToken cancellationToken = default)
    {
        var query = await SegmentQueryAsync(segment, cancellationToken).ConfigureAwait(false);
        return await _customers.CountAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ids of everyone in a segment, up to <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<ObjectId>> SegmentIdsAsync(
        string? segment,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = await SegmentQueryAsync(segment, cancellationToken).ConfigureAwait(false);
        return await _customers.IdsAsync(query, limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdminCustomerQuery> SegmentQueryAsync(
        string? segment,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var window = AdminInsightService.WindowEndingToday(now, UsageWindowDays);

        return await ApplySegmentAsync(new AdminCustomerQuery(), segment, window, now, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AdminCustomerDetailDto> DetailAsync(
        ObjectId id,
        CancellationToken cancellationToken = default)
    {
        var user = await _customers.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("customer_not_found", "No customer with that id.");

        var now = _time.GetUtcNow().UtcDateTime;
        var window = AdminInsightService.WindowEndingToday(now, UsageWindowDays);

        var counts = await _customers.CountsForAsync(id, cancellationToken).ConfigureAwait(false);
        var byFeature = await _usage.ForUserByFeatureAsync(id, window, cancellationToken).ConfigureAwait(false);
        var daily = await _usage.ForUserDailyAsync(id, window, cancellationToken).ConfigureAwait(false);

        var tier = AiQuotaService.ResolveTier();
        var quotas = await _quota.GetStatusAsync(id, tier, cancellationToken: cancellationToken).ConfigureAwait(false);

        var totals = byFeature.Aggregate(UsageTotals.Zero, (acc, b) => new UsageTotals(
            acc.Calls + b.Totals.Calls,
            acc.Errors + b.Totals.Errors,
            acc.InputTokens + b.Totals.InputTokens,
            acc.OutputTokens + b.Totals.OutputTokens,
            acc.TotalTokens + b.Totals.TotalTokens,
            acc.EstimatedCostUsd + b.Totals.EstimatedCostUsd,
            acc.UnpricedCalls + b.Totals.UnpricedCalls));

        return new AdminCustomerDetailDto
        {
            Customer = ToDto(ToRow(user), totals),
            Timezone = user.Timezone,
            Matters = counts.Matters,
            OpenMatters = counts.OpenMatters,
            Documents = counts.Documents,
            Conversations = counts.Conversations,
            Quotas = quotas
                .Select(q => new AdminQuotaMeterDto
                {
                    Kind = q.Kind,
                    Used = q.Used,
                    Limit = q.Limit,
                    ResetAt = q.ResetAt,
                })
                .ToList(),
            CostByFeature = byFeature.Select(AdminInsightService.ToDto).ToList(),
            DailySeries = daily.Select(AdminInsightService.ToDto).ToList(),
        };
    }

    // ---- actions -----------------------------------------------------------

    public Task<AdminActionResultDto> SuspendAsync(
        ObjectId id,
        AdminActor actor,
        string? reason,
        CancellationToken cancellationToken = default) =>
        AuditedAsync(id, actor, reason, AdminAuditAction.CustomerSuspended, async (user, at) =>
        {
            await _customers.SetSuspendedAsync(id, at, Require(reason), at, cancellationToken).ConfigureAwait(false);

            // Suspension that leaves live sessions running is theatre — the access
            // token in the app keeps working until it expires. Revoking the refresh
            // tokens caps the blast radius at one access-token lifetime.
            await _sessions.RevokeAllAsync(id, at, cancellationToken: cancellationToken).ConfigureAwait(false);

            return $"{user.Email} is suspended and their sessions are revoked.";
        });

    public Task<AdminActionResultDto> RestoreAsync(
        ObjectId id,
        AdminActor actor,
        string? reason,
        CancellationToken cancellationToken = default) =>
        AuditedAsync(id, actor, reason, AdminAuditAction.CustomerRestored, async (user, at) =>
        {
            await _customers.SetSuspendedAsync(id, null, null, at, cancellationToken).ConfigureAwait(false);
            return $"{user.Email} can sign in again.";
        });

    public Task<AdminActionResultDto> ResetQuotasAsync(
        ObjectId id,
        AdminActor actor,
        string? reason,
        CancellationToken cancellationToken = default) =>
        AuditedAsync(id, actor, reason, AdminAuditAction.CustomerQuotaReset, async (user, _) =>
        {
            var removed = await _customers.ResetQuotasAsync(id, cancellationToken).ConfigureAwait(false);
            return $"Cleared {removed} counter row(s) for {user.Email}.";
        });

    public Task<AdminActionResultDto> RevokeSessionsAsync(
        ObjectId id,
        AdminActor actor,
        string? reason,
        CancellationToken cancellationToken = default) =>
        AuditedAsync(id, actor, reason, AdminAuditAction.CustomerSessionsRevoked, async (user, at) =>
        {
            await _sessions.RevokeAllAsync(id, at, cancellationToken: cancellationToken).ConfigureAwait(false);
            return $"Signed {user.Email} out of every device.";
        });

    public Task<AdminActionResultDto> GrantTierAsync(
        ObjectId id,
        AdminActor actor,
        string? reason,
        string? tier,
        int? days,
        CancellationToken cancellationToken = default) =>
        AuditedAsync(id, actor, reason, AdminAuditAction.CustomerTierGranted, async (user, at) =>
        {
            var resolved = tier?.Trim().ToLowerInvariant();
            if (resolved is not ("free" or "pro"))
            {
                throw AppException.BadRequest("invalid_tier", "Tier must be 'free' or 'pro'.");
            }

            var renewsAt = days is > 0 ? at.AddDays(days.Value) : (DateTime?)null;
            await _customers.SetTierAsync(id, resolved, renewsAt, at, cancellationToken).ConfigureAwait(false);

            // Stated plainly because it is a live trap: AiQuotaService.ResolveTier()
            // returns "free" unconditionally, so writing 'pro' here changes what the
            // console displays and NOT what the user is allowed to do. Whoever wires
            // billing has to change that method too.
            var caveat = resolved == "pro"
                ? " Note: quota limits still resolve to free until resolveTier() reads this field."
                : string.Empty;

            return $"{user.Email} is now on '{resolved}'.{caveat}";
        });

    /// <summary>
    /// The one place an admin action becomes a stored fact.
    ///
    /// <para>
    /// <b>Order: validate → load → audit → act.</b> The audit row goes in before the
    /// mutation and is NOT rolled back if the mutation then fails — instead the row is
    /// amended to <c>failed</c>. An audit log that only records successes cannot answer
    /// the question it exists for, which is what someone tried to do.
    /// </para>
    /// </summary>
    private async Task<AdminActionResultDto> AuditedAsync(
        ObjectId id,
        AdminActor actor,
        string? reason,
        string action,
        Func<UserProfileDocument, DateTime, Task<string>> act,
        CancellationToken cancellationToken = default)
    {
        var justification = Require(reason);
        var at = _time.GetUtcNow().UtcDateTime;

        var user = await _customers.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("customer_not_found", "No customer with that id.");

        var entry = new AdminAuditEventDocument
        {
            At = at,
            ActorId = actor.Id,
            ActorEmail = actor.Email,
            ActorRole = actor.Role,
            Action = action,
            TargetUserId = id.ToString(),
            TargetEmail = user.Email,
            Reason = justification,
            Ip = actor.Ip,
            UserAgent = actor.UserAgent,
            Outcome = AdminAuditOutcome.Ok,
        };

        // Deliberately not swallowed. If this throws, the action does not happen.
        await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        try
        {
            var message = await act(user, at).ConfigureAwait(false);
            return new AdminActionResultDto { Action = action, Message = message };
        }
        catch (Exception ex)
        {
            await _audit.AppendAsync(
                new AdminAuditEventDocument
                {
                    At = _time.GetUtcNow().UtcDateTime,
                    ActorId = actor.Id,
                    ActorEmail = actor.Email,
                    ActorRole = actor.Role,
                    Action = action,
                    TargetUserId = id.ToString(),
                    TargetEmail = user.Email,
                    Reason = justification,
                    Ip = actor.Ip,
                    UserAgent = actor.UserAgent,
                    Outcome = AdminAuditOutcome.Failed,
                    Error = ex.Message,
                },
                cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    private static string Require(string? reason)
    {
        var trimmed = reason?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < MinReasonLength)
        {
            throw AppException.BadRequest(
                "reason_required",
                $"Every account action needs a reason of at least {MinReasonLength} characters.");
        }

        return trimmed;
    }

    // ---- mapping -----------------------------------------------------------

    private static AdminCustomerRow ToRow(UserProfileDocument u) => new(
        u.Id, u.IdentityUserId, u.Email, u.DisplayName, u.CreatedAt, u.UpdatedAt,
        u.EmailVerifiedAt, u.SuspendedAt, u.SuspendedReason, u.Subscription.Tier,
        u.Locale, u.Timezone, u.HasOnboarded);

    private static AdminCustomerRowDto ToDto(AdminCustomerRow r, UsageTotals usage) => new()
    {
        Id = r.Id.ToString(),
        Email = r.Email,
        DisplayName = r.DisplayName,
        CreatedAt = r.CreatedAt,

        // `updatedAt` is the closest thing to a last-seen timestamp the schema has.
        // It moves on preference writes as well as on real use, so it overstates
        // activity — named `lastActiveAt` because that is what it is used as, and
        // flagged here because it is not what it sounds like.
        LastActiveAt = r.UpdatedAt,
        EmailVerified = r.EmailVerifiedAt is not null,
        SuspendedAt = r.SuspendedAt,
        SuspendedReason = r.SuspendedReason,
        HasOnboarded = r.HasOnboarded,
        Tier = r.Tier,
        Locale = r.Locale,
        Usage30d = AdminInsightService.ToDto(usage),
    };
}
