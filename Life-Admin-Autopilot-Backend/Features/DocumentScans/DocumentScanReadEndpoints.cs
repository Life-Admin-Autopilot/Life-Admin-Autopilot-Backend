using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// The four read routes: list, quota meter, one scan, and the original bytes.
/// None is rate limited in Node.
/// </summary>
public static class DocumentScanReadEndpoints
{
    public const string NotFoundCode = "scanned_document_not_found";
    public const string NotFoundMessage = "Scanned document no longer exists.";

    public static IEndpointRouteBuilder MapDocumentScanReads(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/document-scans — a hard-coded 50, newest first. No query
        // parameters, no pagination, no total.
        endpoints.MapGet("/me/document-scans", async (
            HttpContext context,
            IScannedDocumentRepository scans,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var documents = await scans.ListForUserAsync(caller.Id, cancellationToken);

            return Results.Ok(new ScanListResponse
            {
                ScannedDocuments = documents.Select(d => d.ToDto()).ToList(),
            });
        })
        .RequireAuthorization();

        // GET /me/document-scans/quota — MUST be mapped before /{id}. Express
        // matches in registration order, so the parameterised route would otherwise
        // swallow the literal segment "quota" as an id and 404. ASP.NET's route
        // table prefers the literal segment anyway, but the ordering is kept
        // explicit so the two files read the same way.
        endpoints.MapGet("/me/document-scans/quota", async (
            HttpContext context,
            IAccountProfileRepository profiles,
            IDocumentScanQuotaService quota,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var user = await profiles.FindByIdAsync(caller.Id, cancellationToken)
                ?? throw AppException.NotFound("user_not_found", "Account no longer exists.");

            // The REAL tier here, unlike the upload route's hard-coded 'free'.
            var tier = string.IsNullOrEmpty(user.Subscription.Tier) ? "free" : user.Subscription.Tier;
            var meter = await quota.ReadAsync(caller.Id, tier, cancellationToken);

            return Results.Ok(new ScanQuotaResponse { Tier = tier, Quota = meter });
        })
        .RequireAuthorization();

        endpoints.MapGet("/me/document-scans/{id}", async (
            HttpContext context,
            string id,
            IScannedDocumentRepository scans,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var document = await FindOrThrowAsync(scans, id, caller.Id, cancellationToken);

            return Results.Ok(new ScanSingleResponse { ScannedDocument = document.ToDto() });
        })
        .RequireAuthorization();

        // GET /me/document-scans/{id}/file — the ONLY place storageKey is read back
        // out. Success is a raw binary body; ERRORS ARE STILL JSON, which is why
        // the lookup happens before a single response header is set.
        endpoints.MapGet("/me/document-scans/{id}/file", async (
            HttpContext context,
            string id,
            IScannedDocumentRepository scans,
            IDocumentScanStorage storage,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var document = await FindOrThrowAsync(scans, id, caller.Id, cancellationToken);

            // A missing file throws, and that becomes a 500 internal_error. A row
            // pointing at bytes that are gone is a server fault, not a 404 the
            // client could act on.
            var bytes = await storage.GetAsync(document.StorageKey, cancellationToken);

            // `inline` with no filename parameter, matching res.setHeader exactly.
            context.Response.Headers.ContentDisposition = "inline";
            context.Response.Headers.CacheControl = "private, max-age=86400";

            return Results.Bytes(bytes, document.MimeType);
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// Owner-scoped lookup. A malformed id throws <c>ObjectIdCastException</c>
    /// first, which the kernel renders as the global 404 <c>not_found</c> — a
    /// DIFFERENT body from this route's own <c>scanned_document_not_found</c>, and
    /// both are part of the contract.
    /// </summary>
    internal static async Task<ScannedDocumentDocument> FindOrThrowAsync(
        IScannedDocumentRepository scans,
        string id,
        MongoDB.Bson.ObjectId userId,
        CancellationToken cancellationToken) =>
        await scans.FindForUserAsync(
            MongoRepositoryBase<ScannedDocumentDocument>.ParseObjectId(id),
            userId,
            cancellationToken)
        ?? throw AppException.NotFound(NotFoundCode, NotFoundMessage);
}
