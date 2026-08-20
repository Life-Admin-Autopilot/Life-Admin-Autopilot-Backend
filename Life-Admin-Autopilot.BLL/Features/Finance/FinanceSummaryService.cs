using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Finance;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Time;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Finance;

/// <summary>
/// Turns priced matters and priced documents into the financial summary.
///
/// <para>
/// <b>The rules this service refuses to break</b>, because each one is a way a
/// money page can lie:
/// </para>
/// <list type="number">
/// <item>
/// <b>Never count the same money twice.</b> A document and the matter filed from
/// it describe ONE payment. Where both carry a figure the matter wins, because it
/// is the row whose status the user maintains.
/// </item>
/// <item>
/// <b>Never claim a payment happened.</b> "Spent" means a matter the user marked
/// done, or a receipt — which is by definition proof of payment. An unpaid bill
/// sitting in the documents list is neither.
/// </item>
/// <item>
/// <b>Never claim a debt exists.</b> An old bill with no matter attached may well
/// have been paid at the counter; calling it overdue invents an obligation. Only
/// a matter the user left open, or a bill dated in the FUTURE, is owed.
/// </item>
/// <item>
/// <b>Never convert between currencies.</b> See <see cref="FinanceCurrencyDto"/>.
/// </item>
/// </list>
/// </summary>
public sealed class FinanceSummaryService
{
    public const int DefaultMonths = 6;
    public const int MaxMonths = 24;
    private const int ListLength = 5;

    /// <summary>
    /// Document kinds whose printed due date is a real future obligation. A
    /// <c>receipt</c> is deliberately absent — it is proof the money already
    /// moved — and so are <c>letter</c>, <c>form</c>, <c>identity</c>,
    /// <c>medical</c> and <c>legal</c>, whose figures are rarely a bill to pay.
    /// </summary>
    private static readonly HashSet<string> BillLikeTypes =
        new(StringComparer.Ordinal) { "bill", "statement", "tax", "insurance" };

    private readonly IFinanceRepository _finance;

    public FinanceSummaryService(IFinanceRepository finance) => _finance = finance;

    public async Task<FinanceSummaryDto> BuildAsync(
        ObjectId userId,
        int months,
        string? timezone,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var window = Math.Clamp(months, 1, MaxMonths);
        var zone = ResolveZone(timezone);

        var matters = await _finance.ListPricedMattersAsync(userId, cancellationToken).ConfigureAwait(false);
        var documents = await _finance.ListPricedDocumentsAsync(userId, cancellationToken).ConfigureAwait(false);
        var documentsTotal = await _finance.CountDocumentsAsync(userId, cancellationToken).ConfigureAwait(false);

        // Rule 1. Every document a priced matter came from is spoken for; its own
        // headline figure describes the same payment and must not be added again.
        var claimed = matters
            .Where(m => m.SourceDocumentId is not null)
            .Select(m => m.SourceDocumentId!.Value)
            .ToHashSet();

        var entries = new List<PricedEntry>(matters.Count + documents.Count);
        entries.AddRange(matters.Select(m => FromMatter(m, nowUtc)));
        entries.AddRange(documents
            .Where(d => !claimed.Contains(d.Id))
            .Select(d => FromDocument(d, nowUtc)));

        var windowStart = StartOfMonth(nowUtc, zone, -(window - 1));
        var thisMonth = StartOfMonth(nowUtc, zone, 0);
        var lastMonth = StartOfMonth(nowUtc, zone, -1);

        var currencies = entries
            .GroupBy(e => e.Currency, StringComparer.Ordinal)
            .Select(group => BuildCurrency(group.Key, group.ToList(), zone, window, windowStart, thisMonth, lastMonth))
            // Busiest first: the block the user opens on should be the one their
            // life actually happens in, not whichever code sorts first.
            .OrderByDescending(c => c.SpentWindowMinor + c.UpcomingMinor + c.OverdueMinor)
            .ThenBy(c => c.Currency, StringComparer.Ordinal)
            .ToList();

        return new FinanceSummaryDto
        {
            Months = window,
            Timezone = zone.Id,
            GeneratedAt = nowUtc,
            Currencies = currencies,
            Coverage = new FinanceCoverageDto
            {
                DocumentsTotal = documentsTotal,
                DocumentsWithAmount = documents.Count,
                MattersWithAmount = matters.Count,
            },
        };
    }

    // ---- Classification ----------------------------------------------------

    /// <summary>
    /// What a row contributes, once. <c>Bucket</c> is the whole decision: it is
    /// assigned exactly here, so no downstream sum can reinterpret a row.
    /// </summary>
    private sealed record PricedEntry(
        string Id,
        string Kind,
        string Title,
        string? Domain,
        string Currency,
        long AmountMinor,
        string Source,
        Bucket Bucket,
        DateTime? At,
        bool Overdue);

    private enum Bucket
    {
        /// <summary>Money that left. Counted in the spend totals and the trend.</summary>
        Spent,

        /// <summary>Money owed. Counted in overdue/upcoming, never in spend.</summary>
        Owed,

        /// <summary>Money received — a refund or rebate. Reported, never netted off.</summary>
        Received,

        /// <summary>
        /// A figure we can see but cannot honestly classify: a bill with no matter
        /// and no future due date, which may or may not have been paid elsewhere.
        /// Counted nowhere; visible only through <see cref="FinanceCoverageDto"/>.
        /// </summary>
        Unknown,
    }

    private static PricedEntry FromMatter(TaskDocument matter, DateTime nowUtc)
    {
        var money = matter.Amount!;
        var done = matter.Status == "done";

        var bucket = money.Direction == "in"
            // A refund only counts once it has actually arrived, which for a matter
            // is the user marking it done.
            ? (done ? Bucket.Received : Bucket.Owed)
            : (done ? Bucket.Spent : Bucket.Owed);

        var at = done ? matter.CompletedAt ?? matter.UpdatedAt : matter.DueAt;

        return new PricedEntry(
            matter.Id.ToString(),
            "matter",
            matter.Title,
            matter.Domain,
            money.Currency,
            money.AmountMinor,
            money.Source,
            bucket,
            at,
            Overdue: !done && matter.DueAt is { } due && due < nowUtc);
    }

    private static PricedEntry FromDocument(ScannedDocumentDocument document, DateTime nowUtc)
    {
        var money = document.Amount!;
        var type = document.DocumentType ?? "other";
        var dated = document.AmountDueAt;

        // Rules 2 and 3, side by side. A receipt is the only document that proves
        // by itself that money moved; a bill only tells us money is DUE, and only
        // while its date is still ahead of us.
        var bucket = money.Direction == "in"
            ? Bucket.Received
            : type == "receipt"
                ? Bucket.Spent
                : BillLikeTypes.Contains(type) && dated is { } due && due >= nowUtc
                    ? Bucket.Owed
                    : Bucket.Unknown;

        return new PricedEntry(
            document.Id.ToString(),
            "document",
            document.DocumentTitle ?? document.Issuer ?? "Document",
            Domain: null,
            money.Currency,
            money.AmountMinor,
            money.Source,
            bucket,
            dated ?? document.CreatedAt,
            // An unattached bill is never overdue — see rule 3.
            Overdue: false);
    }

    // ---- Aggregation -------------------------------------------------------

    private static FinanceCurrencyDto BuildCurrency(
        string currency,
        IReadOnlyList<PricedEntry> entries,
        TimeZoneInfo zone,
        int months,
        DateTime windowStart,
        DateTime thisMonth,
        DateTime lastMonth)
    {
        var spent = entries.Where(e => e.Bucket == Bucket.Spent && e.At is not null).ToList();
        var owed = entries.Where(e => e.Bucket == Bucket.Owed).ToList();

        var inWindow = spent.Where(e => e.At >= windowStart).ToList();

        // Every month in the window gets a row, including the empty ones — a trend
        // that silently omits a zero month draws a line between two points that are
        // not adjacent, which reads as continuity that did not happen.
        var monthly = inWindow
            .GroupBy(e => MonthKey(e.At!.Value, zone), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Total: g.Sum(e => e.AmountMinor), Count: g.Count()), StringComparer.Ordinal);

        var byMonth = Enumerable.Range(0, months)
            .Select(offset => MonthKey(StartOfMonth(thisMonth, zone, -(months - 1 - offset)), zone))
            .Select(key => new FinanceMonthDto
            {
                Month = key,
                SpentMinor = monthly.TryGetValue(key, out var cell) ? cell.Total : 0,
                Count = monthly.TryGetValue(key, out var c) ? c.Count : 0,
            })
            .ToList();

        var overdue = owed.Where(e => e.Overdue).ToList();
        var upcoming = owed.Where(e => !e.Overdue).ToList();

        return new FinanceCurrencyDto
        {
            Currency = currency,
            SpentThisMonthMinor = spent.Where(e => e.At >= thisMonth).Sum(e => e.AmountMinor),
            SpentLastMonthMinor = spent.Where(e => e.At >= lastMonth && e.At < thisMonth).Sum(e => e.AmountMinor),
            SpentWindowMinor = inWindow.Sum(e => e.AmountMinor),
            ReceivedWindowMinor = entries
                .Where(e => e.Bucket == Bucket.Received && e.At >= windowStart)
                .Sum(e => e.AmountMinor),
            OverdueMinor = overdue.Sum(e => e.AmountMinor),
            OverdueCount = overdue.Count,
            UpcomingMinor = upcoming.Sum(e => e.AmountMinor),
            UpcomingCount = upcoming.Count,
            ByMonth = byMonth,
            ByDomain = inWindow
                .Where(e => e.Domain is not null)
                .GroupBy(e => e.Domain!, StringComparer.Ordinal)
                .Select(g => new FinanceDomainDto
                {
                    Domain = g.Key,
                    SpentMinor = g.Sum(e => e.AmountMinor),
                    Count = g.Count(),
                })
                .OrderByDescending(d => d.SpentMinor)
                .ToList(),
            Largest = inWindow
                .OrderByDescending(e => e.AmountMinor)
                .Take(ListLength)
                .Select(ToDto)
                .ToList(),
            Upcoming = owed
                // Overdue first, then soonest. An undated obligation sorts last
                // rather than being given a date it does not have.
                .OrderByDescending(e => e.Overdue)
                .ThenBy(e => e.At ?? DateTime.MaxValue)
                .Take(ListLength)
                .Select(ToDto)
                .ToList(),
        };
    }

    private static FinanceEntryDto ToDto(PricedEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        Title = entry.Title,
        Domain = entry.Domain,
        AmountMinor = entry.AmountMinor,
        Source = entry.Source,
        At = entry.At,
        Overdue = entry.Overdue,
    };

    // ---- Calendar ----------------------------------------------------------
    //
    // "August" is a different set of instants in Cairo than in UTC, and a spend at
    // 23:30 on the 31st belongs to the month the user was living in. Everything
    // below converts to the user's wall clock, does calendar arithmetic there, and
    // converts back.

    /// <summary>
    /// A stored zone the host does not know is a bad stored value, not a reason to
    /// fail the request — it falls through to the product default rather than to
    /// UTC, which is the zone the note above this method is warning about.
    /// </summary>
    private static TimeZoneInfo ResolveZone(string? timezone) => AppTimeZone.Resolve(timezone);

    private static string MonthKey(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return $"{local:yyyy-MM}";
    }

    /// <summary>Midnight on the 1st of the month <paramref name="offset"/> months from <paramref name="utc"/>, as UTC.</summary>
    private static DateTime StartOfMonth(DateTime utc, TimeZoneInfo zone, int offset)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var first = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(offset);

        // A DST spring-forward can delete local midnight outright; the next valid
        // instant is the honest boundary and keeps the month non-overlapping.
        if (zone.IsInvalidTime(first)) first = first.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(first, zone);
    }
}
