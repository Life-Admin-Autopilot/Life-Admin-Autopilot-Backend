using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// The serializer used for SSE frame payloads. It is deliberately <b>not</b>
/// <c>KernelJson.Lenient</c>.
///
/// <para>
/// <b>The one difference that matters: nulls are written.</b> The response
/// serializer runs <c>DefaultIgnoreCondition = WhenWritingNull</c> because Mongoose
/// omits unset optional fields. A <c>tool_result</c> frame needs the opposite —
/// <c>result</c> and <c>error</c> are BOTH always present with the unused one an
/// explicit <c>null</c>, and the client branches on <c>error !== null</c>. Serialising
/// a frame with the response options silently drops the key and every failed tool
/// call reads as a success.
/// </para>
///
/// <para>
/// Everything else matches: camelCase names, relaxed escaping so a non-ASCII token
/// is not turned into <c>\uXXXX</c> mid-answer, and the JS ISO date shape.
/// </para>
/// </summary>
public static class AiStreamJson
{
    public static readonly JsonSerializerOptions Frame = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,

            // See the class remarks. NOT WhenWritingNull.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,

            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };

        options.Converters.Add(new JsIsoDateTimeConverter());
        options.Converters.Add(new JsIsoNullableDateTimeConverter());
        return options;
    }
}
