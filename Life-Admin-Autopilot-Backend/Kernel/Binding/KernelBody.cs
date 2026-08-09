using System.Buffers;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Kernel.Binding;

/// <summary>
/// How one route wants its JSON body read.
/// </summary>
/// <param name="Strict">
/// True ONLY when the Node schema being mirrored is <c>.strict()</c>. Unknown
/// top-level keys then fail with <paramref name="Code"/> and a
/// <c>formErrors: ["Unrecognized key(s) in object: 'x'"]</c> detail. Default
/// false, because a plain zod object silently strips unknown keys.
/// </param>
/// <param name="Code">The route's own error code — <c>invalid_body</c>, <c>invalid_review</c>, …</param>
/// <param name="Message">The route's own message, copied verbatim from the Node route.</param>
public readonly record struct KernelBodyOptions(bool Strict, string Code, string Message)
{
    public static KernelBodyOptions Lenient(string message, string code = "invalid_body") =>
        new(false, code, message);

    public static KernelBodyOptions Strict_(string message, string code = "invalid_body") =>
        new(true, code, message);
}

/// <summary>
/// Reads and deserializes a JSON request body the way express does.
///
/// <para><b>Four behaviours that are not ASP.NET defaults.</b></para>
/// <list type="bullet">
///   <item>
///     <b>Only <c>application/json</c> is parsed at all</b> — see
///     <see cref="IsJsonContentType"/>. Every other content type leaves the body
///     as <c>{}</c>.
///   </item>
///   <item>
///     A body over <see cref="KernelJson.MaxJsonBodyBytes"/> (256kb) is a
///     <b>500 <c>internal_error</c></b>, not 413.
///   </item>
///   <item>Malformed JSON is a <b>500 <c>internal_error</c></b>, not 400.</item>
///   <item>
///     An EMPTY body deserializes as <c>{}</c>, because <c>express.json()</c> sets
///     <c>req.body = {}</c> and the route's schema then decides. It is not an error
///     at this layer.
///   </item>
/// </list>
/// </summary>
public static class KernelBody
{
    /// <summary>The one media type <c>express.json()</c> is configured to parse.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Would <c>express.json()</c> parse this request? <c>app.ts</c> calls
    /// <c>express.json({ limit: '256kb' })</c> with no <c>type</c> option, so
    /// body-parser uses its default matcher — <c>type-is</c> against the single
    /// pattern <c>application/json</c>.
    ///
    /// <para><b>What that matcher accepts</b>, all verified live against the Node
    /// reference on <c>POST /auth/signup</c> with an otherwise valid payload:</para>
    ///
    /// <list type="bullet">
    ///   <item><c>application/json</c> → parsed (201).</item>
    ///   <item>
    ///     <c>application/json; charset=utf-8</c> → parsed. Parameters are part of the
    ///     header, not of the media type, so they never affect the match. Getting this
    ///     wrong would break every ordinary browser and <c>fetch()</c> client.
    ///   </item>
    ///   <item><c>APPLICATION/JSON</c> → parsed. Media types are case-insensitive.</item>
    ///   <item>
    ///     <b>Everything else</b> → NOT parsed (400 from the route's own validators):
    ///     an absent header, <c>text/plain</c>, <c>application/x-www-form-urlencoded</c>,
    ///     an unparseable value — and, importantly, the <c>+json</c> structured-suffix
    ///     types <c>application/vnd.api+json</c> and <c>application/json-patch+json</c>.
    ///     The literal pattern <c>application/json</c> does not cover the suffix form;
    ///     only an <c>application/*+json</c> pattern would, and body-parser's default
    ///     is not that.
    ///   </item>
    /// </list>
    ///
    /// <para><b>This is a security control, not only parity.</b> <c>text/plain</c> is a
    /// CORS <i>simple</i> request: a cross-origin page can send one with no preflight.
    /// The content-type gate is what stops such a request from carrying a JSON body
    /// into a state-changing endpoint.</para>
    /// </summary>
    public static bool IsJsonContentType(HttpRequest request)
    {
        var header = request.Headers.ContentType.ToString();

        // Absent, empty, or (when a client sent the header twice) comma-joined into
        // something unparseable. type-is answers false for all three.
        return !string.IsNullOrEmpty(header)
            && Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(header, out var parsed)
            && parsed.MediaType.Equals(JsonMediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raw bytes with the express ceiling applied. Over the limit throws
    /// <see cref="BodyReadException"/> — and the ceiling is checked while reading,
    /// so an enormous body is never buffered whole.
    /// </summary>
    public static async Task<byte[]> ReadBytesAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(maxBytes, 64 * 1024));
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(rented, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.WrittenCount + read > maxBytes)
                {
                    throw new BodyReadException($"request entity too large (limit {maxBytes} bytes)");
                }

                buffer.Write(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The main entry point. Returns a non-null instance of
    /// <typeparamref name="T"/>; a body of <c>{}</c> yields a default-constructed
    /// DTO so the route's own validation, not the binder, reports missing fields.
    /// </summary>
    public static async Task<T> ReadAsync<T>(
        HttpContext context,
        KernelBodyOptions options,
        CancellationToken cancellationToken = default)
        where T : new()
    {
        // The content-type gate comes FIRST, before the body is touched at all.
        // body-parser skips a non-JSON request entirely: it never reads the stream,
        // so neither the 256kb ceiling nor a JSON syntax error can fire. Verified
        // live — the same malformed or 300kb body is a 500 as `application/json`
        // and a plain 400 from the route's validators as `text/plain`. Reading
        // first and gating afterwards would reproduce the status only by accident
        // and get those two cases wrong.
        if (!IsJsonContentType(context.Request))
        {
            return new T();
        }

        var bytes = await ReadBytesAsync(context.Request, KernelJson.MaxJsonBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            return new T();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            // express's body-parser SyntaxError → unrecognised by errorHandler → 500.
            throw new BodyReadException("malformed JSON body", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                // zod reports a non-object body as a whole-object type issue.
                throw AppException.BadRequest(
                    options.Code,
                    options.Message,
                    ValidationDetails.AsFlattened(new[]
                    {
                        ValidationIssue.Form(ZodMessages.ExpectedType("object", KindName(document.RootElement.ValueKind))),
                    }));
            }

            if (options.Strict)
            {
                var unknown = UnknownTopLevelKeys<T>(document.RootElement);
                if (unknown.Count > 0)
                {
                    throw AppException.BadRequest(
                        options.Code,
                        options.Message,
                        ValidationDetails.AsFlattened(new[] { ValidationDetails.UnrecognizedKeys(unknown) }));
                }
            }
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes, KernelJson.Lenient) ?? new T();
        }
        catch (JsonException ex)
        {
            // A wrong TYPE for a known field. zod reports this per field, so key the
            // detail on the top-level path segment the reader was on.
            var field = TopLevelField(ex.Path);
            var issue = field is null
                ? ValidationIssue.Form(ZodMessages.InvalidRegex)
                : ValidationIssue.At(field, ZodMessages.InvalidRegex);

            throw AppException.BadRequest(
                options.Code,
                options.Message,
                ValidationDetails.AsFlattened(new[] { issue }));
        }
    }

    /// <summary>
    /// Top-level JSON property names present in the body that
    /// <typeparamref name="T"/> does not declare. Uses the serializer's own
    /// contract, so <c>[JsonPropertyName]</c> and the camelCase policy are honoured.
    /// </summary>
    public static IReadOnlyList<string> UnknownTopLevelKeys<T>(JsonElement root)
    {
        var known = KernelJson.Lenient
            .GetTypeInfo(typeof(T))
            .Properties
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        return root
            .EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !known.Contains(name))
            .ToList();
    }

    /// <summary>
    /// <c>"$.mic.quality"</c> → <c>"mic"</c>. zod's <c>flatten()</c> keys nested
    /// issues under the TOP-LEVEL field, so this must stop at the first segment.
    /// </summary>
    private static string? TopLevelField(string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || !jsonPath.StartsWith("$.", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = jsonPath[2..];
        var cut = rest.IndexOfAny(new[] { '.', '[' });
        var field = cut < 0 ? rest : rest[..cut];
        return field.Length == 0 ? null : field;
    }

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };
}
