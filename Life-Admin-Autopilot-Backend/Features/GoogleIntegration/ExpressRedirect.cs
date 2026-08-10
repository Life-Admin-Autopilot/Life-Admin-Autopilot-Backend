using System.Net;
using System.Text;

namespace Life_Admin_Autopilot_Backend.Features.GoogleIntegration;

/// <summary>
/// Express's <c>res.redirect(302, url)</c>, body and all.
///
/// <para>
/// ASP.NET's <c>Results.Redirect</c> writes a bare <c>Location</c> header and an
/// empty body. Express writes a body too, chosen by <c>res.format</c>, and the
/// parity harness compares <c>Content-Type</c>, byte count and body text — so a
/// bodyless 302 fails three assertions on a row whose status already matched.
/// </para>
///
/// <para><b>All three branches captured live against <c>:4200</c>:</b></para>
/// <list type="bullet">
///   <item>
///     no <c>Accept</c>, or <c>*/*</c> → <c>text/plain; charset=utf-8</c>,
///     <c>Found. Redirecting to &lt;url&gt;</c>
///   </item>
///   <item>
///     <c>Accept: text/html</c> → <c>text/html; charset=utf-8</c>,
///     <c>&lt;p&gt;Found. Redirecting to &lt;url&gt;&lt;/p&gt;</c> — note Express 5
///     dropped the <c>&lt;a&gt;</c> element Express 4 emitted
///   </item>
///   <item>
///     <c>Accept: application/json</c> → <b>no</b> <c>Content-Type</c>,
///     <c>Content-Length: 0</c>, empty body
///   </item>
/// </list>
/// </summary>
public static class ExpressRedirect
{
    /// <summary>
    /// Writes the whole response. The URL is built entirely from server-side
    /// constants and configuration — no request input reaches it — but the HTML
    /// branch still runs it through <see cref="WebUtility.HtmlEncode"/>, because
    /// Express's <c>escapeHtml</c> does and a future caller must not be one edit away
    /// from a reflected-XSS hole.
    /// </summary>
    public static async Task FoundAsync(HttpContext context, string url, CancellationToken cancellationToken = default)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status302Found;
        response.Headers.Location = url;

        // res.format() varies on Accept. The kernel's CORS middleware has already
        // appended Origin, giving Express's exact "Vary: Origin, Accept".
        AppendVary(response, "Accept");

        (string? Type, string Content) body = Negotiate(context.Request.Headers.Accept.ToString()) switch
        {
            BodyKind.Text => ("text/plain; charset=utf-8", $"Found. Redirecting to {url}"),
            BodyKind.Html => ("text/html; charset=utf-8", $"<p>Found. Redirecting to {WebUtility.HtmlEncode(url)}</p>"),
            _ => (null, string.Empty),
        };

        var bytes = Encoding.UTF8.GetBytes(body.Content);
        if (body.Type is not null)
        {
            response.ContentType = body.Type;
        }

        response.ContentLength = bytes.Length;

        if (bytes.Length > 0)
        {
            await response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private enum BodyKind
    {
        Text,
        Html,
        None,
    }

    /// <summary>
    /// <c>req.accepts(['text', 'html'])</c>. An absent header accepts everything and
    /// yields the FIRST offered type, which is why the common case is text/plain.
    /// Ties also go to text, because <c>res.format</c> lists it first.
    /// </summary>
    private static BodyKind Negotiate(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept))
        {
            return BodyKind.Text;
        }

        var text = Quality(accept, "text", "plain");
        var html = Quality(accept, "text", "html");

        if (text <= 0 && html <= 0)
        {
            return BodyKind.None;
        }

        return html > text ? BodyKind.Html : BodyKind.Text;
    }

    private static double Quality(string accept, string type, string subtype)
    {
        var best = 0d;

        foreach (var raw in accept.Split(','))
        {
            var parts = raw.Split(';');
            var media = parts[0].Trim();
            if (media.Length == 0)
            {
                continue;
            }

            var slash = media.IndexOf('/');
            var mediaType = slash < 0 ? media : media[..slash];
            var mediaSubtype = slash < 0 ? "*" : media[(slash + 1)..];

            var typeMatches = mediaType == "*" || mediaType.Equals(type, StringComparison.OrdinalIgnoreCase);
            var subtypeMatches = mediaSubtype == "*" || mediaSubtype.Equals(subtype, StringComparison.OrdinalIgnoreCase);
            if (!typeMatches || !subtypeMatches)
            {
                continue;
            }

            var q = 1d;
            foreach (var parameter in parts.Skip(1))
            {
                var trimmed = parameter.Trim();
                if (trimmed.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(
                        trimmed[2..],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    q = parsed;
                }
            }

            if (q > best)
            {
                best = q;
            }
        }

        return best;
    }

    private static void AppendVary(HttpResponse response, string value)
    {
        var current = response.Headers.Vary.ToString();
        response.Headers.Vary = string.IsNullOrEmpty(current) ? value : $"{current}, {value}";
    }
}
