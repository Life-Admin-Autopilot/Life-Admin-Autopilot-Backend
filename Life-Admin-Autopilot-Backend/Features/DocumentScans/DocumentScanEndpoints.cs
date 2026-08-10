namespace Life_Admin_Autopilot_Backend.Features.DocumentScans;

/// <summary>
/// Ports <c>server/src/routes/me.documentScans.ts</c> — eight operations across
/// five paths.
///
/// <para>
/// <b>Registration order is load-bearing.</b> Express matches path and method
/// together, in registration order, so <c>/me/document-scans/quota</c> has to be
/// declared before <c>/me/document-scans/{id}</c> or the parameterised route
/// swallows the literal segment as an id. ASP.NET's route table already prefers
/// the literal, but the ordering is kept so this file and the Node router read
/// the same way and a reviewer comparing them does not have to hold the
/// difference in their head.
/// </para>
/// </summary>
public static class DocumentScanEndpoints
{
    public static IEndpointRouteBuilder MapDocumentScanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDocumentScanUpload();
        endpoints.MapDocumentScanReads();
        endpoints.MapDocumentScanWrites();

        return endpoints;
    }
}
