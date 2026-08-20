using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Time;

namespace Life_Admin_Autopilot.BLL.Features.Finance;

/// <summary>One calendar month of spending, in the user's own timezone.</summary>
public sealed class FinanceMonthDto
{
    /// <summary><c>YYYY-MM</c>. A label, not a timestamp — the client never re-parses it into a date.</summary>
    [JsonPropertyName("month")]
    public string Month { get; init; } = string.Empty;

    [JsonPropertyName("spentMinor")]
    public long SpentMinor { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary>Spending under one life domain, for the breakdown.</summary>
public sealed class FinanceDomainDto
{
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("spentMinor")]
    public long SpentMinor { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary>
/// One priced thing, as it appears in the "largest" and "upcoming" lists.
///
/// <para>
/// <c>kind</c> tells the client which surface to open: a <c>matter</c> routes to
/// the matter sheet, a <c>document</c> to the document viewer. Without it the
/// client would have to guess from the id, and the two id spaces look identical.
/// </para>
/// </summary>
public sealed class FinanceEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary><c>matter</c> or <c>document</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "matter";

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Domain { get; init; }

    [JsonPropertyName("amountMinor")]
    public long AmountMinor { get; init; }

    /// <summary>
    /// <c>ai</c> or <c>user</c>. Carried per entry, not per response, because one
    /// list mixes both — and the CitationChip is owed to the figures the reader
    /// did not type themselves.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "ai";

    /// <summary>
    /// When it happened (spent) or falls due (upcoming). Null on an undated
    /// obligation, which the client sorts last rather than inventing a date for.
    /// </summary>
    [JsonPropertyName("at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? At { get; init; }

    /// <summary>Set on upcoming entries whose due date has already passed.</summary>
    [JsonPropertyName("overdue")]
    public bool Overdue { get; init; }
}

/// <summary>
/// Everything known about ONE currency.
///
/// <para>
/// <b>Currencies are never combined.</b> There is no exchange-rate source in this
/// product, and inventing one would put a fabricated number at the top of a page
/// whose entire job is to be checkable against paper. A user with EGP and USD
/// documents gets two blocks and a true total in each, rather than one total that
/// is true in neither.
/// </para>
/// </summary>
public sealed class FinanceCurrencyDto
{
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("spentThisMonthMinor")]
    public long SpentThisMonthMinor { get; init; }

    [JsonPropertyName("spentLastMonthMinor")]
    public long SpentLastMonthMinor { get; init; }

    [JsonPropertyName("spentWindowMinor")]
    public long SpentWindowMinor { get; init; }

    /// <summary>
    /// Refunds and rebates over the window. Kept separate rather than netted off
    /// spending: subtracting them would make the headline figure disagree with
    /// the sum of the rows the user can see.
    /// </summary>
    [JsonPropertyName("receivedWindowMinor")]
    public long ReceivedWindowMinor { get; init; }

    [JsonPropertyName("overdueMinor")]
    public long OverdueMinor { get; init; }

    [JsonPropertyName("overdueCount")]
    public int OverdueCount { get; init; }

    [JsonPropertyName("upcomingMinor")]
    public long UpcomingMinor { get; init; }

    [JsonPropertyName("upcomingCount")]
    public int UpcomingCount { get; init; }

    [JsonPropertyName("byMonth")]
    public IReadOnlyList<FinanceMonthDto> ByMonth { get; init; } = Array.Empty<FinanceMonthDto>();

    [JsonPropertyName("byDomain")]
    public IReadOnlyList<FinanceDomainDto> ByDomain { get; init; } = Array.Empty<FinanceDomainDto>();

    [JsonPropertyName("largest")]
    public IReadOnlyList<FinanceEntryDto> Largest { get; init; } = Array.Empty<FinanceEntryDto>();

    [JsonPropertyName("upcoming")]
    public IReadOnlyList<FinanceEntryDto> Upcoming { get; init; } = Array.Empty<FinanceEntryDto>();
}

/// <summary>
/// What the summary could NOT see.
///
/// <para>
/// This is the most important object in the response and the reason the page can
/// be trusted. Every total here is built from the documents a vision pass
/// happened to find a figure on; presenting that as "your spending" would be a
/// confident claim about money the system never saw. The client renders this as
/// a plain sentence next to the totals.
/// </para>
/// </summary>
public sealed class FinanceCoverageDto
{
    [JsonPropertyName("documentsTotal")]
    public long DocumentsTotal { get; init; }

    [JsonPropertyName("documentsWithAmount")]
    public int DocumentsWithAmount { get; init; }

    [JsonPropertyName("mattersWithAmount")]
    public int MattersWithAmount { get; init; }
}

public sealed class FinanceSummaryDto
{
    /// <summary>Months of history the window covers, echoed back so the client never assumes it.</summary>
    [JsonPropertyName("months")]
    public int Months { get; init; }

    /// <summary>
    /// The IANA zone the month boundaries were computed in. Echoed because
    /// "August" is a different set of days in Cairo than in UTC, and a client
    /// re-bucketing on its own would silently disagree.
    /// </summary>
    [JsonPropertyName("timezone")]
    public string Timezone { get; init; } = AppTimeZone.DefaultId;

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; init; }

    /// <summary>Busiest currency first, so the client's default block is the one that matters.</summary>
    [JsonPropertyName("currencies")]
    public IReadOnlyList<FinanceCurrencyDto> Currencies { get; init; } = Array.Empty<FinanceCurrencyDto>();

    [JsonPropertyName("coverage")]
    public FinanceCoverageDto Coverage { get; init; } = new();
}

public sealed class FinanceSummaryResponse
{
    [JsonPropertyName("finance")]
    public FinanceSummaryDto Finance { get; init; } = new();
}
