using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Kernel.Errors;

/// <summary>The one and only error body shape: <c>{"error":{"code","message","details"?}}</c>.</summary>
public sealed class ErrorEnvelope
{
    [JsonPropertyName("error")]
    public ErrorEnvelopeBody Error { get; init; } = new();
}

public sealed class ErrorEnvelopeBody
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "internal_error";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "Internal server error";

    /// <summary>Omitted entirely when absent — never emitted as <c>"details": null</c>.</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }
}

/// <summary>
/// Port of <c>server/src/middleware/errorHandler.ts</c>. The branch ORDER below is
/// the contract — an earlier branch shadows a later one, and Node's order is:
///
/// <list type="number">
///   <item>Mongoose <c>CastError</c> on an ObjectId → <b>404 <c>not_found</c></b>
///         "Not found". A malformed <c>:id</c> is a "no such resource", not a
///         server fault, and this saves every handler hand-validating.</item>
///   <item><c>ZodError</c> → <b>400 <c>validation_error</c></b> with the
///         <c>[{path,message}]</c> array.</item>
///   <item><c>AppError</c> → its OWN status/code/message, details when present.</item>
///   <item>anything else → <b>500 <c>internal_error</c></b> "Internal server error".</item>
/// </list>
///
/// <para>
/// <b>Two deliberate non-defaults.</b> Malformed JSON and an over-256kb body both
/// land in branch 4 as <b>500</b>, because express's body-parser throws a
/// <c>SyntaxError</c>/<c>PayloadTooLargeError</c> that the Node handler does not
/// recognise. Both verified live. ASP.NET would answer 400/413, so
/// <see cref="BodyReadException"/> is mapped here on purpose.
/// </para>
///
/// <para>
/// <b>There is no catch-all route.</b> An unknown path or wrong method must return
/// the framework's default HTML 404, NOT this envelope — also verified live. Do
/// not add a fallback endpoint.
/// </para>
/// </summary>
public sealed class KernelErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<KernelErrorMiddleware> _logger;

    public KernelErrorMiddleware(RequestDelegate next, ILogger<KernelErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                // Nothing can be done — the status line is already on the wire.
                _logger.LogError(ex, "request:error-after-response-started path={Path}", context.Request.Path);
                throw;
            }

            var (status, envelope) = Translate(ex, context);

            if (status >= 500)
            {
                _logger.LogError(ex, "request:unhandled path={Path}", context.Request.Path);
            }

            await WriteAsync(context, status, envelope);
        }
    }

    public static (int Status, ErrorEnvelope Envelope) Translate(Exception ex, HttpContext context)
    {
        switch (ex)
        {
            // 1. Malformed ObjectId in a route/body id → 404, mirroring CastError.
            case ObjectIdCastException:
                return (404, Envelope("not_found", "Not found"));

            // 2. An uncaught schema failure on a `.parse()` route.
            case ValidationException validation:
                return (
                    400,
                    Envelope(
                        ValidationException.ErrorCode,
                        ValidationException.ErrorMessage,
                        ValidationDetails.AsPathMessageArray(validation.Issues)));

            // 3. Anything a slice threw on purpose.
            case AppException app:
                return (app.Status, Envelope(app.Code, app.Message, app.Details));

            // 4a. Body could not be read: malformed JSON or over the size ceiling.
            //     500 on purpose — see the class remarks.
            case BodyReadException:
            case JsonException:
            case BadHttpRequestException:
            case InvalidDataException:
                return (500, Envelope("internal_error", "Internal server error"));

            // 4b. Everything else.
            default:
                return (500, Envelope("internal_error", "Internal server error"));
        }
    }

    public static ErrorEnvelope Envelope(string code, string message, object? details = null) =>
        new() { Error = new ErrorEnvelopeBody { Code = code, Message = message, Details = details } };

    /// <summary>
    /// Renders the envelope the way Express's error handler does: it replaces the
    /// <b>body</b>, not the headers.
    ///
    /// <para>
    /// <b>Deliberately NOT <c>Response.Clear()</c>.</b> Clear wipes every header set
    /// before the throw, which is wrong on three counts, each verified against the
    /// reference: it strips helmet's twelve security headers off every error response;
    /// it strips the 429's <c>Retry-After</c> and the <c>RateLimit-Policy/Limit/</c>
    /// <c>Remaining/Reset</c> family, which the rate limiter sets immediately before
    /// throwing its <c>AppException</c>; and it drops the CORS <c>Vary</c> /
    /// <c>Access-Control-Allow-Credentials</c> pair, so a browser cannot even read the
    /// error it was sent. In Node all of those survive, because <c>res.json()</c> only
    /// sets status, content-type and length.
    /// </para>
    ///
    /// <para>
    /// The three headers <c>res.json()</c> DOES overwrite are handled explicitly below.
    /// <c>Content-Length</c> is nulled rather than computed so Kestrel recalculates it
    /// for the envelope — leaving a stale length from a handler that set one before
    /// throwing would truncate or hang the response.
    /// </para>
    /// </summary>
    public static async Task WriteAsync(HttpContext context, int status, ErrorEnvelope envelope)
    {
        context.Response.StatusCode = status;

        // Express's res.json() charset, byte for byte.
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = null;

        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, KernelJson.Lenient);
    }
}
