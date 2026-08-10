using System.Globalization;
using System.Text.Json.Nodes;
using Life_Admin_Autopilot.BLL.Kernel.Json;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Profile;

/// <summary>
/// Renders a raw BSON document the way <c>JSON.stringify(doc)</c> renders a
/// Mongoose <c>.lean()</c> result.
///
/// <para>
/// <b>Why this is not the Mongo extended-JSON writer.</b> <c>ToJson()</c> on a
/// <see cref="BsonDocument"/> emits <c>{"$oid": "…"}</c> and
/// <c>{"$date": …}</c>. A lean document is a plain JavaScript object, so
/// <c>JSON.stringify</c> reaches each value's own <c>toJSON</c> instead:
/// <c>ObjectId</c> becomes its 24-hex string and <c>Date</c> becomes
/// <c>toISOString()</c>. Extended JSON here would change the shape of every
/// exported row.
/// </para>
/// </summary>
public static class LeanDocumentJson
{
    public static JsonObject ToJsonObject(BsonDocument document)
    {
        var node = new JsonObject();
        foreach (var element in document)
        {
            node[element.Name] = ToJsonNode(element.Value);
        }

        return node;
    }

    /// <summary>
    /// One BSON value as the JSON node <c>JSON.stringify</c> would produce.
    ///
    /// <para>
    /// Timestamps go through <see cref="JsIsoDateTimeConverter.ToIso"/> rather than
    /// any built-in ISO format: JavaScript's <c>toISOString()</c> always emits three
    /// fractional digits, and .NET's round-trip format trims them
    /// (<c>.600</c> → <c>.6</c>). Both servers write the same instants, so a trimmed
    /// fraction is a pure formatting divergence — and it appears on every
    /// <c>createdAt</c> in the file.
    /// </para>
    /// </summary>
    public static JsonNode? ToJsonNode(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => null,
        BsonType.ObjectId => JsonValue.Create(value.AsObjectId.ToString()),
        BsonType.DateTime => JsonValue.Create(JsIsoDateTimeConverter.ToIso(value.ToUniversalTime())),
        BsonType.String => JsonValue.Create(value.AsString),
        BsonType.Boolean => JsonValue.Create(value.AsBoolean),
        BsonType.Int32 => JsonValue.Create(value.AsInt32),
        BsonType.Int64 => JsonValue.Create(value.AsInt64),
        BsonType.Double => JsonValue.Create(value.AsDouble),
        BsonType.Decimal128 => JsonValue.Create(
            decimal.TryParse(
                value.AsDecimal128.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0m),
        BsonType.Array => ToJsonArray(value.AsBsonArray),
        BsonType.Document => ToJsonObject(value.AsBsonDocument),

        // Nothing in the eleven exported collections stores these. Falling back to
        // the string form keeps a surprise row in the file rather than 500-ing the
        // whole download.
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonArray ToJsonArray(BsonArray array)
    {
        var node = new JsonArray();
        foreach (var item in array)
        {
            node.Add(ToJsonNode(item));
        }

        return node;
    }
}
