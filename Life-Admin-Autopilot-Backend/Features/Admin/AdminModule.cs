using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.BLL.Kernel.Telemetry;
using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot.DAL.Kernel.Activity;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Kernel.Ops;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Life_Admin_Autopilot_Backend.Features.Admin;

/// <summary>
/// The admin console slice: telemetry storage, the audit log, console roles, and
/// every <c>/admin/*</c> route.
///
/// <para>
/// <b>Telemetry registers unconditionally; the console does not.</b> Usage recording
/// has to run on every deployment — a month of missing data cannot be backfilled —
/// whereas the console itself is gated on <c>ADMIN_JWT_SECRET</c>, so a deployment
/// that has not configured one simply has no admin surface rather than an
/// unauthenticated one.
/// </para>
/// </summary>
public static class AdminFeature
{
    public static IServiceCollection AddAdminFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ---- telemetry: always on -------------------------------------------
        services.TryAddSingleton(ModelPricing.FromConfiguration(configuration));
        services.TryAddScoped<IAiUsageStore, MongoAiUsageStore>();

        // Replace() rather than TryAdd(): AddKernel registers NullAiUsageRecorder as
        // the default so LangflowAiProvider always resolves one, and this is the
        // upgrade from "no-op" to "actually records".
        services.Replace(ServiceDescriptor.Scoped<IAiUsageRecorder, AiUsageRecorder>());

        services.AddMongoIndexProvider<AiUsageIndexes>();
        services.AddUserDataEraser<AiUsageEventEraser>();
        services.AddUserDataEraser<AiUsageRollupEraser>();

        services.AddHostedService<AiUsageRollupWorker>();
        services.AddHostedService<AdminRoleSeeder>();

        // The live feed's transport. SINGLETON — it holds the backlog and the
        // subscriber list, so a scoped registration would give every request its own
        // empty bus and the feed would never carry anything.
        services.TryAddSingleton<IAdminActivityBus, AdminActivityBus>();

        // ---- audit + console ------------------------------------------------
        services.TryAddScoped<IAdminAuditStore, MongoAdminAuditStore>();
        services.AddMongoIndexProvider<AdminAuditIndexes>();

        services.TryAddScoped<IAdminCustomerRepository, AdminCustomerRepository>();
        services.TryAddScoped<IAdminRoleStore, AdminRoleStore>();
        services.TryAddScoped<AdminCustomerService>();
        services.TryAddScoped<AdminInsightService>();
        services.TryAddScoped<AdminNotificationService>();
        services.TryAddScoped<AdminOpsService>();

        // Kill switches. Registered here rather than in the kernel because the flags
        // only exist to be flipped from the console.
        services.TryAddScoped<IFeatureFlagStore, MongoFeatureFlagStore>();
        services.AddMongoIndexProvider<FeatureFlagIndexes>();

        // Resolved LAZILY, from the container's IConfiguration, not eagerly from the
        // `configuration` handed to this method.
        //
        // `builder.Configuration` is a mutable ConfigurationManager: sources added
        // after `AddKernel(...)` runs — which is exactly what WebApplicationFactory
        // does, and what any future `AddKeyVault()`/`AddUserSecrets()` late in
        // Program.cs would do — are invisible to a value read here at registration
        // time. Reading it eagerly made the console silently disable itself with a
        // 403 `admin_console_disabled`, while the flat key was plainly present in
        // the resolved configuration. Nothing in the logs said why.
        services.TryAddSingleton(sp =>
            AdminTokenOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        services.TryAddSingleton<AdminTokenService>();

        AddConsoleAuthentication(services);

        return services;
    }

    /// <summary>
    /// Registers the <c>AdminBearer</c> scheme and the two policies.
    ///
    /// <para>
    /// <b>The scheme is registered even when the secret is absent</b>, with a
    /// throwaway key that no real token can be signed with. That is deliberate:
    /// <c>RequireAuthorization(ConsolePolicy)</c> on the route group would throw at
    /// startup if the scheme did not exist, taking the whole API down because the
    /// admin console was not configured. With it registered-but-unusable, every
    /// <c>/admin/*</c> request answers 401 and the customer API is untouched.
    /// </para>
    /// </summary>
    private static void AddConsoleAuthentication(IServiceCollection services)
    {
        // The scheme is registered with an empty callback and configured through
        // the options system instead, so the validation parameters are built when
        // the scheme is first USED rather than when it is declared. Same reason as
        // the options registration above: at declaration time the secret may not be
        // in configuration yet.
        services.AddAuthentication().AddJwtBearer(AdminRoles.Scheme, _ => { });

        services
            .AddOptions<JwtBearerOptions>(AdminRoles.Scheme)
            .Configure<AdminTokenService>((jwt, tokens) =>
            {
                jwt.TokenValidationParameters = tokens.ValidationParameters();
                jwt.MapInboundClaims = false;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminRoles.ConsolePolicy, policy => policy
                .AddAuthenticationSchemes(AdminRoles.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(AdminRoles.Admin, AdminRoles.Support))
            .AddPolicy(AdminRoles.OperatorPolicy, policy => policy
                .AddAuthenticationSchemes(AdminRoles.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(AdminRoles.Admin));
    }
}

/// <summary>Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.</summary>
public sealed class AdminModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddAdminFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapAdminEndpoints();
}
