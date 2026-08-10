using System.Security.Cryptography;
using System.Text;
using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// Claim → extract → hold everything for review → notify. Port of
/// <c>lib/documentScanWorker.ts</c>.
///
/// <para>
/// There is no auto-save gate, by product decision: every extracted candidate
/// lands in <c>candidates</c> for the user to accept, edit or discard, so
/// confidence is shown on the review card and never decides persistence.
/// </para>
///
/// <para>
/// <b>With no AI provider configured this worker legitimately fails every
/// scan.</b> That is the reference server's behaviour without
/// <c>GEMINI_API_KEY</c>, and the parity target — see
/// <see cref="NullDocumentExtractor"/>.
/// </para>
/// </summary>
internal sealed class DocumentScanWorker : KernelPollingWorker
{
    private readonly Random _jitter = new();

    public DocumentScanWorker(IServiceProvider services, ILogger<DocumentScanWorker> logger)
        : base(services, logger)
    {
    }

    protected override TimeSpan Interval => DocumentScanRetryPolicy.PollInterval;

    protected override string WorkerName => "document-scan";

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();
        var scans = scope.ServiceProvider.GetRequiredService<IScannedDocumentRepository>();

        var claimed = await scans
            .ClaimNextAsync(DateTime.UtcNow, DocumentScanRetryPolicy.Lock, cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return;
        }

        await ProcessAsync(scope.ServiceProvider, scans, claimed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotent on reclaim: a document that already has candidates staged skips
    /// the (expensive) vision call entirely and falls through to notify, so a crash
    /// between extraction and the status write costs nothing but a poll.
    /// </summary>
    private async Task ProcessAsync(
        IServiceProvider services,
        IScannedDocumentRepository scans,
        ScannedDocumentDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            if (document.Candidates.Count == 0)
            {
                document.Attempts += 1;
                document.Status = "processing";
                document.LockedUntil = DateTime.UtcNow.Add(DocumentScanRetryPolicy.Lock);
                await scans.SaveAsync(document, cancellationToken).ConfigureAwait(false);

                var storage = services.GetRequiredService<IDocumentScanStorage>();
                var extractor = services.GetRequiredService<IDocumentExtractor>();
                var bytes = await storage.GetAsync(document.StorageKey, cancellationToken).ConfigureAwait(false);

                var extraction = await extractor
                    .ExtractAsync(
                        new DocumentExtractionRequest(
                            bytes,
                            document.MimeType,
                            document.Timezone,
                            await ReadLocaleAsync(services, document, cancellationToken).ConfigureAwait(false)),
                        cancellationToken)
                    .ConfigureAwait(false);

                document.Candidates = extraction.Candidates
                    .Select((candidate, index) => ToDocument(document.Id.ToString(), index, candidate))
                    .ToList();

                document.DocumentSummary = extraction.DocumentSummary;

                // Row copy for the /documents list, set even when nothing actionable
                // was found: a scan with no candidates still has to name itself in
                // the list.
                document.DocumentType = extraction.DocumentType;
                document.DocumentTitle = extraction.DocumentTitle;
                document.DocumentSubtitle = extraction.DocumentSubtitle;
                document.Issuer = extraction.Issuer;
            }

            document.Status = "ready_for_review";
            document.LockedUntil = null;
            document.LastError = null;
            document.FailureReason = null;
            await scans.SaveAsync(document, cancellationToken).ConfigureAwait(false);

            await NotifyAsync(services, scans, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleFailureAsync(scans, document, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFailureAsync(
        IScannedDocumentRepository scans,
        ScannedDocumentDocument document,
        Exception error,
        CancellationToken cancellationToken)
    {
        Logger.LogError(
            error,
            "documentScan:job-error documentId={DocumentId} attempts={Attempts}",
            document.Id,
            document.Attempts);

        if (DocumentScanRetryPolicy.IsTransient(error) && document.Attempts < document.MaxAttempts)
        {
            document.Status = "pending";
            document.NextRunAt = DateTime.UtcNow.Add(DocumentScanRetryPolicy.Backoff(document.Attempts, _jitter));
            document.LockedUntil = null;
            document.LastError = error.Message;
        }
        else
        {
            document.Status = "failed";

            // Both are set to the same text, and only one of them survives toJSON.
            // failureReason is what the error screen renders; lastError is machinery.
            document.FailureReason = error.Message;
            document.LastError = error.Message;
            document.LockedUntil = null;
        }

        try
        {
            await scans.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveFailure)
        {
            // Save-on-failure is best effort. The lease will expire and the document
            // gets reclaimed; losing this write costs a poll, not the document.
            Logger.LogWarning(saveFailure, "documentScan:failure-save-failed documentId={DocumentId}", document.Id);
        }
    }

    private async Task NotifyAsync(
        IServiceProvider services,
        IScannedDocumentRepository scans,
        ScannedDocumentDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Status != "ready_for_review" || document.NotifiedAt is not null)
        {
            return;
        }

        var notifications = services.GetRequiredService<IDocumentScanNotifications>();

        if (await notifications.WantsPushAsync(document.UserId, cancellationToken).ConfigureAwait(false))
        {
            await notifications
                .CreateReadyAsync(document.UserId, document.Id, document.Candidates.Count, cancellationToken)
                .ConfigureAwait(false);
        }

        document.NotifiedAt = DateTime.UtcNow;

        try
        {
            await scans.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // notifiedAt persistence is best effort — a duplicate notification after
            // a crash is better than losing the review entirely.
            Logger.LogWarning(ex, "documentScan:notified-save-failed documentId={DocumentId}", document.Id);
        }
    }

    private static async Task<string?> ReadLocaleAsync(
        IServiceProvider services,
        ScannedDocumentDocument document,
        CancellationToken cancellationToken)
    {
        // Read at extraction time rather than stamped at upload: a scan can sit in
        // the queue across a language change, and the review card is read after the
        // extraction, not before it.
        var profiles = services.GetRequiredService<IAccountProfileRepository>();
        var user = await profiles.FindByIdAsync(document.UserId, cancellationToken).ConfigureAwait(false);
        return user?.Locale;
    }

    /// <summary>
    /// The document-scoped candidate key. Deterministic in
    /// <c>(documentId, index, normalized title)</c>, so a reclaim that re-runs
    /// extraction produces the SAME keys and the review commit stays idempotent.
    /// </summary>
    private static ExtractedTaskCandidateDocument ToDocument(string documentId, int index, DraftCandidate draft)
    {
        var normalized = string.Join(' ', draft.Title.Trim().ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}|{index}|{normalized}"));
        var key = Convert.ToHexStringLower(hash)[..24];

        return new ExtractedTaskCandidateDocument
        {
            Key = key,
            Title = draft.Title,
            Domain = draft.Domain,
            Priority = draft.Priority,
            Confidence = draft.Confidence,
            Estimate = draft is { EstimateMinMinutes: { } min, EstimateMaxMinutes: { } max }
                ? new TaskEstimateDocument { MinMinutes = min, MaxMinutes = max, Source = "ai" }
                : null,
            DueAt = draft.DueAt,
            Notes = draft.Notes,
            SourcePage = draft.SourcePage,
        };
    }
}
