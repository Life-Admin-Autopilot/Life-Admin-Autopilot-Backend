using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.Tasks;

/// <summary>
/// Exposes the read-only endpoint GET /me/financial-insights with bearer token auth.
/// </summary>
internal static class FinancialInsightsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me/financial-insights", async (
            HttpContext ctx,
            FinancialInsightsService service,
            CancellationToken ct) =>
        {
            var user = ctx.RequireUser();

            var insights = await service.ComputeAsync(user.Id, null, ct).ConfigureAwait(false);

            return Results.Ok(insights);
        }).RequireAuthorization();
    }
}
