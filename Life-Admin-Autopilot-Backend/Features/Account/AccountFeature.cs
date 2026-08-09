using Life_Admin_Autopilot.DAL.Features.Account;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Account;

/// <summary>
/// The Account slice's entire DI surface: <c>GET /me/subscription</c>,
/// <c>GET /me/billing/invoices</c>, <c>GET /me/integrations</c>.
/// (<c>GET /health</c> is the kernel's own smoke test and is not registered here.)
///
/// <para>
/// Nothing else to add. The slice is read-only and owns no collection, so there is
/// no <c>IUserDataEraser</c>, no <c>IMongoIndexProvider</c> and no worker — see the
/// note on <see cref="IAccountProfileRepository"/>. None of these routes is rate
/// limited in Node either.
/// </para>
/// </summary>
public static class AccountFeature
{
    public static IServiceCollection AddAccountFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // TryAdd: the auth slice reads the same collection through its own
        // interface, and neither registration should clobber the other.
        services.TryAddScoped<IAccountProfileRepository, AccountProfileRepository>();

        return services;
    }
}
