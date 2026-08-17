using System.Globalization;
using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>
/// Everything the dashboards read. Pure aggregation over the rollups plus the user
/// collection — no mutation, so no audit entries.
/// </summary>
public sealed class AdminInsightService
{
    /// <summary>
    /// Net monthly revenue per subscriber at the intended price, used as the
    /// break-even line on the cost histogram.
    ///
    /// <para>
    /// Default is $59/yr through a store: $4.92/mo gross, less the 15% cut, ≈ $4.18.
    /// Override with <c>Admin:BreakEvenUsd</c> when the price or the channel changes —
    /// selling direct through a merchant-of-record moves this by roughly $0.45.
    /// </para>
    /// </summary>
    public const decimal DefaultBreakEvenUsd = 4.18m;

    /// <summary>
    /// Histogram edges, USD per month. Deliberately uneven and dense at the bottom:
    /// almost every user sits under a dollar, and equal-width buckets would put the
    /// entire population in the first bar and tell you nothing.
    /// </summary>
    private static readonly decimal[] HistogramEdges =
        { 0m, 0.10m, 0.25m, 0.50m, 1m, 2m, 4m, 8m, 16m };

    private readonly IAiUsageStore _usage;
    private readonly IAdminCustomerRepository _customers;
    private readonly TimeProvider _time;
    private readonly decimal _breakEven;

    public AdminInsightService(
        IAiUsageStore usage,
        IAdminCustomerRepository customers,
        IConfiguration configuration,
        TimeProvider? time = null)
    {
        _usage = usage;
        _customers = customers;
        _time = time ?? TimeProvider.System;
        _breakEven = decimal.TryParse(
            configuration["Admin:BreakEvenUsd"],
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var configured) && configured > 0
            ? configured
            : DefaultBreakEvenUsd;
    }

    public async Task<AdminPulseDto> PulseAsync(int days, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var window = WindowEndingToday(now, days);
        var today = UsageQuotaBuckets.UtcDate(now);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthWindow = new UsageWindow(UsageQuotaBuckets.UtcDate(monthStart), today);

        var todayTotals = await _usage.TotalsAsync(UsageWindow.Day(today), cancellationToken).ConfigureAwait(false);
        var monthTotals = await _usage.TotalsAsync(monthWindow, cancellationToken).ConfigureAwait(false);
        var series = await _usage.DailySeriesAsync(window, cancellationToken).ConfigureAwait(false);
        var byFeature = await _usage.ByFeatureAsync(window, cancellationToken).ConfigureAwait(false);
        var perUser = await _usage.PerUserTotalsAsync(window, cancellationToken).ConfigureAwait(false);

        var signups = await _customers
            .SignupsByDayAsync(monthStart.AddDays(-days), now.AddDays(1).Date, cancellationToken)
            .ConfigureAwait(false);

        var totalCustomers = await _customers.TotalCustomersAsync(cancellationToken).ConfigureAwait(false);

        // Straight-line projection. Day-of-month is the elapsed denominator, so on
        // the 1st this is just "today × days in month" — noisy, and labelled as an
        // estimate in the UI rather than smoothed into false confidence here.
        var daysElapsed = Math.Max(1, now.Day);
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var projected = monthTotals.EstimatedCostUsd / daysElapsed * daysInMonth;

        return new AdminPulseDto
        {
            Window = $"{window.FromDay}..{window.ToDay}",
            Today = ToDto(todayTotals),
            MonthToDate = ToDto(monthTotals),
            ProjectedMonthUsd = decimal.Round(projected, 4),
            SignupsToday = signups.TryGetValue(today, out var count) ? count : 0,
            TotalCustomers = totalCustomers,
            ActiveUsers = perUser.Count(u => u.Totals.Calls > 0),
            DailySeries = series.Select(ToDto).ToList(),
            ByFeature = byFeature.Select(ToDto).ToList(),
            SignupSeries = DaysOf(window)
                .Select(day => new CountPointDto
                {
                    Day = day,
                    Count = signups.TryGetValue(day, out var n) ? n : 0,
                })
                .ToList(),
        };
    }

    public async Task<IReadOnlyList<SpenderDto>> TopSpendersAsync(
        int days,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var window = WindowEndingToday(_time.GetUtcNow().UtcDateTime, days);
        var spenders = await _usage.TopSpendersAsync(window, limit, cancellationToken).ConfigureAwait(false);

        var emails = await _customers
            .EmailsForAsync(spenders.Select(s => s.UserId).ToList(), cancellationToken)
            .ConfigureAwait(false);

        return spenders
            .Select(s => new SpenderDto
            {
                UserId = s.UserId.ToString(),

                // A spender whose account has since been deleted still has rollup
                // rows until the erasure runs. Showing the id rather than dropping
                // the row keeps the total on this page equal to the total on Pulse.
                Email = emails.TryGetValue(s.UserId, out var email) ? email : "(deleted account)",
                Totals = ToDto(s.Totals),
            })
            .ToList();
    }

    public async Task<CostDistributionDto> CostDistributionAsync(
        int days,
        CancellationToken cancellationToken = default)
    {
        var window = WindowEndingToday(_time.GetUtcNow().UtcDateTime, days);
        var perUser = await _usage.PerUserTotalsAsync(window, cancellationToken).ConfigureAwait(false);

        var costs = perUser
            .Select(u => u.Totals.EstimatedCostUsd)
            .OrderBy(c => c)
            .ToList();

        var buckets = new List<HistogramBucketDto>();
        for (var i = 0; i < HistogramEdges.Length; i++)
        {
            var from = HistogramEdges[i];
            decimal? to = i + 1 < HistogramEdges.Length ? HistogramEdges[i + 1] : null;

            buckets.Add(new HistogramBucketDto
            {
                FromUsd = from,
                ToUsd = to,
                Users = costs.Count(c => c >= from && (to is null || c < to.Value)),
            });
        }

        return new CostDistributionDto
        {
            Buckets = buckets,
            BreakEvenUsd = _breakEven,
            UsersAboveBreakEven = costs.Count(c => c > _breakEven),
            MedianUsd = Median(costs),
            MeanUsd = costs.Count == 0 ? 0m : decimal.Round(costs.Sum() / costs.Count, 4),
        };
    }

    public async Task<IReadOnlyList<UsageBucketDto>> ByFeatureAsync(
        int days,
        CancellationToken cancellationToken = default)
    {
        var window = WindowEndingToday(_time.GetUtcNow().UtcDateTime, days);
        var rows = await _usage.ByFeatureAsync(window, cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<UsageBucketDto>> DailySeriesAsync(
        int days,
        CancellationToken cancellationToken = default)
    {
        var window = WindowEndingToday(_time.GetUtcNow().UtcDateTime, days);
        var rows = await _usage.DailySeriesAsync(window, cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// A window of <paramref name="days"/> ending today, inclusive. Clamped to a
    /// year: the rollups are permanent, so an unbounded window is a query nobody
    /// meant to run.
    /// </summary>
    public static UsageWindow WindowEndingToday(DateTime now, int days)
    {
        var span = Math.Clamp(days, 1, 366);
        return new UsageWindow(
            UsageQuotaBuckets.UtcDate(now.AddDays(-(span - 1))),
            UsageQuotaBuckets.UtcDate(now));
    }

    private static IEnumerable<string> DaysOf(UsageWindow window)
    {
        if (!DateTime.TryParseExact(window.FromDay, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var from)
            || !DateTime.TryParseExact(window.ToDay, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var to))
        {
            yield break;
        }

        for (var cursor = from; cursor <= to; cursor = cursor.AddDays(1))
        {
            yield return cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private static decimal Median(IReadOnlyList<decimal> sorted) => sorted.Count switch
    {
        0 => 0m,
        var n when n % 2 == 1 => sorted[n / 2],
        var n => decimal.Round((sorted[(n / 2) - 1] + sorted[n / 2]) / 2m, 4),
    };

    internal static UsageTotalsDto ToDto(UsageTotals t) => new()
    {
        Calls = t.Calls,
        Errors = t.Errors,
        InputTokens = t.InputTokens,
        OutputTokens = t.OutputTokens,
        TotalTokens = t.TotalTokens,
        EstimatedCostUsd = decimal.Round(t.EstimatedCostUsd, 6),
        UnpricedCalls = t.UnpricedCalls,
    };

    internal static UsageBucketDto ToDto(UsageBucket b) => new()
    {
        Key = b.Key,
        Totals = ToDto(b.Totals),
    };
}
