using System.Security.Cryptography;
using System.Text;

namespace Life_Admin_Autopilot_Backend.Kernel.Http;

/// <summary>
/// Express enables weak ETags by default, so <c>res.json()</c> stamps one on
/// every response that carries a body. Kestrel emits none. The frozen contract
/// states it (<c>docs/contract/paths.auth.yaml</c>: "with a weak ETag and
/// Content-Length"), and it showed up on 240 harness steps once the differ began
/// comparing the union of both sides' headers.
///
/// <para><b>The algorithm</b> is the <c>etag</c> npm package's, reproduced exactly
/// and verified byte-for-byte against the reference:
/// <c>W/"&lt;byteLength in lowercase hex&gt;-&lt;first 27 chars of the base64 SHA-1&gt;"</c>.
/// Measured: a body of <c>{"invoices":[]}</c> yields
/// <c>W/"f-d2zQMPzGSUsfWr4GTZsXTHph00M"</c> on both servers.</para>
///
/// <para><b>Which responses get one</b>, measured against the reference rather than
/// assumed — it is NOT "GET 200 only":</para>
/// <list type="bullet">
///   <item>200, 201, 400 and 401 with a JSON body — <b>yes</b>, including on POST</item>
///   <item>204 — <b>no</b>, there is no body to hash</item>
///   <item>the fall-through 404 — <b>no</b>; that response is written by
///         <c>finalhandler</c>, which never sets one</item>
/// </list>
/// So the rule is simply: any response that writes bytes gets an ETag. Our
/// fall-through 404 writes an empty body, so it is excluded by the same rule
/// rather than needing a special case.
///
/// <para><b>Why buffer.</b> An ETag is a hash of the finished body, so the header
/// cannot be set until the body exists. Express has the same constraint —
/// <c>res.send()</c> computes it from the fully-serialised payload. Responses here
/// are small JSON documents, and the one genuinely large payload
/// (<c>GET /me/document-scans/{id}/file</c>) is streamed binary that never reaches
/// this middleware with a JSON content type.</para>
///
/// <para>An ETag already set upstream is left alone, matching Express, which skips
/// generation when the header is present.</para>
/// </summary>
public sealed class WeakETagMiddleware
{
    /// <summary>
    /// The <c>etag</c> package's fast path for an empty entity. Unused here — an
    /// empty body means no ETag at all — but recorded because it is the one case
    /// where the package returns a STRONG tag, and a future change that starts
    /// tagging empty bodies must not invent a weak one.
    /// </summary>
    internal const string EmptyEntityTag = "\"0-2jmj7l5rSw0yVb/vlWAYkK/YBwk\"";

    private readonly RequestDelegate _next;

    public WeakETagMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            if (ShouldTag(context.Response, buffer.Length))
            {
                context.Response.Headers.ETag = Compute(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = original;
        }
    }

    /// <summary>
    /// Express generates the ETag inside <c>res.send()</c>, so anything that writes
    /// its body another way never gets one. Measured on the reference:
    ///
    /// <list type="bullet">
    ///   <item>200, 201, 400, 401 with a JSON body — tagged</item>
    ///   <item>204 — not tagged, there is no body</item>
    ///   <item>the fall-through 404 — not tagged; <c>finalhandler</c> writes it</item>
    ///   <item><b>3xx redirects — NOT tagged</b>, even though they carry a body.
    ///         <c>res.redirect()</c> calls <c>res.end()</c> directly and bypasses
    ///         <c>res.send()</c> entirely. Verified: the Google OAuth callback answers
    ///         302 with a 66-byte <c>Found. Redirecting to …</c> body and no ETag.</item>
    /// </list>
    /// </summary>
    private static bool ShouldTag(HttpResponse response, long bodyLength) =>
        !response.HasStarted
        && bodyLength > 0
        && response.StatusCode is < 300 or >= 400
        && !response.Headers.ContainsKey("ETag");

    /// <summary>
    /// <c>W/"&lt;len:x&gt;-&lt;base64(sha1)[..27]&gt;"</c>. The length is the BYTE count, not the
    /// character count, and the base64 is truncated rather than padded away.
    /// </summary>
    internal static string Compute(ReadOnlySpan<byte> body)
    {
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(body, digest);

        var hash = Convert.ToBase64String(digest)[..27];
        return string.Create(
            null,
            stackalloc char[64],
            $"W/\"{body.Length:x}-{hash}\"");
    }

    /// <summary>Convenience overload for tests and callers holding a string body.</summary>
    internal static string Compute(string body) => Compute(Encoding.UTF8.GetBytes(body));
}
