using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.Ai;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>One grounding citation. <c>_id: false</c> upstream, so nothing to rename.</summary>
public sealed class AiConversationSourceDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// One tool invocation, as the conversation read emits it.
///
/// <para>
/// <b><c>result</c> and <c>error</c> are ALWAYS present, as literal <c>null</c>
/// when unset.</b> The Mongoose sub-schema declares <c>default: null</c> for both,
/// and Mongoose applies defaults on hydration as well as on create, so the keys
/// exist even for a document whose stored BSON omits them. The kernel pins
/// <c>WhenWritingNull</c> globally, which would drop them — hence the explicit
/// <c>Never</c> opt-back-in on each.
/// </para>
///
/// <para><c>modelCallId</c> has NO default, so an absent value stays absent.</para>
/// </summary>
public sealed class AiConversationToolCallDto
{
    [JsonPropertyName("callId")]
    public string CallId { get; init; } = string.Empty;

    [JsonPropertyName("modelCallId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelCallId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("args")]
    public JsonNode? Args { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonNode? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Error { get; init; }
}

/// <summary>
/// One turn.
///
/// <para>
/// Built by the ROUTE, not by a Mongoose <c>toJSON</c> transform:
/// <c>sources</c> and <c>toolCalls</c> are coerced from absent to <c>[]</c> here,
/// and <c>createdAt</c> is stringified. A port that serialized the document
/// directly would omit both arrays.
/// </para>
/// </summary>
public sealed class AiConversationMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("sources")]
    public IReadOnlyList<AiConversationSourceDto> Sources { get; init; } = Array.Empty<AiConversationSourceDto>();

    [JsonPropertyName("toolCalls")]
    public IReadOnlyList<AiConversationToolCallDto> ToolCalls { get; init; } = Array.Empty<AiConversationToolCallDto>();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// The envelope both <c>GET /ai/conversation</c> and
/// <c>POST /ai/conversation/reset</c> answer with.
///
/// <para>
/// <c>scopeId</c> is <b>literally null and always present</b>. It is a JS object
/// literal on the Node side, so the key ships whatever its value; the kernel's
/// global <c>WhenWritingNull</c> would silently drop it here.
/// </para>
/// </summary>
public sealed class AiConversationResponse
{
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = AiConversationVocabulary.PersonalScope;

    [JsonPropertyName("scopeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ScopeId { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<AiConversationMessageDto> Messages { get; init; } = Array.Empty<AiConversationMessageDto>();

    /// <summary>The literal body <c>POST /ai/conversation/reset</c> always returns.</summary>
    public static AiConversationResponse Empty { get; } = new();
}

/// <summary>
/// Document → wire. Mirrors the inline <c>.map()</c> in
/// <c>server/src/modules/ai/routes.ts</c>.
/// </summary>
public static class AiConversationMappers
{
    public static AiConversationResponse ToResponse(this AiConversationDocument document) =>
        new()
        {
            // Both hard-coded on the Node route rather than echoed from the
            // document — v1 has exactly one scope and the route says so.
            Scope = AiConversationVocabulary.PersonalScope,
            ScopeId = null,
            Messages = document.Messages.Select(ToDto).ToList(),
        };

    public static AiConversationMessageDto ToDto(this AiConversationMessageDocument message) =>
        new()
        {
            Role = message.Role,
            Text = message.Text,
            Sources = message.Sources?.Select(ToDto).ToList()
                      ?? (IReadOnlyList<AiConversationSourceDto>)Array.Empty<AiConversationSourceDto>(),
            ToolCalls = message.ToolCalls?.Select(ToDto).ToList()
                        ?? (IReadOnlyList<AiConversationToolCallDto>)Array.Empty<AiConversationToolCallDto>(),
            CreatedAt = message.CreatedAt,
        };

    public static AiConversationSourceDto ToDto(this AiConversationSourceDocument source) =>
        new() { Kind = source.Kind, Id = source.SourceId, Title = source.Title };

    public static AiConversationToolCallDto ToDto(this AiConversationToolCallDocument call) =>
        new()
        {
            CallId = call.CallId,
            ModelCallId = call.ModelCallId,
            Name = call.Name,
            Args = BsonJson.ToJsonNode(call.Args),
            Status = call.Status,
            Result = BsonJson.ToJsonNode(call.Result),
            Error = call.Error,
        };
}

/// <summary>
/// BSON → <c>JsonNode</c>, for the two <c>Schema.Types.Mixed</c> fields on a tool
/// call (<c>args</c> and <c>result</c>).
///
/// <para>
/// Hand-rolled rather than routed through the driver's extended-JSON writer:
/// extended JSON renders a <c>Date</c> as <c>{"$date":…}</c> and a long as
/// <c>{"$numberLong":"…"}</c>, neither of which is what <c>res.json()</c> emits.
/// This produces the plain JSON a JavaScript client would see, with dates in the
/// three-fractional-digit <c>toISOString()</c> form the kernel uses everywhere.
/// </para>
/// </summary>
public static class BsonJson
{
    public static JsonNode? ToJsonNode(BsonValue? value)
    {
        if (value is null || value.IsBsonNull)
        {
            return null;
        }

        switch (value.BsonType)
        {
            case BsonType.Document:
                var obj = new JsonObject();
                foreach (var element in value.AsBsonDocument)
                {
                    obj[element.Name] = ToJsonNode(element.Value);
                }

                return obj;

            case BsonType.Array:
                var array = new JsonArray();
                foreach (var item in value.AsBsonArray)
                {
                    array.Add(ToJsonNode(item));
                }

                return array;

            case BsonType.Boolean:
                return JsonValue.Create(value.AsBoolean);

            case BsonType.Int32:
                return JsonValue.Create(value.AsInt32);

            case BsonType.Int64:
                return JsonValue.Create(value.AsInt64);

            case BsonType.Double:
                return JsonValue.Create(value.AsDouble);

            case BsonType.Decimal128:
                return JsonValue.Create((double)value.AsDecimal128);

            case BsonType.DateTime:
                return JsonValue.Create(
                    value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));

            case BsonType.ObjectId:
                return JsonValue.Create(value.AsObjectId.ToString());

            default:
                return JsonValue.Create(value.AsString);
        }
    }
}
