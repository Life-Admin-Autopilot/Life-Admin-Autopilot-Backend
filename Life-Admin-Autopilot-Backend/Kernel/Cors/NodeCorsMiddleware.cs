using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot_Backend.Kernel.Cors;

/// <summary>
/// Mirrors <c>buildCorsOptions()</c> in <c>server/src/app.ts</c>.
/// Bound from <c>Kernel:Cors</c>, or the <c>CORS_ORIGINS</c> environment variable.
/// </summary>
public sealed class KernelCorsOptions
{
    public const string SectionName = "Kernel:Cors";

    /// <summary>Comma-separated, exactly like the Node env var. Blank entries are dropped.</summary>
    public string Origins { get; set; } = string.Empty;

    public IReadOnlyList<string> Allowlist() =>
        Origins
            .Split(',')
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .ToList();
}

/// <summary>
/// A hand-written port of the <c>cors</c> npm package as configured by
/// <c>app.ts</c>. ASP.NET's built-in CORS middleware cannot reproduce it:
/// it answers a rejected preflight with 204 and adds a <c>Vary</c> header where
/// Node adds neither.
///
/// <para><b>The three cases, all verified live against the Node server.</b></para>
///
/// <list type="number">
///   <item>
///     <b>No <c>Origin</c> header</b> (native app, curl, server-to-server) —
///     ALLOWED. <c>cors</c> resolves the origin option to <c>true</c>, so it emits
///     <c>Vary: Origin</c> and <c>Access-Control-Allow-Credentials: true</c> but NO
///     <c>Access-Control-Allow-Origin</c> (its value would be the absent request
///     origin, and <c>cors</c> skips falsy header values). A preflight in this state
///     still short-circuits with <b>204</b>.
///   </item>
///   <item>
///     <b>Allowlisted origin</b> — <c>Access-Control-Allow-Origin</c> echoes the
///     request origin (never <c>*</c>: reflecting an arbitrary origin while sending
///     credentials lets any site drive authenticated requests), plus
///     <c>Vary: Origin</c> and credentials. Preflight → <b>204</b> with
///     <c>Access-Control-Allow-Methods</c> and an echo of
///     <c>Access-Control-Request-Headers</c>.
///   </item>
///   <item>
///     <b>Any other origin</b> — the origin callback yields <c>false</c>, and the
///     <c>cors</c> middleware then calls <c>next()</c> WITHOUT applying anything and
///     WITHOUT short-circuiting. So: <b>no CORS headers at all</b> (not even
///     <c>Vary</c>), the request is processed normally, and a preflight falls
///     through to the default OPTIONS responder with <b>200</b>. It is not an error —
///     the browser simply refuses the response, which is the correct rejection.
///   </item>
/// </list>
/// </summary>
public sealed class NodeCorsMiddleware
{
    /// <summary>The <c>cors</c> package default. Order and spelling are part of the response.</summary>
    private const string DefaultMethods = "GET,HEAD,PUT,PATCH,POST,DELETE";

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<string> _allowlist;

    public NodeCorsMiddleware(RequestDelegate next, IOptions<KernelCorsOptions> options)
    {
        _next = next;
        _allowlist = options.Value.Allowlist();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var origin = request.Headers.Origin.ToString();
        var hasOrigin = !string.IsNullOrEmpty(origin);
        var isAllowed = !hasOrigin || _allowlist.Contains(origin, StringComparer.Ordinal);

        // `cors` branches on the METHOD alone — it never checks
        // Access-Control-Request-Method. So a bare OPTIONS with no preflight headers
        // is still short-circuited with 204 (verified live).
        var isPreflight = HttpMethods.IsOptions(request.Method);

        if (!isAllowed)
        {
            // Case 3 — no headers, no short-circuit.
            await _next(context);

            if (isPreflight)
            {
                MimicExpressAutoOptions(context);
            }

            return;
        }

        var response = context.Response;

        // configureOrigin: ACAO only when there IS a request origin to echo.
        if (hasOrigin)
        {
            response.Headers.AccessControlAllowOrigin = origin;
        }

        AppendVary(response, "Origin");

        // configureCredentials
        response.Headers.AccessControlAllowCredentials = "true";

        if (!isPreflight)
        {
            await _next(context);
            return;
        }

        // configureMethods
        response.Headers.AccessControlAllowMethods = DefaultMethods;

        // configureAllowedHeaders: echo the request's list; Vary on it either way.
        var requestedHeaders = request.Headers.AccessControlRequestHeaders.ToString();
        if (!string.IsNullOrEmpty(requestedHeaders))
        {
            response.Headers.AccessControlAllowHeaders = requestedHeaders;
        }

        AppendVary(response, "Access-Control-Request-Headers");

        // optionsSuccessStatus: 204, empty body.
        response.StatusCode = StatusCodes.Status204NoContent;
        response.ContentLength = 0;
    }

    /// <summary>
    /// Express's router answers an unhandled OPTIONS on a path that HAS routes with
    /// <c>200</c> and an <c>Allow</c> header — never 405. ASP.NET routing returns 405,
    /// so translate it. A path with no routes at all still 404s in both, which is why
    /// this only rewrites 405.
    ///
    /// <para>Express also lists HEAD alongside GET; ASP.NET's <c>Allow</c> says only
    /// GET.</para>
    /// </summary>
    private static void MimicExpressAutoOptions(HttpContext context)
    {
        if (context.Response.StatusCode != StatusCodes.Status405MethodNotAllowed || context.Response.HasStarted)
        {
            return;
        }

        var allow = context.Response.Headers.Allow.ToString();
        if (allow.Contains("GET", StringComparison.Ordinal) && !allow.Contains("HEAD", StringComparison.Ordinal))
        {
            allow = string.Join(
                ",",
                allow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SelectMany(m => m == "GET" ? new[] { "GET", "HEAD" } : new[] { m }));
            context.Response.Headers.Allow = allow;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>
    /// The <c>vary</c> helper <c>cors</c> uses appends into ONE comma-joined header
    /// value, so a preflight emits a single
    /// <c>Vary: Origin, Access-Control-Request-Headers</c> line. Two separate
    /// <c>Vary</c> headers are semantically equivalent but not byte-identical.
    /// </summary>
    private static void AppendVary(HttpResponse response, string value)
    {
        var current = response.Headers.Vary.ToString();
        response.Headers.Vary = string.IsNullOrEmpty(current) ? value : $"{current}, {value}";
    }
}
