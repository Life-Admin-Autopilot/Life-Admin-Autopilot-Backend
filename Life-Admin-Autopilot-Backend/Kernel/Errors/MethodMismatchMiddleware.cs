namespace Life_Admin_Autopilot_Backend.Kernel.Errors;

/// <summary>
/// Express has no 405. Its router tries each layer in turn; a layer whose path
/// matches but whose method does not simply is not invoked, so the request falls
/// through to <c>finalhandler</c>, which answers <b>404</b>. ASP.NET routing
/// instead selects a synthetic method-not-allowed endpoint and answers 405.
///
/// <para><b>Verified live against the Node reference:</b>
/// <c>PUT /health</c> → 404 (not 405), with <b>no <c>Allow</c> header</b>.</para>
///
/// <para><b>Only fires when NO route accepted the method.</b> A path that matches a
/// different route under another method is unaffected — <c>DELETE /me/tasks/counts</c>
/// matches <c>DELETE /me/tasks/{id}</c> with <c>id="counts"</c> and answers 401 from
/// auth, exactly as Express does. Routing has already resolved that before this
/// middleware sees the status, so there is nothing to special-case.</para>
///
/// <para><b>Placement.</b> Registered OUTSIDE <c>NodeCorsMiddleware</c>, so on the way
/// back up CORS has already done its post-processing. That matters for OPTIONS: a
/// disallowed origin lets the request reach routing, and CORS rewrites the resulting
/// 405 to Express's automatic-OPTIONS 200 with <c>Allow: GET,HEAD</c>. By the time
/// this middleware looks, the status is 200 and it correctly does nothing. The
/// OPTIONS check below is belt-and-braces for the case where that rewrite is skipped.</para>
///
/// <para><b>Deliberately NOT reproduced: the body.</b> Express emits
/// <c>Content-Type: text/html</c>, <c>Content-Security-Policy: default-src 'none'</c>
/// and <c>&lt;pre&gt;Cannot PUT /health&lt;/pre&gt;</c>. That body interpolates the
/// attacker-controlled request path, so porting it naively creates reflected XSS on
/// every unknown route of an API that also serves authenticated JSON. Nothing parses a
/// 404 body — the frontend branches on status — so the status is the only difference a
/// client can observe. See <c>docs/KERNEL.md</c> §2.2.1 for the full arbitration; the
/// two <c>framework/*</c> harness rows mask body and content-type for this reason.</para>
/// </summary>
public sealed class MethodMismatchMiddleware
{
    private readonly RequestDelegate _next;

    public MethodMismatchMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        var response = context.Response;

        if (response.HasStarted || response.StatusCode != StatusCodes.Status405MethodNotAllowed)
        {
            return;
        }

        // Express's automatic OPTIONS responder answers 200 with Allow, never 404.
        // NodeCorsMiddleware already performs that rewrite where it applies.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return;
        }

        response.StatusCode = StatusCodes.Status404NotFound;

        // Express's 404 carries no Allow header — ASP.NET's 405 does. Verified live.
        response.Headers.Remove("Allow");
    }
}
