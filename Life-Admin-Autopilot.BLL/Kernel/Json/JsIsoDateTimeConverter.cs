using System.Text.Json;
using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Kernel.Json;

/// <summary>
/// Writes a <see cref="DateTime"/> exactly the way JavaScript's
/// <c>Date#toISOString()</c> does: UTC, ALWAYS three fractional digits, always a
/// <c>Z</c> suffix.
///
/// <para>
/// This is not cosmetic. System.Text.Json's default round-trip format trims
/// trailing zeros in the fraction, so an instant stored as <c>…:22.600</c> ships
/// as <c>"…:22.6Z"</c> where Node ships <c>"…:22.600Z"</c>, and an instant with no
/// fraction ships as <c>"…:22Z"</c> where Node ships <c>"…:22.000Z"</c>. Both are
/// byte-level parity failures on timestamps that appear in nearly every response.
/// </para>
/// </summary>
public sealed class JsIsoDateTimeConverter : JsonConverter<DateTime>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToIso(value));

    public static string ToIso(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Mongo hands back Unspecified in a few code paths; it is always UTC.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return utc.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Nullable companion. Registered alongside the non-nullable converter.</summary>
public sealed class JsIsoNullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTime().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(JsIsoDateTimeConverter.ToIso(value.Value));
    }
}
