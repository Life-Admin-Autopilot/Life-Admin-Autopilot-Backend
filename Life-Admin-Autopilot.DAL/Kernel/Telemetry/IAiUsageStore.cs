using MongoDB.Bson;

namespace Life_Admin_Autopilot.DAL.Kernel.Telemetry;

/// <summary>A half-open UTC window, <c>[From, To)</c>, as the console asks for it.</summary>
/// <param name="FromDay">Inclusive <c>YYYY-MM-DD</c>.</param>
/// <param name="ToDay">Inclusive <c>YYYY-MM-DD</c>. Same value as <paramref name="FromDay"/> means one day.</param>
public readonly record struct UsageWindow(string FromDay, string ToDay)
{
    public static UsageWindow Day(string day) => new(day, day);
}

/// <summary>Totals over a window, however it was sliced.</summary>
public sealed record UsageTotals(
    int Calls,
    int Errors,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal EstimatedCostUsd,
    int UnpricedCalls)
{
    public static readonly UsageTotals Zero = new(0, 0, 0, 0, 0, 0m, 0);
}

/// <summary>One bucket of a grouped total — the key is a feature, a day, or a user id.</summary>
public sealed record UsageBucket(string Key, UsageTotals Totals);

/// <summary>A user and what they cost, for the top-spenders table.</summary>
public sealed record UserSpend(ObjectId UserId, UsageTotals Totals);

/// <summary>
/// Reads and writes AI usage. Two very different shapes live behind one interface
/// because they share a collection pair, not because they share a caller: the
/// <see cref="RecordAsync"/> side is on the hot path of every AI turn, and
/// everything else runs behind the admin console.
/// </summary>
public interface IAiUsageStore
{
    /// <summary>
    /// Append one call. <b>Callers must not let a failure here fail the turn</b> —
    /// telemetry is not worth a 500 on a request the user already paid for. The
    /// swallow lives in the recorder, not here, so this stays testable.
    /// </summary>
    Task RecordAsync(AiUsageEventDocument usage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fold one UTC day of raw events into <c>aiusagerollups</c>, replacing whatever
    /// was there. Idempotent by construction, so re-running a day after a late-arriving
    /// event is always safe and never double-counts.
    /// </summary>
    Task<int> RollupDayAsync(string day, CancellationToken cancellationToken = default);

    /// <summary>Everything in the window, as one figure.</summary>
    Task<UsageTotals> TotalsAsync(UsageWindow window, CancellationToken cancellationToken = default);

    /// <summary>Grouped by <see cref="AiUsageEventDocument.Feature"/>.</summary>
    Task<IReadOnlyList<UsageBucket>> ByFeatureAsync(UsageWindow window, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grouped by day, ascending, with <b>no gaps</b> — a day nobody used the product
    /// comes back as a zero row rather than being absent, because a line chart that
    /// skips missing days draws a straight line through them and reads as steady use.
    /// </summary>
    Task<IReadOnlyList<UsageBucket>> DailySeriesAsync(UsageWindow window, CancellationToken cancellationToken = default);

    /// <summary>The most expensive users in the window, cost descending.</summary>
    Task<IReadOnlyList<UserSpend>> TopSpendersAsync(
        UsageWindow window,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Every user's total in the window. Feeds the cost-per-user histogram.</summary>
    Task<IReadOnlyList<UserSpend>> PerUserTotalsAsync(
        UsageWindow window,
        CancellationToken cancellationToken = default);

    /// <summary>One user's window, grouped by feature. The customer-detail cost panel.</summary>
    Task<IReadOnlyList<UsageBucket>> ForUserByFeatureAsync(
        ObjectId userId,
        UsageWindow window,
        CancellationToken cancellationToken = default);

    /// <summary>One user's daily series, for the sparkline on their detail page.</summary>
    Task<IReadOnlyList<UsageBucket>> ForUserDailyAsync(
        ObjectId userId,
        UsageWindow window,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Failures grouped by cause, for the reliability view.
    ///
    /// <para>
    /// <b>This one reads the RAW events, not the rollups</b> — the rollup carries an
    /// error COUNT but not the error CODE, because a per-code rollup would multiply
    /// the row count by the size of an open-ended vocabulary. It is therefore bounded
    /// by the event TTL: ask for a window older than that and the answer is honestly
    /// empty rather than quietly wrong.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ErrorBucket>> ByErrorAsync(
        UsageWindow window,
        CancellationToken cancellationToken = default);
}

/// <summary>One failure cause and how often it happened.</summary>
/// <param name="Feature">Which surface it happened on.</param>
/// <param name="ErrorCode">The cause. <c>unknown</c> when an error carried none.</param>
public sealed record ErrorBucket(string Feature, string ErrorCode, int Count, DateTime LastSeen);
