using Life_Admin_Autopilot.BLL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Features.IcsFeeds;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.IcsFeeds;

/// <summary>
/// The ICS slice's entire DI surface, so it stays greppable.
/// </summary>
public static class IcsFeedsFeature
{
    public static IServiceCollection AddIcsFeedsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<IcsFeedRepository>();
        services.TryAddScoped<IcsMatterRepository>();
        services.TryAddScoped<IcsImportContextReader>();
        services.TryAddScoped<IcsMatterReconciler>();
        services.TryAddScoped<IcsFeedSyncService>();
        services.TryAddScoped<FeedFetcher>();

        // The DNS seam. Singleton because it holds no state and the resolver is
        // process-wide anyway; a test replaces it to drive the SSRF rules without a
        // network.
        services.TryAddSingleton<IFeedDnsResolver, SystemFeedDnsResolver>();
        services.TryAddScoped<FeedUrlGuard>();

        // ---------------------------------------------------------------------
        // THE SINGLE MOST IMPORTANT LINE IN THIS FILE.
        //
        // `AllowAutoRedirect = false`. The SSRF guard vets the hostname before the
        // first request, but a publisher URL that passes can still 302 into private
        // space — so every hop is re-vetted by hand in FeedFetcher. An
        // auto-following handler would chase the redirect inside the transport, with
        // no opportunity to look, and defeat the entire guard silently. Anything that
        // turns this back on re-opens the metadata-endpoint hole.
        //
        // UseCookies is off for the same family of reasons: a feed is a public
        // bearer URL and must never accumulate or replay ambient credentials.
        // ---------------------------------------------------------------------
        services
            .AddHttpClient(FeedFetcherOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                Credentials = null,
                ConnectTimeout = FeedFetcher.Timeout,
            });

        // Additive registries — adding to them never touches another slice's file.
        services.AddUserDataEraser<IcsFeedEraser>();
        services.AddMongoIndexProvider<IcsFeedIndexes>();
        services.AddKernelWorker<IcsFeedWorker>();

        return services;
    }
}

/// <summary>
/// Found by the kernel's assembly scanner — no <c>Program.cs</c> edit, which is how
/// several slices land in parallel without touching a shared file.
/// </summary>
public sealed class IcsFeedsModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddIcsFeedsFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapIcsFeedEndpoints();
}
