using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.GoogleIntegration;

/// <summary>
/// Ports <c>server/src/routes/integrations.google.ts</c> — the Google OAuth legs and
/// the three authenticated management routes. None of the five is rate limited in
/// Node.
///
/// <para><b>Google OAuth, three legs:</b></para>
/// <list type="number">
///   <item><c>POST /integrations/google/authorize</c> (authenticated) → returns a URL</item>
///   <item><c>GET /integrations/google/callback</c> — <b>NOT authenticated</b>, because
///         Google's redirect carries no session</item>
///   <item>deep link back into the app</item>
/// </list>
///
/// <para>
/// Leg 2 is why the signed state exists. It arrives as a bare GET with no cookie and
/// no Authorization header, so <c>state</c> is the only thing binding the incoming
/// tokens to a Kitto account. Leg 1 returns a URL rather than redirecting because
/// the CALLER must open it in the system browser: Google refuses OAuth inside an
/// embedded webview (<c>disallowed_useragent</c>), which is exactly what the
/// Capacitor shell is, so a 302 from here would land the user in a dead end.
/// </para>
/// </summary>
public static class GoogleIntegrationEndpoints
{
    /// <summary>
    /// Deliberately inert: no script, no styling hooks, nothing to interact with. Its
    /// only job is to tell a human the tab has done its work. Byte-exact from the
    /// Node source, including the absence of a trailing newline.
    /// </summary>
    private const string ConnectedPage =
        "<!doctype html><meta charset=\"utf-8\">\n"
        + "<title>Connected</title>\n"
        + "<body style=\"font-family:system-ui;padding:3rem;text-align:center\">\n"
        + "<h1 style=\"font-size:1.25rem\">Google connected</h1>\n"
        + "<p style=\"color:#666\">You can close this tab and go back to Kitto.</p>\n"
        + "</body>";

    public static IEndpointRouteBuilder MapGoogleIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/integrations/google/authorize", AuthorizeAsync).RequireAuthorization();
        endpoints.MapGet("/integrations/google/callback", CallbackAsync);
        endpoints.MapGet("/integrations/google", GetIntegrationAsync).RequireAuthorization();
        endpoints.MapPost("/integrations/google/sync", SyncAsync).RequireAuthorization();
        endpoints.MapDelete("/integrations/google", DisconnectAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        GoogleIntegrationOptions options,
        IGoogleTokenCipher cipher,
        IGoogleOAuthState state,
        IGoogleOAuthClient oauth,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        // Read the body FIRST even though the readiness gate below may make it
        // irrelevant: in Node express.json() is middleware, so a malformed body is a
        // 500 before the route is ever entered. Gating first would answer 400 to a
        // request Node answers 500 to.
        var web = await ReadWebFlagAsync(context, cancellationToken).ConfigureAwait(false);

        if (!Ready(options, cipher))
        {
            throw AppException.BadRequest(
                "google_not_configured",
                "Connecting a Google account is not available yet.");
        }

        return Results.Ok(new GoogleAuthorizeResponse
        {
            Url = oauth.BuildAuthorizeUrl(state.Issue(caller.IdString, web)),
        });
    }

    /// <summary>
    /// <b>Every exit from here is a REDIRECT to the app, never JSON.</b> The user is
    /// looking at a system browser tab, and a raw error body is both useless to them
    /// and a dead end — the app would keep waiting for a deep link that never comes.
    /// There is no code path that emits the error envelope.
    /// </summary>
    private static async Task CallbackAsync(
        HttpContext context,
        GoogleIntegrationOptions options,
        IGoogleOAuthState state,
        IGoogleOAuthClient oauth,
        IGoogleConnectionService connections,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Google.Callback");

        Task Fail(string reason) => ExpressRedirect.FoundAsync(
            context, DeepLink(options, "error", ("reason", reason)), cancellationToken);

        // The user pressed Cancel on the consent screen. Not an error; the app should
        // just close the browser quietly. Checked FIRST, before state verification.
        var declined = SingleQueryValue(context, "error");
        if (!string.IsNullOrEmpty(declined))
        {
            await ExpressRedirect
                .FoundAsync(
                    context,
                    DeepLink(options, declined == "access_denied" ? "cancelled" : "error"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        OAuthStateClaims claims;
        try
        {
            claims = state.Verify(SingleQueryValue(context, "state"));
        }
        catch (InvalidOAuthStateException ex)
        {
            // Deliberately vague to the caller. A forged or expired state is either an
            // attack or a stale tab, and neither deserves a description of which check
            // failed.
            logger.LogWarning(ex, "google:callback:bad-state");
            await Fail("invalid_state").ConfigureAwait(false);
            return;
        }

        var code = SingleQueryValue(context, "code");
        if (string.IsNullOrEmpty(code))
        {
            await Fail("no_code").ConfigureAwait(false);
            return;
        }

        try
        {
            var (tokens, identity) = await oauth.ExchangeCodeAsync(code, cancellationToken).ConfigureAwait(false);

            // Node hands Mongoose the string and lets it cast; a value that will not
            // cast raises a CastError inside the same try, so it lands on the same
            // exchange_failed exit.
            if (!ObjectId.TryParse(claims.UserId, out var userId))
            {
                throw new FormatException($"state carried an uncastable user id: {claims.UserId}");
            }

            await connections.SaveConnectionAsync(userId, tokens, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "google:callback:exchange-failed userId={UserId}", claims.UserId);
            await Fail("exchange_failed").ConfigureAwait(false);
            return;
        }

        if (claims.Web)
        {
            // Nothing to redirect to — a browser tab cannot follow kitto://. The tab
            // says its piece and the app picks the change up when it regains focus.
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(ConnectedPage, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ExpressRedirect
            .FoundAsync(context, DeepLink(options, "connected"), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> GetIntegrationAsync(
        HttpContext context,
        GoogleIntegrationOptions options,
        IGoogleTokenCipher cipher,
        IGoogleConnectionService connections,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();
        var integration = await connections.FindAsync(caller.Id, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new GoogleStatusResponse
        {
            Available = Ready(options, cipher),

            // The mapper strips every token field — see GoogleIntegrationDto.
            Integration = integration?.ToDto(),
        });
    }

    /// <summary>
    /// Pull now. Runs Calendar and Tasks <b>in sequence</b> and reports both.
    /// Sequential rather than parallel on purpose: they share one access token, and a
    /// concurrent refresh from both would race to write the same row.
    /// </summary>
    private static async Task<IResult> SyncAsync(
        HttpContext context,
        IGoogleConnectionService connections,
        IGoogleImportProfileReader profiles,
        IGoogleCalendarSyncService calendarSync,
        IGoogleTasksSyncService tasksSync,
        IIntegrationRepository integrations,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var integration = await connections.FindAsync(caller.Id, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("not_connected", "No Google account is connected.");

        var profile = await profiles.FindAsync(caller.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(profile?.Timezone))
        {
            throw AppException.BadRequest(
                "timezone_required",
                "Set your timezone before importing — Google items have no timezone of their own.");
        }

        var options = new GoogleSyncOptions(
            profile.Value.Timezone!,
            profile.Value.DefaultTimeOfDay,
            integration.ImportDomain);

        var now = clock.GetUtcNow().UtcDateTime;

        var calendar = await calendarSync.SyncAsync(integration, options, now, cancellationToken).ConfigureAwait(false);
        var tasks = await tasksSync.SyncAsync(integration, options, now, cancellationToken).ConfigureAwait(false);

        // A clean run has to RETRACT the last failure. The worker does this on its
        // own success path, and the token-refresh path happens to as well — but a
        // manual import that ran without either left the old sentence sitting on the
        // row for good. That was survivable only while nothing displayed it; the
        // sheet now shows lastError, so a stale one would tell the user their import
        // is still broken immediately after it worked.
        if (integration.LastError is not null)
        {
            integration.LastError = null;
            integration.UpdatedAt = now;
            await integrations.SaveAsync(integration, cancellationToken).ConfigureAwait(false);
        }

        return Results.Ok(new GoogleSyncResponse
        {
            Integration = integration.ToDto(),
            Calendar = calendar,
            Tasks = tasks,
        });
    }

    /// <summary>
    /// <b>NOT idempotent</b> — a second call is 404 <c>not_connected</c>. Verified
    /// live, and a parity trap: the document-scan DELETE next door IS idempotent.
    /// </summary>
    private static async Task<IResult> DisconnectAsync(
        HttpContext context,
        IGoogleConnectionService connections,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var integration = await connections.FindAsync(caller.Id, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("not_connected", "No Google account is connected.");

        await connections.DisconnectAsync(integration, cancellationToken).ConfigureAwait(false);

        return Results.Ok(new GoogleDisconnectResponse { Removed = true });
    }

    /// <summary>
    /// <c>req.body?.web === true</c>, and nothing else.
    ///
    /// <para>
    /// <b>This route has no zod schema at all</b>, so the typed body binder is the
    /// wrong tool: it answers <c>400 invalid_body</c> to <c>{"web":"yes"}</c>,
    /// <c>{"web":1}</c> and <c>[1,2]</c>, all of which Node accepts and ignores.
    /// Measured against <c>:4200</c>, three shapes and three outcomes:
    /// </para>
    /// <list type="bullet">
    ///   <item>object or array → accepted; only a literal boolean <c>true</c> at
    ///         <c>web</c> counts, everything else is ignored</item>
    ///   <item>bare scalar (<c>5</c>, <c>"x"</c>, <c>null</c>, <c>true</c>) →
    ///         <b>500</b>, because <c>express.json()</c> defaults to
    ///         <c>strict: true</c> and its SyntaxError is unrecognised by the error
    ///         handler</item>
    ///   <item>absent or malformed → <c>{}</c> and 500 respectively, as everywhere else</item>
    /// </list>
    /// </summary>
    private static async Task<bool> ReadWebFlagAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // express.json() parses ONLY application/json and never reads the stream of
        // a request it skips, so a non-JSON body leaves req.body = {} — no parse, no
        // SyntaxError, no 500. Reading it by hand here means this route does NOT
        // inherit KernelBody.ReadAsync<T>'s gate and has to apply it itself.
        //
        // Measured before this gate: form-encoded and text/plain bodies gave
        // dotnet=500 against node=400, because the bytes were parsed as JSON and the
        // failure surfaced as a BodyReadException.
        if (!KernelBody.IsJsonContentType(context.Request))
        {
            return false;
        }

        var bytes = await KernelBody
            .ReadBytesAsync(context.Request, KernelJson.MaxJsonBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            // express.json() sets req.body = {} for a request with no body.
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new BodyReadException("malformed JSON body", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Legal JSON under strict mode, but `req.body?.web` is undefined.
                return false;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                // body-parser's strict mode: only an object or an array may be the
                // top-level value. Anything else throws, and Node answers 500.
                throw new BodyReadException($"strict JSON body must be an object or array, got {root.ValueKind}");
            }

            return root.TryGetProperty("web", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
    }

    /// <summary>True when the server can actually complete a connection end to end.</summary>
    private static bool Ready(GoogleIntegrationOptions options, IGoogleTokenCipher cipher) =>
        options.IsGoogleConfigured && cipher.IsConfigured;

    private static string DeepLink(
        GoogleIntegrationOptions options,
        string status,
        params (string Key, string Value)[] extra)
    {
        var parameters = new List<(string Key, string Value)> { ("status", status) };
        parameters.AddRange(extra);

        return $"{options.DeepLinkScheme}://integrations/google?{GoogleOAuthClient.FormUrlEncode(parameters)}";
    }

    /// <summary>
    /// Node guards every one of these with <c>typeof req.query.x === 'string'</c>, so
    /// a REPEATED parameter (which express parses into an array) reads as absent
    /// rather than as its first value. Reproduced exactly: <c>?state=a&amp;state=b</c>
    /// must land on <c>invalid_state</c>, not verify <c>a</c>.
    /// </summary>
    private static string? SingleQueryValue(HttpContext context, string name)
    {
        var values = context.Request.Query[name];
        return values.Count == 1 ? values[0] : null;
    }
}
