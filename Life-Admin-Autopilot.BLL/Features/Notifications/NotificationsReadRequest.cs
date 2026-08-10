using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Notifications;

/// <summary>
/// <c>z.object({ ids: z.array(z.string()).max(100).optional() }).parse(req.body ?? {})</c>.
///
/// <para>
/// <b>This is the only route in the API on the THROWING <c>.parse()</c> lane
/// (KERNEL.md §2.3).</b> Every other route uses <c>safeParse()</c> and renders its
/// own <c>invalid_body</c> envelope with a <c>{formErrors, fieldErrors}</c>
/// object. Here the raw <c>ZodError</c> escapes to <c>errorHandler.ts</c>, which
/// answers <c>400 validation_error</c> / "Request validation failed" with
/// <c>details</c> as an ARRAY of <c>{path, message}</c>. Throwing
/// <see cref="ValidationException"/> is what selects that shape — an
/// <c>AppException.BadRequest</c> with <c>AsFlattened</c> would be the wrong one.
/// </para>
///
/// <para>
/// The schema is NOT <c>.strict()</c>: unknown keys are accepted and ignored
/// (verified live — <c>{"zz":1}</c> answers 200).
/// </para>
/// </summary>
public static class NotificationsReadRequest
{
    /// <summary>zod's <c>.max(100)</c> on an array. Not in <c>ZodMessages</c> — see the note in this file's slice report.</summary>
    public const int MaxIds = 100;

    /// <summary>
    /// <c>z.array(...).max(n)</c>. Captured live from <c>:4200</c> with 101 entries.
    /// </summary>
    public static string ArrayTooBig(int max) => $"Array must contain at most {max} element(s)";

    /// <summary>
    /// Parse the body root.
    /// </summary>
    /// <returns>
    /// The <c>ids</c> the caller asked for, already narrowed to well-formed
    /// ObjectIds — or <c>null</c> when <c>ids</c> was absent OR an empty array,
    /// both of which mean "mark everything unread as read".
    /// </returns>
    /// <exception cref="ValidationException">
    /// Rendered as the <c>validation_error</c> envelope with an array <c>details</c>.
    /// </exception>
    public static IReadOnlyList<ObjectId>? Parse(JsonElement root)
    {
        var issues = new List<ValidationIssue>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            // An express-parsed body can only be an object or an array here — a
            // top-level primitive is rejected by body-parser's strict mode long
            // before the route runs, and surfaces as a 500. So this is the array
            // case, reported by zod as a whole-object type issue at the EMPTY path.
            issues.Add(ValidationIssue.At(string.Empty, ZodMessages.ExpectedType("object", JsTypeName(root.ValueKind))));
            throw new ValidationException(issues);
        }

        if (!root.TryGetProperty("ids", out var ids))
        {
            // Absent — mark all unread read.
            return null;
        }

        if (ids.ValueKind != JsonValueKind.Array)
        {
            // An explicit null is a TYPE error, not an absent key: JSON has no
            // `undefined`, so `.optional()` never sees this as missing. Verified
            // live — {"ids":null} answers "Expected array, received null".
            issues.Add(ValidationIssue.At("ids", ZodMessages.ExpectedType("array", JsTypeName(ids.ValueKind))));
            throw new ValidationException(issues);
        }

        var length = ids.GetArrayLength();

        // Order is observable, and it is not the obvious one: zod checks the array's
        // own size constraints BEFORE walking its elements, so the `.max(100)` issue
        // is emitted first and the per-element issues follow. Verified live with a
        // 101-entry array whose first element was a number.
        if (length > MaxIds)
        {
            issues.Add(ValidationIssue.At("ids", ArrayTooBig(MaxIds)));
        }

        var parsed = new List<ObjectId>();
        var index = 0;
        foreach (var element in ids.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                issues.Add(ValidationIssue.At(
                    $"ids.{index}",
                    ZodMessages.ExpectedType("string", JsTypeName(element.ValueKind))));
            }
            else if (ObjectId.TryParse(element.GetString(), out var id))
            {
                // `Types.ObjectId.isValid` — anything that is not a well-formed id is
                // dropped SILENTLY rather than rejected. That can empty the $in
                // entirely, which makes the update a deliberate no-op rather than a
                // mark-everything. Verified live: a 12-character non-hex string does
                // NOT match a notification whose _id holds those bytes, so plain
                // 24-hex parsing is the faithful equivalent; uppercase hex IS
                // accepted, and TryParse accepts it too.
                parsed.Add(id);
            }

            index++;
        }

        if (issues.Count > 0)
        {
            throw new ValidationException(issues);
        }

        // An EMPTY array is Node's falsy `ids.length > 0` — it does NOT narrow the
        // filter, so it marks everything read, exactly as an omitted key does.
        return length == 0 ? null : parsed;
    }

    /// <summary>The name zod's error map uses for a received value's type.</summary>
    private static string JsTypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        _ => "unknown",
    };
}
