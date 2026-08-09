using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Binding;

namespace Life_Admin_Autopilot_Backend.Features.Auth;

/// <summary>Shared plumbing for the auth endpoints.</summary>
public static class AuthRequestContext
{
    /// <summary>Node's <c>MAX_USER_AGENT</c>.</summary>
    private const int MaxUserAgentLength = 256;

    /// <summary>
    /// Port of <c>lib/requestMeta.ts</c>. The User-Agent is truncated to 256
    /// characters at capture time, and the IP is the RAW socket peer — no
    /// <c>X-Forwarded-For</c>, because the reference does not enable
    /// <c>trust proxy</c> and reading the header would let any client forge it.
    /// </summary>
    public static SessionMeta SessionMeta(this HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var ip = context.Connection.RemoteIpAddress?.ToString();

        return new SessionMeta(
            string.IsNullOrEmpty(userAgent)
                ? null
                : userAgent[..Math.Min(userAgent.Length, MaxUserAgentLength)],
            string.IsNullOrEmpty(ip) ? null : ip);
    }

    /// <summary>
    /// Reads the body as raw JSON members. Every auth schema is lenient, so the
    /// options here only ever matter for the non-object and malformed cases, which
    /// the kernel handles identically for all of them.
    /// </summary>
    public static Task<RawJsonBody> ReadBodyAsync(this HttpContext context, CancellationToken cancellationToken) =>
        KernelBody.ReadAsync<RawJsonBody>(
            context,
            KernelBodyOptions.Lenient("Request validation failed", ValidationException.ErrorCode),
            cancellationToken);

    /// <summary>
    /// The <c>.parse()</c> lane: an invalid body throws <c>ValidationException</c>,
    /// which the kernel middleware renders as <c>400 validation_error</c> with the
    /// ISSUE-ARRAY details shape. Used by every auth route except the four that
    /// mirror <c>safeParse</c>.
    /// </summary>
    public static void ThrowIfInvalid(this ZodBody body)
    {
        if (!body.IsValid)
        {
            throw new ValidationException(body.Issues);
        }
    }
}
