using Life_Admin_Autopilot.BLL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.Account;

/// <summary>
/// Ports <c>routes/me.subscription.ts</c>, <c>routes/me.billing.ts</c> and
/// <c>routes/me.integrations.ts</c>. All three are authenticated, none is rate
/// limited, and none takes a body or a query parameter.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/subscription — the only one of the three that touches the database.
        endpoints.MapGet("/me/subscription", async (
            HttpContext context,
            IAccountProfileRepository profiles,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var user = await profiles.FindByIdAsync(caller.Id, cancellationToken)
                ?? throw AppException.NotFound("user_not_found", "Account no longer exists.");

            // `user.subscription ?? { tier: 'free' }` in Node. The document's
            // Subscription property is non-nullable with a `= new()` initializer, so a
            // row written before the field existed deserializes to exactly that
            // literal — the fallback is structural rather than a branch here.
            return Results.Ok(new SubscriptionResponse
            {
                Subscription = user.Subscription.ToDto(),
            });
        })
        .RequireAuthorization();

        // GET /me/billing/invoices and GET /me/integrations are stubs that never load
        // the user. That is observable, not incidental: after DELETE /me the access
        // token stays cryptographically valid until it expires, and these two keep
        // answering 200 while every user-backed route answers 404 user_not_found.
        // Adding a lookup "for consistency" would break parity.
        endpoints.MapGet("/me/billing/invoices", (HttpContext context) =>
        {
            context.RequireUser();

            return Results.Ok(new InvoicesResponse());
        })
        .RequireAuthorization();

        endpoints.MapGet("/me/integrations", (HttpContext context) =>
        {
            context.RequireUser();

            return Results.Ok(new IntegrationsResponse());
        })
        .RequireAuthorization();

        return endpoints;
    }
}
