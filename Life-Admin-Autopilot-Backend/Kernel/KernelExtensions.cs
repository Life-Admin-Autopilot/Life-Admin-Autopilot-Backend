using Life_Admin_Autopilot.BLL.Kernel;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Cors;
using Life_Admin_Autopilot_Backend.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Json;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Life_Admin_Autopilot_Backend.Kernel.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot_Backend.Kernel;

/// <summary>
/// The two calls <c>Program.cs</c> makes. Everything else registers itself.
/// </summary>
public static class KernelExtensions
{
    /// <summary>
    /// Registers the whole kernel plus every discovered
    /// <see cref="IEndpointModule"/>. Call ONCE, after the existing layer
    /// registrations (it needs <c>IMongoDatabase</c>).
    /// </summary>
    public static IServiceCollection AddKernel(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKernelBusiness();

        // ---- CORS ----------------------------------------------------------
        services.AddOptions<KernelCorsOptions>()
            .Bind(configuration.GetSection(KernelCorsOptions.SectionName))
            .PostConfigure(options =>
            {
                // The Node deployment configures this as a plain env var, so accept it
                // under its original name too. Explicit config wins.
                if (string.IsNullOrWhiteSpace(options.Origins))
                {
                    options.Origins = configuration["CORS_ORIGINS"] ?? string.Empty;
                }
            });

        // ---- Auth ----------------------------------------------------------
        services.AddAuthentication(KernelAuthOptions.Scheme)
            .AddScheme<KernelAuthOptions, KernelAuthenticationHandler>(KernelAuthOptions.Scheme, _ => { });

        services.AddOptions<KernelAuthOptions>(KernelAuthOptions.Scheme)
            .Bind(configuration.GetSection(KernelAuthOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.AccessSecret))
                {
                    // JWT_ACCESS_SECRET is the Node env var; Jwt:Key is the pre-existing
                    // .NET setting. Either keeps the server bootable.
                    options.AccessSecret = configuration["JWT_ACCESS_SECRET"]
                        ?? configuration["Jwt:Key"]
                        ?? string.Empty;
                }
            });

        // ---- Rate limiting --------------------------------------------------
        services.AddOptions<KernelRateLimitOptions>()
            .Bind(configuration.GetSection(KernelRateLimitOptions.SectionName));
        services.AddKernelRateLimiters();

        // ---- MVC / JSON ------------------------------------------------------
        services.Configure<JsonOptions>(options => CopyInto(options.JsonSerializerOptions));
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
            options => CopyInto(options.SerializerOptions));

        services.Configure<ApiBehaviorOptions>(options =>
        {
            // ASP.NET's automatic 400 emits a ProblemDetails body, which Node never
            // produces. Validation failures must travel as AppException instead.
            options.SuppressModelStateInvalidFilter = true;
        });

        // Index creation. Mongoose does this from the schema; the .NET driver does
        // not, and the quota primitive's correctness depends on it.
        services.AddHostedService<Hosting.MongoIndexHostedService>();

        services.AddEndpointModules(configuration);
        return services;
    }

    /// <summary>
    /// Installs the kernel middleware, in the ONE order that reproduces Node's
    /// pipeline, and maps every module's endpoints. Call before
    /// <c>app.MapControllers()</c>.
    /// </summary>
    public static WebApplication UseKernel(this WebApplication app)
    {
        // 1. Error handling wraps everything, so a throw anywhere below becomes the
        //    envelope. It is FIRST because it must also catch CORS/auth failures.
        app.UseMiddleware<KernelErrorMiddleware>();

        // 2. Method-mismatch translation, OUTSIDE CORS so it observes the status after
        //    CORS's post-processing. Express has no 405 — a path that matches a route
        //    under a different method falls through to a 404. See the middleware's own
        //    docs for why it must sit here rather than inside NodeCorsMiddleware.
        app.UseMiddleware<MethodMismatchMiddleware>();

        // 3. CORS before routing — a rejected preflight must never reach an endpoint,
        //    and an accepted one must be answered even for a path that does not exist.
        app.UseMiddleware<NodeCorsMiddleware>();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapEndpointModules();
        return app;
    }

    private static void AddKernelRateLimiters(this IServiceCollection services)
    {
        var minute = TimeSpan.FromMinutes(1);

        services.AddSingleton<IKernelRateLimiter>(sp => new FixedWindowRateLimiter(
            KernelRateLimiters.Auth,
            TimeSpan.FromMinutes(15),
            20,
            "Too many requests. Try again in a few minutes.",
            Enabled(sp)));

        services.AddSingleton<IKernelRateLimiter>(sp => new FixedWindowRateLimiter(
            KernelRateLimiters.StrictAuth,
            TimeSpan.FromHours(1),
            5,
            "Too many attempts. Try again later.",
            Enabled(sp)));

        services.AddSingleton<IKernelRateLimiter>(sp =>
            new SlidingWindowRateLimiter(KernelRateLimiters.AiAsk, minute, 30, Enabled(sp)));
        services.AddSingleton<IKernelRateLimiter>(sp =>
            new SlidingWindowRateLimiter(KernelRateLimiters.TaskSearch, minute, 30, Enabled(sp)));
        services.AddSingleton<IKernelRateLimiter>(sp =>
            new SlidingWindowRateLimiter(KernelRateLimiters.TaskSummary, minute, 10, Enabled(sp)));
        services.AddSingleton<IKernelRateLimiter>(sp =>
            new SlidingWindowRateLimiter(KernelRateLimiters.AiConfirm, minute, 30, Enabled(sp)));
        services.AddSingleton<IKernelRateLimiter>(sp =>
            new SlidingWindowRateLimiter(KernelRateLimiters.AiVoice, minute, 12, Enabled(sp)));
        services.AddSingleton<IKernelRateLimiter>(sp =>
            new SlidingWindowRateLimiter(KernelRateLimiters.DocumentScan, minute, 6, Enabled(sp)));

        services.AddSingleton<KernelRateLimiterRegistry>();

        static Func<bool> Enabled(IServiceProvider sp)
        {
            var options = sp.GetRequiredService<IOptionsMonitor<KernelRateLimitOptions>>();
            return () => options.CurrentValue.Enabled;
        }
    }

    /// <summary>
    /// Mirrors <see cref="KernelJson.Lenient"/> onto the framework's own options
    /// instances — they cannot simply be replaced, only configured.
    /// </summary>
    private static void CopyInto(System.Text.Json.JsonSerializerOptions target)
    {
        var source = KernelJson.Lenient;
        target.PropertyNamingPolicy = source.PropertyNamingPolicy;
        target.PropertyNameCaseInsensitive = source.PropertyNameCaseInsensitive;
        target.DefaultIgnoreCondition = source.DefaultIgnoreCondition;
        target.Encoder = source.Encoder;
        target.NumberHandling = source.NumberHandling;
        target.UnmappedMemberHandling = source.UnmappedMemberHandling;

        foreach (var converter in source.Converters)
        {
            target.Converters.Add(converter);
        }
    }
}
