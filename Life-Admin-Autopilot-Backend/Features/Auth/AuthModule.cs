using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Life_Admin_Autopilot_Backend.Features.Auth;

/// <summary>
/// The auth slice's single registration point. Discovered by the kernel's
/// assembly scan, so <c>Program.cs</c> stays untouched.
/// </summary>
public sealed class AuthModule : IEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddAuthFeature(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthPasswordEndpoints();
        endpoints.MapAuthSessionEndpoints();
        endpoints.MapAuthEmailEndpoints();
        endpoints.MapAuthMagicEndpoints();
    }
}

public static class AuthFeature
{
    public static IServiceCollection AddAuthFeature(this IServiceCollection services, IConfiguration configuration)
    {
        EnsureGuidSerializer();

        services.AddScoped<IUserProfileRepository, UserProfileRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IVerificationTokenRepository, VerificationTokenRepository>();

        // The credential seam. Registered with TryAdd so a test host can supply a
        // double before this runs.
        services.TryAddScoped<IAuthCredentialStore, IdentityCredentialStore>();

        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IAuthFlowService, AuthFlowService>();
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.TryAddSingleton<IAuthEmailSender, LoggingAuthEmailSender>();
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<AuthJwtOptions>(options =>
        {
            var resolved = AuthJwtConfiguration.Read(configuration);
            options.AccessSecret = resolved.AccessSecret;
            options.AccessTtl = resolved.AccessTtl;
            options.RefreshTtl = resolved.RefreshTtl;
        });

        services.AddMongoIndexProvider<AuthIndexes>();
        services.AddUserDataEraser<AuthSessionEraser>();

        return services;
    }

    /// <summary>
    /// Gives <c>Guid</c> an explicit BSON representation.
    ///
    /// <para>
    /// <b>This belongs in the kernel, not here.</b> <c>UserProfileDocument</c> is a
    /// kernel document and <c>IdentityUserId</c> is a kernel field, but
    /// <c>MongoKernelConventions</c> registers serializers only for
    /// <c>DateTime</c>. Driver 3.x refuses to serialize a Guid whose representation
    /// is <c>Unspecified</c>, so writing the kernel's own user document throws
    /// <c>BsonSerializationException</c> — every signup was a 500 until this was
    /// added. Registered from the slice purely to unblock; reported to the kernel
    /// owner so it can move into the convention pack, at which point this becomes a
    /// harmless no-op.
    /// </para>
    ///
    /// <para>
    /// <c>Standard</c> is the driver-3 default for new data and the only
    /// representation that round-trips a Guid unambiguously across drivers.
    /// </para>
    /// </summary>
    /// <summary>
    /// Guards <see cref="EnsureGuidSerializer"/>. Interlocked rather than a bool
    /// because racing callers are the entire problem it exists for.
    /// </summary>
    private static int _guidSerializerRegistered;

    private static void EnsureGuidSerializer()
    {
        // ONCE PER PROCESS, and it has to be. The driver's serializer registry is
        // static, but this runs from AddServices — once per host build. A process
        // builds one host in production, so the difference never showed there; the
        // test suite builds a dozen WebApplicationFactory hosts in parallel against
        // that one registry. Whichever host lost the race found a Guid serializer
        // already present, TryRegisterSerializer threw instead of returning false
        // because the two instances are not equal, and AddKernel died during
        // startup — failing every test in that host, including the several hundred
        // that never touch a Guid. Intermittently, which is worse than always: it
        // reads as a bad commit rather than as a race.
        if (Interlocked.Exchange(ref _guidSerializerRegistered, 1) == 1)
        {
            return;
        }

        try
        {
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            BsonSerializer.TryRegisterSerializer(
                new NullableSerializer<Guid>(new GuidSerializer(GuidRepresentation.Standard)));
        }
        catch (BsonSerializationException)
        {
            // Something asked the driver for a Guid serializer before this ran and
            // it cached its own default. Exactly one representation is ever used
            // here and it is registered from this method alone, so whatever landed
            // first is the one to keep — and refusing to start over a serializer
            // that is already present helps nobody.
        }
    }
}

/// <summary>
/// Drops the two collections this slice owns when an account is deleted.
///
/// <para>
/// Runs at <see cref="UserErasureOrder.Sessions"/> so the refresh-token rows go
/// before the dependent collections and long before the account row itself —
/// a half-finished cascade should leave a signed-out user, not a still-live
/// session pointing at half-deleted data.
/// </para>
/// </summary>
public sealed class AuthSessionEraser : IUserDataEraser
{
    private readonly ISessionRepository _sessions;
    private readonly IVerificationTokenRepository _tokens;
    private readonly IAuthCredentialStore _credentials;

    public AuthSessionEraser(
        ISessionRepository sessions,
        IVerificationTokenRepository tokens,
        IAuthCredentialStore credentials)
    {
        _sessions = sessions;
        _tokens = tokens;
        _credentials = credentials;
    }

    public string Name => "auth-sessions";

    public int Order => UserErasureOrder.Sessions;

    public async Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default)
    {
        await _sessions.DeleteAllAsync(context.UserId, cancellationToken).ConfigureAwait(false);
        await _tokens.DeleteAllAsync(context.UserId, cancellationToken).ConfigureAwait(false);
        await _credentials.DeleteAsync(context.IdentityUserId, cancellationToken).ConfigureAwait(false);
    }
}
