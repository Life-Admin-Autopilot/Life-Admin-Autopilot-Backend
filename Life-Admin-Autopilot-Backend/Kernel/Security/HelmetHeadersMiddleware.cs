namespace Life_Admin_Autopilot_Backend.Kernel.Security;

/// <summary>
/// Port of <c>app.use(helmet())</c> in <c>server/src/app.ts</c> — helmet 8.1.0 with
/// no options, so the full default set and nothing else.
///
/// <para>
/// The twelve headers below were captured from the live Node reference rather than
/// read out of helmet's documentation, and they are <b>values, not approximations</b>:
/// the CSP is one semicolon-joined line with no spaces after the separators, and
/// <c>X-XSS-Protection</c> is <c>0</c> (helmet disables the legacy auditor on purpose,
/// because it introduced its own vulnerabilities) rather than the <c>1; mode=block</c>
/// a naive port writes.
/// </para>
///
/// <para><b>Every response, including error responses.</b> In Express these are set on
/// the way in, and <c>res.json()</c> in the error handler replaces the BODY, not the
/// headers — so a 400, a 429 and a 500 all carry the same twelve. Reproducing that is
/// the reason <c>KernelErrorMiddleware.WriteAsync</c> must not call
/// <c>Response.Clear()</c>.</para>
///
/// <para><b>Placement.</b> Registered immediately inside <see cref="Errors.KernelErrorMiddleware"/>
/// and before <see cref="Cors.NodeCorsMiddleware"/>, mirroring <c>helmet()</c> being the
/// first <c>app.use</c> ahead of <c>cors()</c>. That ordering is also what puts the
/// helmet block before <c>Vary</c> / <c>Access-Control-Allow-Credentials</c> in the
/// emitted header list, as the reference does.</para>
///
/// <para>
/// <b>What Node does NOT send.</b> <c>X-Powered-By</c> is turned off by
/// <c>app.disable('x-powered-by')</c>; Kestrel's equivalent is the <c>Server</c> header,
/// suppressed in <c>AddKernel</c> via <c>KestrelServerOptions.AddServerHeader</c>. Both
/// are version-disclosure headers and neither reference nor candidate should emit one.
/// </para>
/// </summary>
public sealed class HelmetHeadersMiddleware
{
    /// <summary>
    /// helmet 8's default <c>Content-Security-Policy</c>. Directives are joined with
    /// <c>;</c> and NO following space — that is helmet's own serialisation, and a
    /// byte-level diff notices the difference.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self';base-uri 'self';font-src 'self' https: data:;form-action 'self';" +
        "frame-ancestors 'self';img-src 'self' data:;object-src 'none';script-src 'self';" +
        "script-src-attr 'none';style-src 'self' https: 'unsafe-inline';upgrade-insecure-requests";

    /// <summary>
    /// The complete set, in the order the reference emits it. Exposed so tests can
    /// assert against one list instead of a dozen literals.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Defaults = new[]
    {
        new KeyValuePair<string, string>("Content-Security-Policy", ContentSecurityPolicy),
        new KeyValuePair<string, string>("Cross-Origin-Opener-Policy", "same-origin"),
        new KeyValuePair<string, string>("Cross-Origin-Resource-Policy", "same-origin"),
        new KeyValuePair<string, string>("Origin-Agent-Cluster", "?1"),
        new KeyValuePair<string, string>("Referrer-Policy", "no-referrer"),
        new KeyValuePair<string, string>("Strict-Transport-Security", "max-age=31536000; includeSubDomains"),
        new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
        new KeyValuePair<string, string>("X-DNS-Prefetch-Control", "off"),
        new KeyValuePair<string, string>("X-Download-Options", "noopen"),
        new KeyValuePair<string, string>("X-Frame-Options", "SAMEORIGIN"),
        new KeyValuePair<string, string>("X-Permitted-Cross-Domain-Policies", "none"),
        new KeyValuePair<string, string>("X-XSS-Protection", "0"),
    };

    private readonly RequestDelegate _next;

    public HelmetHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        // Set on the way IN, exactly like helmet, so they are already on the response
        // when anything below throws and the error middleware writes the envelope.
        Apply(context.Response);
        return _next(context);
    }

    /// <summary>Writes the default set onto a response, overwriting any existing value.</summary>
    public static void Apply(HttpResponse response)
    {
        var headers = response.Headers;
        foreach (var (name, value) in Defaults)
        {
            headers[name] = value;
        }
    }
}
