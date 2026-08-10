using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.DocumentScans.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// The three mutating routes: delete, reprocess, and the review commit.
/// </summary>
public static class DocumentScanWriteEndpoints
{
    public static IEndpointRouteBuilder MapDocumentScanWrites(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/me/document-scans/{id}", async (
            HttpContext context,
            string id,
            IScannedDocumentRepository scans,
            IDocumentScanNotifications notifications,
            IDocumentScanStorage storage,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // Still throws the global CastError 404 for a malformed id: the parse
            // happens inside the lookup, before the idempotent branch below.
            var scanId = Life_Admin_Autopilot.DAL.Kernel.Mongo
                .MongoRepositoryBase<ScannedDocumentDocument>.ParseObjectId(id);

            var document = await scans.FindForUserAsync(scanId, caller.Id, cancellationToken);

            // IDEMPOTENT, unlike the ICS and Google deletes which 404 on a second
            // call. A double-tap — or a retry of a request that actually
            // succeeded — is a no-op success, not an error the client has to
            // special-case while deleting several at once.
            if (document is null)
            {
                return Results.NoContent();
            }

            var storageKey = document.StorageKey;

            // Record first, bytes second. The reverse order can leave a row pointing
            // at a file that no longer exists, so opening the document 500s while it
            // still sits in the list — strictly worse than leaking a file on disk.
            await scans.DeleteAsync(document.Id, cancellationToken);
            await notifications.DeleteForDocumentAsync(caller.Id, document.Id, cancellationToken);

            try
            {
                await storage.RemoveAsync(storageKey, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                loggerFactory
                    .CreateLogger("documentScan")
                    .LogWarning(ex, "documentScan:delete-storage-failed storageKey={StorageKey}", storageKey);
            }

            // The quota slot is deliberately NOT released. The extraction this
            // document paid for has already happened; refunding here would make
            // scan-then-delete an unlimited loop around the monthly cap.
            return Results.NoContent();
        })
        .RequireAuthorization();

        endpoints.MapPost("/me/document-scans/{id}/reprocess", async (
            HttpContext context,
            string id,
            IScannedDocumentRepository scans,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var document = await DocumentScanReadEndpoints
                .FindOrThrowAsync(scans, id, caller.Id, cancellationToken);

            // 200, NOT 409, and NOT 202. The client polls every 4s, so a retry
            // tapped in the window where the worker already recovered has to read
            // as success — a conflict there would show the user an error about a
            // document that is fine. The status code is the whole signal: 200 means
            // "nothing to do", 202 means "re-queued".
            if (document.Status != "failed")
            {
                return Results.Ok(new ScanSingleResponse { ScannedDocument = document.ToDto() });
            }

            if (document.ManualRetries >= ScannedDocumentVocabulary.MaxManualScanRetries)
            {
                throw AppException.BadRequest(
                    "document_scan_retry_exhausted",
                    "This document has failed too many times to keep retrying. Try scanning it again.");
            }

            document.ManualRetries += 1;

            // attempts resets so the transient-error backoff ladder starts fresh. A
            // document that exhausted maxAttempts during an outage an hour ago
            // should get a full ladder now, not one last try spent on whatever the
            // first request happens to hit.
            document.Attempts = 0;
            document.Status = "pending";
            document.NextRunAt = DateTime.UtcNow;
            document.LockedUntil = null;
            document.LastError = null;

            // Cleared, and therefore GONE from the response — failureReason is the
            // one job-adjacent field the transform does not strip.
            document.FailureReason = null;

            await scans.SaveAsync(document, cancellationToken);

            // The monthly quota is NOT charged again: the slot belongs to the
            // document, not to an extraction attempt. manualRetries is what bounds
            // the cost instead.
            return Results.Json(
                new ScanSingleResponse { ScannedDocument = document.ToDto() },
                statusCode: StatusCodes.Status202Accepted);
        })
        .RequireAuthorization()
        .RateLimited(KernelRateLimiters.DocumentScan);

        endpoints.MapPost("/me/document-scans/{id}/review", async (
            HttpContext context,
            string id,
            IScannedDocumentRepository scans,
            IDocumentScanReviewService review,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var document = await DocumentScanReadEndpoints
                .FindOrThrowAsync(scans, id, caller.Id, cancellationToken);

            // The status gate runs BEFORE body validation, so a malformed body on a
            // not-ready document reports scan_not_ready rather than invalid_review.
            if (document.Status != "ready_for_review")
            {
                throw AppException.BadRequest("scan_not_ready", "This scan is not ready for review yet.");
            }

            var body = await ScanReviewBinder.ReadAsync(context, cancellationToken);

            var held = document.Candidates.ToDictionary(c => c.Key, StringComparer.Ordinal);
            var accepted = new List<ExtractedTaskCandidateDocument>();

            foreach (var accept in body.Accepts)
            {
                // An unknown key is stale or already handled. Ignored idempotently
                // rather than rejected — the review card can be committed twice.
                if (!held.TryGetValue(accept.Key, out var candidate))
                {
                    continue;
                }

                accepted.Add(new ExtractedTaskCandidateDocument
                {
                    Key = candidate.Key,
                    Title = accept.Title ?? candidate.Title,
                    Domain = accept.Domain ?? candidate.Domain,
                    Priority = accept.Priority ?? candidate.Priority,

                    // confidence, estimate and sourcePage are ALWAYS carried, never
                    // supplied by the caller: the estimate came from the vision pass
                    // that actually read the document, and nothing at accept time
                    // knows more than it did.
                    Confidence = candidate.Confidence,
                    Estimate = candidate.Estimate,
                    SourcePage = candidate.SourcePage,

                    DueAt = accept.DueAt ?? candidate.DueAt,
                    Notes = accept.Notes ?? candidate.Notes,
                });
            }

            var created = await review.PersistAsync(caller.Id, document.Id, accepted, cancellationToken);

            var idByKey = created
                .Where(t => t.SourceTaskKey is not null)
                .ToDictionary(t => t.SourceTaskKey!, t => t.Id, StringComparer.Ordinal);

            foreach (var record in accepted)
            {
                record.TaskId = idByKey.TryGetValue(record.Key, out var taskId) ? taskId : null;
            }

            var handled = new HashSet<string>(
                accepted.Select(a => a.Key).Concat(body.Discards),
                StringComparer.Ordinal);

            document.Candidates = document.Candidates
                .Where(c => !handled.Contains(c.Key))
                .Concat(accepted)
                .ToList();

            // Stamped once nothing is left un-filed. Discards count as handled, so a
            // pass that discards everything closes the review just as an accept-all
            // would.
            if (document.Candidates.All(c => c.TaskId is not null))
            {
                document.ReviewedAt = DateTime.UtcNow;
            }

            await scans.SaveAsync(document, cancellationToken);

            return Results.Ok(new ScanReviewResponse
            {
                Tasks = created.Select(t => t.ToDto()).ToList(),
                ScannedDocument = document.ToDto(),
            });
        })
        .RequireAuthorization();

        return endpoints;
    }
}
