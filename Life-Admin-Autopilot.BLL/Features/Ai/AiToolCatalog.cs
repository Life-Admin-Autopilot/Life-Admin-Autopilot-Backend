using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// Port of the closed vocabulary half of <c>server/src/modules/ai/toolRunner.ts</c>
/// — the eleven tool names, which of them pauses for a human, and the argument
/// re-validation that runs on recovery from the durable record.
///
/// <para>
/// <b>Only <c>deleteAllTasks</c> requires confirmation.</b> Every per-item mutation
/// runs inline; the bulk wipe alone keeps the guard because deleting every matter
/// in one call is the one action an undo toast cannot console anyone about. That
/// single fact drives <c>needsConfirmation</c> on the <c>tool_call</c> frame, the
/// existence of a pending record at all, and the whole confirm route.
/// </para>
///
/// <para>
/// <b>Why validation lives here and not only at emission time.</b> The confirm
/// route reconstructs the call from the persisted <c>AiConversation</c> tool-call
/// record, which may have been written by an older build, by a different agent
/// revision, or an hour before a deploy. Those args are re-parsed before any
/// destructive dispatch — defence in depth against a record that no longer matches
/// the schema the tool expects.
/// </para>
/// </summary>
public static class AiToolCatalog
{
    public const string DeleteAllTasks = "deleteAllTasks";

    /// <summary><c>TOOL_NAMES</c>, in source order.</summary>
    public static readonly IReadOnlyList<string> ToolNames = new[]
    {
        "createTask",
        "updateTask",
        "completeTask",
        "deleteTask",
        DeleteAllTasks,
        "snoozeTask",
        "queryTasks",
        "addSubtask",
        "toggleSubtask",
        "removeSubtask",
        "holdForClarification",
    };

    /// <summary><c>isKnownTool</c>. An unknown name aborts the turn with an error frame.</summary>
    public static bool IsKnownTool(string name) => ToolNames.Contains(name, StringComparer.Ordinal);

    /// <summary><c>requiresConfirmation</c> — one name, and it is not a list.</summary>
    public static bool RequiresConfirmation(string name) =>
        string.Equals(name, DeleteAllTasks, StringComparison.Ordinal);

    /// <summary>
    /// <c>validateToolArgs</c>. Throws
    /// <c>400 invalid_tool_args</c> whose MESSAGE is the JSON-stringified zod
    /// <c>{formErrors,fieldErrors}</c> flatten — Node passes the serialized object as
    /// the message, not as <c>details</c>, and the client surfaces it verbatim.
    ///
    /// <para>
    /// Only <c>deleteAllTasks</c> is modelled: it is the only tool that can reach a
    /// durable pending record, so it is the only one this method is ever asked
    /// about. Any other known name returns its args untouched rather than
    /// pretending to have validated them — <see cref="RequiresConfirmation"/> then
    /// refuses the dispatch, exactly as Node's <c>runConfirmedTool</c> does.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ValidateArgs(string name, BsonValue? args)
    {
        var supplied = args as BsonDocument ?? new BsonDocument();

        if (!string.Equals(name, DeleteAllTasks, StringComparison.Ordinal))
        {
            return ToPlainMap(supplied);
        }

        var fieldErrors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var domain = ReadOptionalEnum(supplied, "domain", TaskVocabulary.Domains, fieldErrors);

        // `status` OR `status_filter`, and the second name is not cosmetic.
        //
        // Langflow 1.11.2 refuses any component input called `status`: the name collides
        // with `Component.status`, the build-status slot the canvas renders, and
        // `Component.set_attributes` raises "reserved word" before the graph is built.
        // The tool ARGUMENT name is derived from the input name with no renaming layer
        // (`lfx.base.tools.component_tool._build_output_function`), so the planning flow's
        // `deleteAllTasks` necessarily puts `status_filter` on the wire.
        //
        // Reading only `status` would not fail loudly — it would silently widen the
        // filter. A user who confirmed "delete all my DONE tasks" would have every task
        // in that domain deleted, because the unrecognised key is stripped below. Both
        // names are accepted so a record written by either flow revision replays
        // correctly.
        var status = supplied.Contains("status")
            ? ReadOptionalEnum(supplied, "status", TaskVocabulary.Statuses, fieldErrors)
            : ReadOptionalEnum(supplied, "status_filter", TaskVocabulary.Statuses, fieldErrors);

        if (fieldErrors.Count > 0)
        {
            throw AppException.BadRequest("invalid_tool_args", Flatten(fieldErrors));
        }

        // A plain zod object STRIPS unknown keys. `deleteAllTasks` relies on that:
        // the tool_call frame carries a display-only `count` the confirmation card
        // renders, and it must not reach the bulk filter.
        var validated = new Dictionary<string, object?>(2, StringComparer.Ordinal);
        if (domain is not null)
        {
            validated["domain"] = domain;
        }

        if (status is not null)
        {
            validated["status"] = status;
        }

        return validated;
    }

    /// <summary>
    /// <c>JSON.stringify(result.error.flatten())</c> — <c>formErrors</c> first, then
    /// <c>fieldErrors</c>, both always present.
    /// </summary>
    private static string Flatten(IReadOnlyDictionary<string, List<string>> fieldErrors)
    {
        var flattened = new Dictionary<string, object>(2, StringComparer.Ordinal)
        {
            ["formErrors"] = Array.Empty<string>(),
            ["fieldErrors"] = fieldErrors,
        };

        return JsonSerializer.Serialize(flattened, AiStreamJson.Frame);
    }

    private static string? ReadOptionalEnum(
        BsonDocument args,
        string field,
        IReadOnlyList<string> allowed,
        IDictionary<string, List<string>> fieldErrors)
    {
        if (!args.TryGetValue(field, out var value) || value.IsBsonNull)
        {
            // `.optional()` accepts an absent key. A stored explicit null is treated
            // as absent too: it is what Mongo hands back for a field the agent chose
            // not to set, and rejecting it would strand old records forever.
            return null;
        }

        if (value.BsonType != BsonType.String)
        {
            Add(fieldErrors, field, $"Expected {Quoted(allowed)}, received {JsTypeName(value)}");
            return null;
        }

        var text = value.AsString;
        if (!allowed.Contains(text, StringComparer.Ordinal))
        {
            Add(fieldErrors, field, $"Invalid enum value. Expected {Quoted(allowed)}, received '{text}'");
            return null;
        }

        return text;
    }

    private static void Add(IDictionary<string, List<string>> fieldErrors, string field, string message)
    {
        if (!fieldErrors.TryGetValue(field, out var messages))
        {
            messages = new List<string>(1);
            fieldErrors[field] = messages;
        }

        messages.Add(message);
    }

    private static string Quoted(IReadOnlyList<string> allowed) =>
        string.Join(" | ", allowed.Select(v => $"'{v}'"));

    private static string JsTypeName(BsonValue value) => value.BsonType switch
    {
        BsonType.Array => "array",
        BsonType.Document => "object",
        BsonType.String => "string",
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => "number",
        BsonType.Boolean => "boolean",
        BsonType.Null => "null",
        _ => "undefined",
    };

    /// <summary>
    /// BSON → plain CLR so the value can go straight into a <c>tool_call</c> frame
    /// without dragging the driver's extended-JSON representation onto the wire.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ToPlainMap(BsonDocument document)
    {
        var map = new Dictionary<string, object?>(document.ElementCount, StringComparer.Ordinal);
        foreach (var element in document)
        {
            map[element.Name] = ToPlainValue(element.Value);
        }

        return map;
    }

    public static object? ToPlainValue(BsonValue? value) => value?.BsonType switch
    {
        null or BsonType.Null or BsonType.Undefined => null,
        BsonType.Document => ToPlainMap(value.AsBsonDocument),
        BsonType.Array => value.AsBsonArray.Select(ToPlainValue).ToList(),
        BsonType.String => value.AsString,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => value.AsDecimal,
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        _ => value.ToString(),
    };
}
