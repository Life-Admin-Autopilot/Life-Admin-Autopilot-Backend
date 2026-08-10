using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// The monthly document-scan allowance, on top of the kernel's one quota
/// primitive.
///
/// <para>
/// Monthly rather than daily on purpose: this guards SUSTAINED cost. Bursts are
/// already covered by <c>documentScanLimiter</c> (6/min), and a user who scans
/// eight receipts in one sitting is behaving normally.
/// </para>
///
/// <para>
/// <b>The slot belongs to the DOCUMENT, not to an extraction attempt.</b> That
/// single sentence explains all three of the asymmetries here, each of which
/// looks like a bug in isolation:
/// </para>
/// <list type="bullet">
///   <item><b>Reserved before the storage write</b>, so a full month is refused
///         before any bytes are spent.</item>
///   <item><b>Released only when the upload itself fails</b> after admission —
///         the user got nothing, so they keep the slot.</item>
///   <item><b>Never refunded on delete or reprocess.</b> Refunding on delete would
///         make scan-then-delete an unlimited loop around the cap; charging a
///         retry would bill the user for our failure.</item>
/// </list>
/// </summary>
public interface IDocumentScanQuotaService
{
    Task<UsageQuotaAdmission> TryAdmitAsync(
        ObjectId userId,
        string tier,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(ObjectId userId, CancellationToken cancellationToken = default);

    Task<DocumentScanQuotaDto> ReadAsync(
        ObjectId userId,
        string tier,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDocumentScanQuotaService"/>
public sealed class DocumentScanQuotaService : IDocumentScanQuotaService
{
    private readonly IUsageQuotaStore _store;
    private readonly DocumentScanOptions _options;

    public DocumentScanQuotaService(IUsageQuotaStore store, DocumentScanOptions options)
    {
        _store = store;
        _options = options;
    }

    public Task<UsageQuotaAdmission> TryAdmitAsync(
        ObjectId userId,
        string tier,
        CancellationToken cancellationToken = default) =>
        _store.TryAdmitAsync(Bucket(userId, tier), cancellationToken);

    /// <summary>
    /// The tier is irrelevant to a release — it only picks a ceiling, and the
    /// release is a guarded decrement — so this deliberately does not take one,
    /// mirroring Node's <c>releaseDocumentScanSlot({ userId })</c>.
    /// </summary>
    public Task ReleaseAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        _store.ReleaseAsync(Bucket(userId, "free"), cancellationToken);

    public async Task<DocumentScanQuotaDto> ReadAsync(
        ObjectId userId,
        string tier,
        CancellationToken cancellationToken = default)
    {
        var month = UsageQuotaBuckets.UtcMonth();
        var limit = _options.QuotaFor(tier);
        var used = await _store.ReadUsedAsync(Bucket(userId, tier), cancellationToken).ConfigureAwait(false);

        return new DocumentScanQuotaDto
        {
            Kind = "document_scan",
            Limit = limit,
            Used = used,
            Remaining = Math.Max(0, limit - used),
            ResetAt = UsageQuotaBuckets.NextMonthStartIso(month),
        };
    }

    private UsageQuotaBucket Bucket(ObjectId userId, string tier) => new(
        MongoCollections.DocumentScanUsageCounters,
        userId,
        new Dictionary<string, string> { ["month"] = UsageQuotaBuckets.UtcMonth() },
        _options.QuotaFor(tier));
}
