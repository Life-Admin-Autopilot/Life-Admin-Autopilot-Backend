using Life_Admin_Autopilot.BLL.Features.Finance;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Finance;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Finance;

/// <summary>
/// The four rules in <see cref="FinanceSummaryService"/>, each with a test that
/// fails if it is broken.
///
/// <para>
/// These are about what the summary REFUSES to say. Every assertion here is a
/// number the page would otherwise state confidently and wrongly — a payment
/// counted twice, a debt that does not exist, a spend that never happened. The
/// product's stated biggest risk is one wrong AI-derived value losing trust
/// permanently, and money is the value with the least tolerance for it.
/// </para>
/// </summary>
public sealed class FinanceSummaryServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task a_document_and_the_matter_filed_from_it_are_one_payment()
    {
        // Arrange — the everyday case: a bill was scanned, its candidate accepted,
        // and the user marked the resulting matter done. Both rows carry $142.37.
        var documentId = ObjectId.GenerateNewId();
        var repository = new FakeFinanceRepository
        {
            Matters = { DoneMatter(14237, "USD", sourceDocumentId: documentId, completedAt: Now.AddDays(-2)) },
            Documents = { ReceiptDocument(documentId, 14237, "USD", Now.AddDays(-2)) },
            DocumentCount = 1,
        };

        // Act
        var summary = await Build(repository);

        // Assert — $142.37, not $284.74.
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(14237, usd.SpentThisMonthMinor);
    }

    [Fact]
    public async Task an_unpaid_bill_with_no_matter_is_not_counted_as_spent()
    {
        // Arrange — a bill sitting in the documents list, never filed, already past
        // its due date. It may have been paid at the counter; nothing here knows.
        var repository = new FakeFinanceRepository
        {
            Documents = { BillDocument(ObjectId.GenerateNewId(), 50_000, "USD", dueAt: Now.AddDays(-10)) },
            DocumentCount = 1,
        };

        // Act
        var summary = await Build(repository);

        // Assert — it appears in neither total. Claiming it was spent invents a
        // payment; claiming it is overdue invents a debt.
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(0, usd.SpentWindowMinor);
        Assert.Equal(0, usd.OverdueMinor);
        Assert.Equal(0, usd.OverdueCount);
    }

    [Fact]
    public async Task a_bill_due_in_the_future_is_an_upcoming_obligation()
    {
        // Arrange — same document, dated ahead. Now it IS a real thing to pay.
        var repository = new FakeFinanceRepository
        {
            Documents = { BillDocument(ObjectId.GenerateNewId(), 50_000, "USD", dueAt: Now.AddDays(10)) },
            DocumentCount = 1,
        };

        // Act
        var summary = await Build(repository);

        // Assert
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(50_000, usd.UpcomingMinor);
        Assert.Equal(1, usd.UpcomingCount);
        Assert.Equal(0, usd.SpentWindowMinor);
    }

    [Fact]
    public async Task an_open_matter_past_its_due_date_is_overdue()
    {
        // Arrange — the one row that CAN prove a debt: the user made it and left it open.
        var repository = new FakeFinanceRepository
        {
            Matters =
            {
                OpenMatter(20_000, "USD", dueAt: Now.AddDays(-3)),
                OpenMatter(30_000, "USD", dueAt: Now.AddDays(5)),
            },
        };

        // Act
        var summary = await Build(repository);

        // Assert
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(20_000, usd.OverdueMinor);
        Assert.Equal(1, usd.OverdueCount);
        Assert.Equal(30_000, usd.UpcomingMinor);
        Assert.Equal(1, usd.UpcomingCount);
    }

    [Fact]
    public async Task currencies_are_reported_separately_and_never_added_together()
    {
        // Arrange — there is no exchange-rate source in this product, so a combined
        // total could only be fabricated.
        var repository = new FakeFinanceRepository
        {
            Matters =
            {
                DoneMatter(10_000, "USD", completedAt: Now.AddDays(-1)),
                DoneMatter(500_000, "EGP", completedAt: Now.AddDays(-1)),
            },
        };

        // Act
        var summary = await Build(repository);

        // Assert — two blocks, each true on its own.
        Assert.Equal(2, summary.Currencies.Count);
        Assert.Equal(500_000, summary.Currencies.Single(c => c.Currency == "EGP").SpentThisMonthMinor);
        Assert.Equal(10_000, summary.Currencies.Single(c => c.Currency == "USD").SpentThisMonthMinor);
    }

    [Fact]
    public async Task a_refund_is_reported_but_never_netted_off_spending()
    {
        // Arrange — subtracting the refund would make the headline figure disagree
        // with the sum of the rows the user can see underneath it.
        var repository = new FakeFinanceRepository
        {
            Matters =
            {
                DoneMatter(10_000, "USD", completedAt: Now.AddDays(-1)),
                DoneMatter(4_000, "USD", completedAt: Now.AddDays(-1), direction: "in"),
            },
        };

        // Act
        var summary = await Build(repository);

        // Assert
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(10_000, usd.SpentWindowMinor);
        Assert.Equal(4_000, usd.ReceivedWindowMinor);
    }

    [Fact]
    public async Task coverage_reports_the_documents_the_summary_could_not_read()
    {
        // Arrange — 1 of 40 documents yielded a figure. A total presented without
        // that context reads as "your spending" when it is "what I could see".
        var repository = new FakeFinanceRepository
        {
            Documents = { ReceiptDocument(ObjectId.GenerateNewId(), 1_000, "USD", Now.AddDays(-1)) },
            DocumentCount = 40,
        };

        // Act
        var summary = await Build(repository);

        // Assert
        Assert.Equal(40, summary.Coverage.DocumentsTotal);
        Assert.Equal(1, summary.Coverage.DocumentsWithAmount);
    }

    [Fact]
    public async Task the_trend_includes_months_with_no_spending()
    {
        // Arrange — one spend, six-month window. A trend that omitted the empty
        // months would draw a line between two points that are not adjacent.
        var repository = new FakeFinanceRepository
        {
            Matters = { DoneMatter(10_000, "USD", completedAt: Now.AddDays(-1)) },
        };

        // Act
        var summary = await Build(repository, months: 6);

        // Assert — six rows, ending on the current month, oldest first.
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(6, usd.ByMonth.Count);
        Assert.Equal("2026-08", usd.ByMonth[^1].Month);
        Assert.Equal("2026-03", usd.ByMonth[0].Month);
        Assert.Equal(10_000, usd.ByMonth[^1].SpentMinor);
        Assert.Equal(0, usd.ByMonth[0].SpentMinor);
    }

    [Fact]
    public async Task months_are_bucketed_in_the_users_timezone_not_utc()
    {
        // Arrange — 22:30 UTC on 31 July is 01:30 on 1 AUGUST in Cairo (UTC+3).
        // The spend belongs to the month the user was living in.
        var repository = new FakeFinanceRepository
        {
            Matters =
            {
                DoneMatter(10_000, "USD", completedAt: new DateTime(2026, 7, 31, 22, 30, 0, DateTimeKind.Utc)),
            },
        };

        // Act
        var summary = await Build(repository, timezone: "Africa/Cairo");

        // Assert
        var usd = Assert.Single(summary.Currencies);
        Assert.Equal(10_000, usd.ByMonth.Single(m => m.Month == "2026-08").SpentMinor);
        Assert.Equal(0, usd.ByMonth.Single(m => m.Month == "2026-07").SpentMinor);
    }

    [Fact]
    public async Task an_unknown_timezone_falls_back_to_utc_rather_than_failing()
    {
        // Arrange — a bad stored value is not a reason to deny the user their page.
        var repository = new FakeFinanceRepository
        {
            Matters = { DoneMatter(10_000, "USD", completedAt: Now.AddDays(-1)) },
        };

        // Act
        var summary = await Build(repository, timezone: "Mars/Olympus_Mons");

        // Assert
        Assert.Equal("UTC", summary.Timezone);
        Assert.Equal(10_000, Assert.Single(summary.Currencies).SpentThisMonthMinor);
    }

    [Fact]
    public async Task an_account_with_no_money_anywhere_reports_no_currencies()
    {
        // Arrange — the empty state, which is what most accounts look like on day one.
        var summary = await Build(new FakeFinanceRepository { DocumentCount = 12 });

        // Assert — an empty list, not a zero-filled fake currency block.
        Assert.Empty(summary.Currencies);
        Assert.Equal(12, summary.Coverage.DocumentsTotal);
    }

    // ---- Builders ----------------------------------------------------------

    private static Task<FinanceSummaryDto> Build(
        FakeFinanceRepository repository,
        int months = 6,
        string? timezone = "UTC") =>
        new FinanceSummaryService(repository).BuildAsync(ObjectId.GenerateNewId(), months, timezone, Now);

    private static MoneyDocument Money(long minor, string currency, string direction = "out") =>
        new() { AmountMinor = minor, Currency = currency, Source = "ai", Direction = direction };

    private static TaskDocument DoneMatter(
        long minor,
        string currency,
        DateTime completedAt,
        ObjectId? sourceDocumentId = null,
        string direction = "out") => new()
    {
        Id = ObjectId.GenerateNewId(),
        Title = "Paid something",
        Domain = "home",
        Status = "done",
        CompletedAt = completedAt,
        UpdatedAt = completedAt,
        SourceDocumentId = sourceDocumentId,
        Amount = Money(minor, currency, direction),
    };

    private static TaskDocument OpenMatter(long minor, string currency, DateTime? dueAt) => new()
    {
        Id = ObjectId.GenerateNewId(),
        Title = "Owe something",
        Domain = "home",
        Status = "open",
        DueAt = dueAt,
        UpdatedAt = Now,
        Amount = Money(minor, currency),
    };

    private static ScannedDocumentDocument ReceiptDocument(
        ObjectId id,
        long minor,
        string currency,
        DateTime at) => new()
    {
        Id = id,
        DocumentType = "receipt",
        DocumentTitle = "Receipt",
        Amount = Money(minor, currency),
        AmountDueAt = at,
        CreatedAt = at,
    };

    private static ScannedDocumentDocument BillDocument(
        ObjectId id,
        long minor,
        string currency,
        DateTime dueAt) => new()
    {
        Id = id,
        DocumentType = "bill",
        DocumentTitle = "Bill",
        Amount = Money(minor, currency),
        AmountDueAt = dueAt,
        CreatedAt = Now.AddDays(-30),
    };

    /// <summary>
    /// In-memory stand-in. The service is pure over what the repository returns,
    /// so every rule above is testable without a database — which is why these run
    /// in milliseconds and cannot flake on a dirty collection.
    /// </summary>
    private sealed class FakeFinanceRepository : IFinanceRepository
    {
        public List<TaskDocument> Matters { get; } = new();

        public List<ScannedDocumentDocument> Documents { get; } = new();

        public long DocumentCount { get; init; }

        public Task<IReadOnlyList<TaskDocument>> ListPricedMattersAsync(
            ObjectId userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskDocument>>(Matters);

        public Task<IReadOnlyList<ScannedDocumentDocument>> ListPricedDocumentsAsync(
            ObjectId userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScannedDocumentDocument>>(Documents);

        public Task<long> CountDocumentsAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(DocumentCount);
    }
}
