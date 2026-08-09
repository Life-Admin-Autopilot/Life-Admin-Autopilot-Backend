using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Json;

namespace Life_Admin_Autopilot_Backend.Kernel.Json;

/// <summary>
/// The serializer options the whole server uses. There are exactly two, and
/// picking the wrong one for a request body is a silent parity break.
///
/// <para>
/// <b>Read this before choosing.</b> zod objects are LENIENT by default — they
/// STRIP unknown keys and succeed. Only a schema marked <c>.strict()</c> rejects
/// them. In the Node source, <c>.strict()</c> appears on the me.tasks bodies, the
/// me.tasks/me.digest query schemas, and nothing else; every other body
/// (auth.signup, auth.signin, PATCH /me, ai, clarifications, voice-notes,
/// document-scans, ics-feeds, notifications) silently ignores extra keys.
/// Verified live: <c>POST /auth/signup</c> with an extra <c>"bogus":1</c> returns
/// <b>201</b>, not 400.
/// </para>
///
/// <para>
/// So: <see cref="Lenient"/> is the DEFAULT for bodies, and
/// <see cref="Strict"/> is opt-in per DTO. Use <see cref="Strict"/> only where the
/// Node schema you are mirroring carries <c>.strict()</c>.
/// </para>
/// </summary>
public static class KernelJson
{
    /// <summary>
    /// Express's <c>express.json({ limit: '256kb' })</c>. Exceeding it is a 500
    /// <c>internal_error</c>, not a 413 — see
    /// <see cref="Life_Admin_Autopilot.DAL.Kernel.Errors.BodyReadException"/>.
    /// </summary>
    public const int MaxJsonBodyBytes = 256 * 1024;

    /// <summary>
    /// Response serialization and the default for request bodies. Unknown members
    /// are skipped, exactly as a plain zod object does.
    /// </summary>
    public static readonly JsonSerializerOptions Lenient = Build(strict: false);

    /// <summary>
    /// Request bodies whose Node counterpart is <c>.strict()</c>. An unmapped
    /// member throws <see cref="JsonException"/>, which the strict body binder
    /// converts into the <c>invalid_body</c> +
    /// <c>{formErrors:["Unrecognized key(s) in object: 'x'"],fieldErrors:{}}</c>
    /// envelope zod produces.
    /// </summary>
    public static readonly JsonSerializerOptions Strict = Build(strict: true);

    private static JsonSerializerOptions Build(bool strict)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,

            // Mongoose omits unset optional fields entirely. DTOs already carry
            // per-property [JsonIgnore(WhenWritingNull)]; this is the backstop.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Express's res.json() does not escape non-ASCII. Escaping would turn
            // every Arabic title into \uXXXX and break byte comparison.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = strict
                ? JsonUnmappedMemberHandling.Disallow
                : JsonUnmappedMemberHandling.Skip,
        };

        options.Converters.Add(new JsIsoDateTimeConverter());
        options.Converters.Add(new JsIsoNullableDateTimeConverter());
        return options;
    }
}
