using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Hosting;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.GoogleIntegration;

/// <summary>
/// Discovered by <c>EndpointModuleScanner</c>. No <c>Program.cs</c> edit.
/// </summary>
public sealed class GoogleIntegrationModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddGoogleIntegrationFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGoogleIntegrationEndpoints();
}

/// <summary>
/// The Google slice's entire DI surface.
/// </summary>
public static class GoogleIntegrationFeature
{
    public static IServiceCollection AddGoogleIntegrationFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Resolved once, but LAZILY — from the container's own IConfiguration rather
        // than the instance handed to AddServices.
        //
        // Not a style choice. Under WebApplicationFactory the test fixture's
        // in-memory configuration source is added to the host AFTER the entry point
        // has run, so an eager `Read(configuration)` here sees none of it: the slice
        // came up with an empty JWT secret and every signed OAuth state failed to
        // verify against itself. Caught by the callback tests. The kernel's own
        // options avoid this by going through Bind(), which is lazy for the same
        // reason.
        services.AddSingleton(sp => GoogleIntegrationOptions.Read(sp.GetRequiredService<IConfiguration>()));

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IGoogleTokenCipher, GoogleTokenCipher>();
        services.AddSingleton<IGoogleOAuthState, GoogleOAuthState>();
        services.AddSingleton<IGoogleOAuthClient, GoogleOAuthClient>();

        services.AddScoped<IIntegrationRepository, IntegrationRepository>();
        services.AddScoped<IGoogleImportProfileReader, GoogleImportProfileReader>();
        services.AddScoped<IGoogleConnectionService, GoogleConnectionService>();

        // ExternalMatterReconciler is a kernel type registered by AddKernelBusiness()
        // and shared with the ICS importer — not registered here.
        services.AddScoped<IGoogleCalendarSyncService, GoogleCalendarSyncService>();
        services.AddScoped<IGoogleTasksSyncService, GoogleTasksSyncService>();

        // The outbound half: matters mirrored INTO a Kitto-owned calendar.
        services.AddScoped<IGoogleCalendarPushService, GoogleCalendarPushService>();

        // Three named clients so a timeout or a handler policy can be tuned per
        // upstream. The per-request deadlines are enforced with linked cancellation
        // tokens, matching Node's AbortSignal.timeout.
        services.AddHttpClient(GoogleOAuthClient.HttpClientName);
        services.AddHttpClient(GoogleCalendarSyncService.HttpClientName);
        services.AddHttpClient(GoogleTasksSyncService.HttpClientName);
        services.AddHttpClient(GoogleCalendarPushService.HttpClientName);

        services.AddMongoIndexProvider<IntegrationIndexes>();
        services.AddUserDataEraser<IntegrationEraser>();
        services.AddKernelWorker<GoogleSyncWorker>();
        services.AddKernelWorker<GooglePushWorker>();

        return services;
    }
}
