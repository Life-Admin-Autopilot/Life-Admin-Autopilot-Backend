using Microsoft.AspNetCore.Mvc.Filters;

namespace Life_Admin_Autopilot_Backend.Kernel.RateLimiting;

/// <summary>
/// Applies a named limiter to a controller action.
///
/// <para>
/// <b>Ordering matters.</b> The hand-rolled limiters key on the authenticated user
/// id, so they must run AFTER authentication — which they do, because MVC filters
/// run after the authentication middleware. The IP-keyed auth limiters do not
/// care.
/// </para>
///
/// <code>
/// [Authorize]
/// [RateLimit(KernelRateLimiters.AiVoice)]
/// [HttpPost("/me/voice-notes")]
/// public async Task&lt;IResult&gt; Upload() { … }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RateLimitAttribute : Attribute, IFilterFactory
{
    public RateLimitAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new RateLimitFilter(serviceProvider.GetRequiredService<KernelRateLimiterRegistry>(), Name);
}

internal sealed class RateLimitFilter : IActionFilter
{
    private readonly KernelRateLimiterRegistry _registry;
    private readonly string _name;

    public RateLimitFilter(KernelRateLimiterRegistry registry, string name)
    {
        _registry = registry;
        _name = name;
    }

    public void OnActionExecuting(ActionExecutingContext context) =>
        _registry.Get(_name).Apply(context.HttpContext);

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>Minimal-API equivalent of <see cref="RateLimitAttribute"/>.</summary>
public static class RateLimitEndpointExtensions
{
    public static TBuilder RateLimited<TBuilder>(this TBuilder builder, string name)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.RequestServices
                .GetRequiredService<KernelRateLimiterRegistry>()
                .Get(name)
                .Apply(context.HttpContext);

            return await next(context);
        });

        return builder;
    }
}
