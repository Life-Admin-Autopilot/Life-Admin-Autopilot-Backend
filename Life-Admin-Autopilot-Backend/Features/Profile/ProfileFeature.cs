using Life_Admin_Autopilot.BLL.Features.Profile;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.Profile;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Profile;

/// <summary>
/// The profile slice's entire DI surface: <c>PATCH /me</c>, <c>DELETE /me</c>,
/// <c>GET /me/export</c>.
///
/// <para>
/// <b>No <c>IUserDataEraser</c> here, and that is the point.</b> This slice owns no
/// collection — it WRITES the <c>users</c> row, which the kernel's own
/// <c>UserProfileEraser</c> already retires at <c>Account</c> order, and it RUNS the
/// cascade rather than contributing to it. Every other collection Node deletes by
/// hand belongs to some other slice, and each registers its own.
/// </para>
///
/// <para>
/// No <c>IMongoIndexProvider</c> either: <c>KernelIndexProvider</c> already covers
/// <c>users</c>, and the export reads other slices' collections without owning any
/// uniqueness invariant on them.
/// </para>
/// </summary>
public static class ProfileFeature
{
    public static IServiceCollection AddProfileFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IProfileRepository, ProfileRepository>();
        services.AddScoped<IAccountExportRepository, AccountExportRepository>();
        services.AddScoped<IAccountExportService, AccountExportService>();

        // TryAdd: the account slice registers the same reader for GET /me/subscription,
        // and the auth slice reads the collection through its own interface. None of
        // the three should clobber the others.
        services.TryAddScoped<IAccountProfileRepository, AccountProfileRepository>();

        // Also TryAdded by the auth slice. Both routes need a clock a test can pin.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
