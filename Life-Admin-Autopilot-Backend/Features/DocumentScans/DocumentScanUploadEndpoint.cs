using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.DocumentScans.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// <c>POST /me/document-scans</c> — the raw-bytes upload.
///
/// <para>
/// <b>The body is the file.</b> There is no multipart form, no field name and no
/// multipart handling anywhere in this stack; the metadata rides on
/// <c>x-document-scan-*</c> headers precisely so the request can stay a single
/// binary body. The <c>Content-Type</c> IS the file's own type.
/// </para>
/// </summary>
public static class DocumentScanUploadEndpoint
{
    /// <summary>
    /// Drives BOTH the parser's type filter and the handler's own content-type
    /// check in Node, which is why one list serves both here too.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedMime = new[]
    {
        "application/pdf", "image/jpeg", "image/png", "image/heic", "image/webp",
    };

    public static IEndpointRouteBuilder MapDocumentScanUpload(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/me/document-scans", async (
            HttpContext context,
            IScannedDocumentRepository scans,
            IDocumentScanStorage storage,
            IDocumentScanQuotaService quota,
            DocumentScanOptions options,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var contentType = ReadContentType(context.Request);

            // express.raw({ type: ALLOWED_MIME }) only populates the body for a
            // matching content type. For anything else req.body is never a Buffer,
            // so the handler's FIRST check fires and the caller sees `empty_body` —
            // not `unsupported_media_type`. Verified live with `text/plain`.
            if (!AllowedMime.Contains(contentType, StringComparer.Ordinal))
            {
                throw AppException.BadRequest("empty_body", "No document payload received.");
            }

            var upload = await RawBodyReader.ReadAsync(
                context,
                options.MaxBytes,
                emptyMessage: "No document payload received.",
                tooLargeMessage: $"Document exceeds {options.MaxMegabytes}MB.",
                cancellationToken: cancellationToken);

            // UNREACHABLE DEAD CODE, ported on purpose. The gate above already
            // rejected every foreign content type as `empty_body`, so nothing can
            // arrive here with a type outside the list — exactly as in Node, where
            // raw()'s type filter makes the same branch unreachable. It is kept so
            // the two servers stay line-for-line comparable, and so a future
            // transport that DOES deliver a foreign body still gets the right code.
            if (upload.ContentType is null || !AllowedMime.Contains(upload.ContentType, StringComparer.Ordinal))
            {
                throw AppException.BadRequest(
                    "unsupported_media_type",
                    "Only PDF, JPEG, PNG, HEIC, or WebP are supported.");
            }

            var metadata = ScanMetadataBinder.Read(context.Request);

            // The page cap is enforced BEFORE the storage write: a PDF's page count
            // is only knowable after a parse, but that parse is cheap and does not
            // render, so it is worth doing before spending a write and a quota slot.
            var pageCount = contentType == "application/pdf" ? PdfPageCounter.Count(upload.Bytes) : 1;
            if (pageCount > options.MaxPages)
            {
                throw AppException.BadRequest(
                    "document_too_many_pages",
                    $"PDF has {pageCount} pages, exceeding the {options.MaxPages}-page limit.");
            }

            // EVERY USER IS ADMITTED AS FREE TIER. Hard-coded in the Node route
            // (`const tier = 'free' as const`) regardless of the real subscription,
            // while GET /me/document-scans/quota reads the real tier. The asymmetry
            // is reproduced deliberately, not overlooked: "fixing" it here would
            // give a pro user a 200-scan month the reference server refuses at 20.
            const string admissionTier = "free";

            var admission = await quota.TryAdmitAsync(caller.Id, admissionTier, cancellationToken);
            if (!admission.Admitted)
            {
                throw AppException.PaymentRequired(
                    "document_scan_quota_exceeded",
                    $"You've hit this month's limit of {admission.Limit} document scans.",
                    new DocumentScanQuotaExceededDetails
                    {
                        Tier = admissionTier,
                        Limit = admission.Limit,
                        Used = admission.Used,
                    });
            }

            try
            {
                var id = ObjectId.GenerateNewId();
                var key = DocumentScanStorageKeys.Build(caller.IdString, id.ToString(), contentType);

                // Bytes first, row second. A processing failure then never costs the
                // user their capture — a retry is a worker re-enqueue, not a photo
                // they have to take again.
                await storage.PutAsync(key, upload.Bytes, cancellationToken);

                var now = DateTime.UtcNow;
                var document = new ScannedDocumentDocument
                {
                    Id = id,
                    UserId = caller.Id,
                    StorageKey = key,
                    MimeType = contentType,
                    SourceType = metadata.Source,
                    PageCount = pageCount,
                    ByteSize = upload.Bytes.Length,
                    Status = "pending",
                    ClientCapturedAt = metadata.CapturedAt,
                    Timezone = metadata.Timezone,
                    NextRunAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await scans.InsertAsync(document, cancellationToken);

                return Results.Json(
                    new ScanSingleResponse { ScannedDocument = document.ToDto() },
                    statusCode: StatusCodes.Status202Accepted);
            }
            catch
            {
                // The slot is handed back ONLY here: the user got nothing, so they
                // keep their allowance. Delete and reprocess deliberately do not
                // refund — see DocumentScanQuotaService.
                await quota.ReleaseAsync(caller.Id, cancellationToken);
                throw;
            }
        })
        .RequireAuthorization()
        .RateLimited(KernelRateLimiters.DocumentScan);

        return endpoints;
    }

    /// <summary>
    /// Node: <c>(req.header('content-type') ?? '').split(';')[0]?.trim()</c> — the
    /// parameters after the semicolon are dropped, so
    /// <c>application/pdf; charset=binary</c> still matches.
    /// </summary>
    private static string ReadContentType(HttpRequest request) =>
        request.Headers.ContentType.ToString().Split(';')[0].Trim();
}
