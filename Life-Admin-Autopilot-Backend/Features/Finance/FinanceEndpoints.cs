using Life_Admin_Autopilot.BLL.Features.Finance;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.Finance;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Finance;

/// <summary>
/// One read route. The slice writes nothing — every figure it reports was stored
/// by the document-scan pass or typed on a matter, so there is no path here that
/// can change what the user owes.
/// </summary>
public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/finance/summary?months=6
        endpoints.MapGet("/me/finance/summary", async (
            HttpContext context,
            IAccountProfileRepository profiles,
            FinanceSummaryService summaries,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var months = ReadMonths(context.Request.Query["months"]);

            // The zone the user's months are measured in. A missing profile is a
            // 404 rather than a UTC fallback: every other /me route treats a caller
            // whose account has gone as not-found, and a summary is not the place
            // to invent an exception to that.
            var user = await profiles.FindByIdAsync(caller.Id, cancellationToken)
                ?? throw AppException.NotFound("user_not_found", "Account no longer exists.");

            var summary = await summaries.BuildAsync(
                caller.Id,
                months,
                user.Timezone,
                DateTime.UtcNow,
                cancellationToken);

            return Results.Ok(new FinanceSummaryResponse { Finance = summary });
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// A junk <c>months</c> falls back to the default rather than 400ing. The
    /// parameter only widens or narrows a view — there is no request a bad value
    /// makes dangerous, and refusing the whole summary over it would trade a
    /// working page for a pedantic error. Range clamping happens in the service,
    /// which is also what a caller omitting the parameter gets.
    /// </summary>
    private static int ReadMonths(string? raw) =>
        int.TryParse(raw, out var parsed) ? parsed : FinanceSummaryService.DefaultMonths;
}

/// <summary>The slice's DI surface, in one place.</summary>
public static class FinanceFeature
{
    public static IServiceCollection AddFinanceFeature(this IServiceCollection services)
    {
        services.TryAddScoped<IFinanceRepository, FinanceRepository>();
        services.TryAddScoped<FinanceSummaryService>();
        services.AddMongoIndexProvider<FinanceIndexes>();

        // No IUserDataEraser: this slice owns no collection of its own. The
        // amounts live on matters and scans, and those slices' erasers already
        // take them with the rows they hang off.
        return services;
    }
}

/// <summary>Found by the kernel's assembly scanner — no Program.cs edit.</summary>
public sealed class FinanceModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddFinanceFeature();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapFinanceEndpoints();
}
