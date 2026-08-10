using Life_Admin_Autopilot.BLL.Features.Digest;
using Life_Admin_Autopilot.BLL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Features.Digest;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Digest;

/// <summary>
/// The daily-digest slice's one DI extension.
///
/// <para>
/// <c>UserLocaleReader</c> is registered with <c>TryAdd</c> because the Matters
/// slice registers the same type — whichever module the scanner reaches first wins
/// and the other is a no-op. Registering it here as well is what lets this slice
/// stand up on its own.
/// </para>
/// </summary>
public static class DigestFeature
{
    public static IServiceCollection AddDigestFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<DailyDigestRepository>();
        services.TryAddScoped<DailyDigestSourceReader>();
        services.TryAddScoped<DailyDigestComputer>();
        services.TryAddScoped<DailyDigestService>();
        services.TryAddScoped<UserLocaleReader>();

        // Additive registries. The eraser is what puts `dailydigests` into the
        // account-deletion cascade slice K drives; the index provider carries the
        // uniqueness invariant behind the cache upsert and the 7-day TTL.
        services.AddUserDataEraser<DailyDigestEraser>();
        services.AddMongoIndexProvider<DigestIndexes>();

        return services;
    }
}

/// <summary>
/// Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.
/// </summary>
public sealed class DigestModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddDigestFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapDigestEndpoints();
}
