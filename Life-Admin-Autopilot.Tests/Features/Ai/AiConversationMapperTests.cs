using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot_Backend.Kernel.Json;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The conversation wire shape, serialized through the kernel's own options.
///
/// <para>
/// These are the assertions the harness cannot make: reaching a NON-EMPTY
/// conversation needs a configured model, so the shape was verified by seeding an
/// identical document into both parity databases and diffing the two reads. These
/// tests pin what that differential established.
/// </para>
/// </summary>
public sealed class AiConversationMapperTests
{
    [Fact]
    public void serializes_scope_id_as_an_explicit_null()
    {
        // The kernel pins WhenWritingNull globally, and this key is a JS object
        // literal on the Node side — it ships whatever its value. Dropping it is
        // exactly the bug slice C hit on `proposal`.
        var json = Serialize(new AiConversationDocument().ToResponse());

        Assert.Contains("\"scopeId\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void answers_the_literal_empty_envelope()
    {
        Assert.Equal(
            """{"scope":"personal","scopeId":null,"messages":[]}""",
            Serialize(AiConversationResponse.Empty));
    }

    [Fact]
    public void coerces_absent_sources_and_tool_calls_to_empty_arrays()
    {
        // Both are `default: undefined` in Mongoose, so nothing is stored. The
        // ROUTE, not the model, supplies the []. A port that serialized the
        // document directly would omit both keys.
        var document = new AiConversationDocument
        {
            Messages = { new AiConversationMessageDocument { Role = "user", Text = "hi" } },
        };

        var message = document.ToResponse().Messages.Single();

        Assert.Empty(message.Sources);
        Assert.Empty(message.ToolCalls);
        Assert.Contains("\"sources\":[],\"toolCalls\":[]", Serialize(document.ToResponse()), StringComparison.Ordinal);
    }

    [Fact]
    public void keeps_result_and_error_as_explicit_nulls_but_omits_an_absent_model_call_id()
    {
        // `result` and `error` are declared `default: null`; Mongoose applies
        // defaults on hydration, so both keys exist even when the stored BSON has
        // neither. `modelCallId` has no default, so an absent value stays absent.
        var document = new AiConversationDocument
        {
            Messages =
            {
                new AiConversationMessageDocument
                {
                    Role = "assistant",
                    Text = string.Empty,
                    ToolCalls = new List<AiConversationToolCallDocument>
                    {
                        new()
                        {
                            CallId = "c1",
                            Name = "deleteAllTasks",
                            Args = new BsonDocument { ["scope"] = "all" },
                            Status = "pending_confirmation",
                        },
                    },
                },
            },
        };

        var json = Serialize(document.ToResponse());

        Assert.Contains("\"result\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"error\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("modelCallId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void emits_a_present_model_call_id()
    {
        var call = new AiConversationToolCallDocument
        {
            CallId = "c1",
            ModelCallId = "gemini-call-7",
            Name = "snoozeTask",
            Args = new BsonDocument(),
            Status = "declined",
        };

        Assert.Contains("\"modelCallId\":\"gemini-call-7\"", Serialize(call.ToDto()), StringComparison.Ordinal);
    }

    [Fact]
    public void reads_a_source_id_from_the_plain_id_field()
    {
        // Regression: naming this member `Id` makes the driver's
        // NamedIdMemberConvention claim it as the document key and map it to `_id`,
        // which this sub-schema does not have. Every source then reads back with an
        // empty id and nothing errors. Caught by the live differential.
        var source = new AiConversationSourceDocument
        {
            Kind = "task",
            SourceId = "6a7900000000000000000011",
            Title = "Renew passport",
        };

        Assert.Equal(
            """{"kind":"task","id":"6a7900000000000000000011","title":"Renew passport"}""",
            Serialize(source.ToDto()));
    }

    [Fact]
    public void renders_mixed_tool_args_as_plain_json()
    {
        // Extended JSON would render these as {"$numberLong":…} / {"$date":…};
        // res.json() renders plain values, and a date as a three-digit ISO string.
        var call = new AiConversationToolCallDocument
        {
            CallId = "c1",
            Name = "queryTasks",
            Status = "executed",
            Args = new BsonDocument
            {
                ["limit"] = 20,
                ["big"] = 9_000_000_000L,
                ["ratio"] = 2.5,
                ["flag"] = false,
                ["tags"] = new BsonArray { "home", "car" },
                ["nested"] = new BsonDocument { ["deep"] = BsonNull.Value },
                ["at"] = new BsonDateTime(new DateTime(2026, 8, 5, 7, 0, 0, DateTimeKind.Utc)),
                ["oid"] = ObjectId.Parse("6a7900000000000000000011"),
            },
            Result = new BsonDocument { ["count"] = 2 },
        };

        var json = Serialize(call.ToDto());

        Assert.Contains("\"limit\":20", json, StringComparison.Ordinal);
        Assert.Contains("\"big\":9000000000", json, StringComparison.Ordinal);
        Assert.Contains("\"ratio\":2.5", json, StringComparison.Ordinal);
        Assert.Contains("\"flag\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"tags\":[\"home\",\"car\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"deep\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"at\":\"2026-08-05T07:00:00.000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"oid\":\"6a7900000000000000000011\"", json, StringComparison.Ordinal);
        Assert.Contains("\"result\":{\"count\":2}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void emits_created_at_in_the_three_digit_js_iso_form()
    {
        // STJ's default trims .600 to .6, which is a parity break.
        var document = new AiConversationDocument
        {
            Messages =
            {
                new AiConversationMessageDocument
                {
                    Role = "user",
                    Text = "hi",
                    CreatedAt = new DateTime(2026, 8, 1, 9, 15, 22, 600, DateTimeKind.Utc),
                },
            },
        };

        Assert.Contains(
            "\"createdAt\":\"2026-08-01T09:15:22.600Z\"",
            Serialize(document.ToResponse()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void hard_codes_the_scope_rather_than_echoing_the_document()
    {
        // The Node route writes `scope: 'personal' as const`; it never reads the
        // stored value. A document written with something else still reports
        // `personal`.
        var document = new AiConversationDocument { Scope = "team", ScopeId = "abc" };

        var response = document.ToResponse();

        Assert.Equal("personal", response.Scope);
        Assert.Null(response.ScopeId);
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, KernelJson.Lenient);
}
