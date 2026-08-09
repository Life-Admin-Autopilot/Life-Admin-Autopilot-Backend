using System.Globalization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Kernel.Binding;

/// <summary>
/// A raw binary upload plus its <c>x-*</c> header metadata.
/// </summary>
/// <param name="Bytes">The whole body. Already checked against the friendly limit.</param>
/// <param name="ContentType">
/// The content type with any <c>;charset=…</c> stripped, matching Node's
/// <c>(req.header('content-type') ?? '').split(';')[0]?.trim()</c>.
/// </param>
public readonly record struct RawUpload(byte[] Bytes, string? ContentType);

/// <summary>
/// The frontend posts RAW BYTES with metadata in headers — NOT multipart. Node
/// uses <c>express.raw()</c> plus <c>x-voice-note-*</c> / <c>x-document-scan-*</c>
/// headers to keep the request a single binary body rather than dragging multer
/// in for one field. ASP.NET's form/multipart binding does not apply; this reader
/// is the whole story.
///
/// <para>
/// <b>The two-ceiling trick, ported deliberately.</b> Node sets the
/// <c>express.raw()</c> limit to <c>maxBytes * 2</c> and then checks
/// <c>bytes.length &gt; maxBytes</c> by hand, so a normally-oversize upload gets a
/// friendly <c>400 payload_too_large</c> instead of body-parser's terse 413.
/// Only a body over TWICE the limit trips the transport ceiling — and that one
/// becomes a 500, like every other body-read failure.
/// Call <see cref="ReadAsync"/> with the friendly limit and it does both.
/// </para>
/// </summary>
public static class RawBodyReader
{
    /// <summary>
    /// Reads the body, applying the hard ceiling at <c>2 × maxBytes</c> and the
    /// friendly one at <c>maxBytes</c>.
    /// </summary>
    /// <param name="emptyCode">Node uses <c>empty_body</c>.</param>
    /// <param name="emptyMessage">e.g. <c>"No audio payload received."</c></param>
    /// <param name="tooLargeMessage">e.g. <c>"Voice note exceeds 5MB."</c></param>
    public static async Task<RawUpload> ReadAsync(
        HttpContext context,
        int maxBytes,
        string emptyMessage,
        string tooLargeMessage,
        string emptyCode = "empty_body",
        string tooLargeCode = "payload_too_large",
        CancellationToken cancellationToken = default)
    {
        // Over 2x → BodyReadException → 500, exactly like body-parser's own limit.
        var bytes = await KernelBody
            .ReadBytesAsync(context.Request, maxBytes * 2, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            throw AppException.BadRequest(emptyCode, emptyMessage);
        }

        if (bytes.Length > maxBytes)
        {
            throw AppException.BadRequest(tooLargeCode, tooLargeMessage);
        }

        var contentType = context.Request.Headers.ContentType.ToString().Split(';')[0].Trim();
        return new RawUpload(bytes, contentType.Length == 0 ? null : contentType);
    }

    /// <summary>
    /// Reads <c>x-*</c> metadata headers with zod-shaped validation.
    ///
    /// <para>Accumulate every issue, then call
    /// <see cref="HeaderReader.ThrowIfInvalid"/> once — Node reports them together
    /// under <c>invalid_metadata</c>.</para>
    /// </summary>
    public static HeaderReader Headers(HttpContext context) => new(context.Request);
}

/// <summary>Companion to <see cref="QueryReader"/>, for <c>x-*</c> metadata headers.</summary>
public sealed class HeaderReader
{
    private readonly HttpRequest _request;
    private readonly List<ValidationIssue> _issues = new();

    public HeaderReader(HttpRequest request)
    {
        _request = request;
    }

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>
    /// <paramml name="field"/> is the SCHEMA field name (e.g. <c>durationMs</c>), not
    /// the header name — Node builds the zod object from the header values, so the
    /// <c>fieldErrors</c> keys are the schema's.
    /// </summary>
    public void ThrowIfInvalid(string code, string message)
    {
        if (_issues.Count == 0)
        {
            return;
        }

        throw AppException.BadRequest(code, message, ValidationDetails.AsFlattened(_issues));
    }

    public string? String(string header, string field, bool required = true, int? maxLength = null)
    {
        var raw = _request.Headers[header].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            if (required)
            {
                _issues.Add(ValidationIssue.At(field, ZodMessages.Required));
            }

            return null;
        }

        if (maxLength.HasValue && raw.Length > maxLength.Value)
        {
            _issues.Add(ValidationIssue.At(field, ZodMessages.TooLong(maxLength.Value)));
            return null;
        }

        return raw;
    }

    public string? Enum(string header, string field, IReadOnlyList<string> allowed, string? fallback = null)
    {
        var raw = _request.Headers[header].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        if (!allowed.Contains(raw, StringComparer.Ordinal))
        {
            _issues.Add(ValidationIssue.At(field, ZodMessages.InvalidEnum(allowed, raw)));
            return fallback;
        }

        return raw;
    }

    public int? Int(string header, string field, int? min = null, int? max = null, bool required = true)
    {
        var raw = _request.Headers[header].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            if (required)
            {
                _issues.Add(ValidationIssue.At(field, ZodMessages.Required));
            }

            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            _issues.Add(ValidationIssue.At(field, ZodMessages.ExpectedType("number", "nan")));
            return null;
        }

        if (min.HasValue && value < min.Value)
        {
            _issues.Add(ValidationIssue.At(field, ZodMessages.NumberTooSmall(min.Value)));
            return null;
        }

        if (max.HasValue && value > max.Value)
        {
            _issues.Add(ValidationIssue.At(field, ZodMessages.NumberTooBig(max.Value)));
            return null;
        }

        return value;
    }

    public DateTime? IsoDate(string header, string field, bool required = true)
    {
        var raw = _request.Headers[header].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            if (required)
            {
                _issues.Add(ValidationIssue.At(field, ZodMessages.Required));
            }

            return null;
        }

        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            _issues.Add(ValidationIssue.At(field, ZodMessages.InvalidDatetime));
            return null;
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }
}
