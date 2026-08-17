using System.Globalization;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Features.Tasks.Binding;

/// <summary>
/// The body-side twin of the kernel's <c>QueryReader</c>: typed field extraction
/// that accumulates zod-shaped issues and throws them all at once.
///
/// <para>
/// Every <c>me.tasks</c> body field is declared as a <see cref="JsonElement"/> on
/// its request class, because that is the only way to tell the THREE cases apart
/// that this slice's PATCH semantics depend on:
/// <c>Undefined</c> = key absent (leave alone), <c>Null</c> = explicit null
/// (clear the field), anything else = a value to set. A nullable CLR property
/// collapses the first two and silently turns "leave alone" into "clear".
/// </para>
///
/// <para><b>Accumulate, then throw once</b> — zod reports every issue in one
/// response, so never throw per field.</para>
/// </summary>
public sealed class BodyFields
{
    private readonly List<ValidationIssue> _issues = new();

    public bool HasIssues => _issues.Count > 0;

    public void AddIssue(string field, string message) => _issues.Add(ValidationIssue.At(field, message));

    public void AddFormIssue(string message) => _issues.Add(ValidationIssue.Form(message));

    /// <summary>
    /// Throws <c>400 {code}</c> with <c>{formErrors, fieldErrors}</c> when anything
    /// failed. Call exactly once, after reading every field.
    /// </summary>
    public void ThrowIfInvalid(string code, string message)
    {
        if (_issues.Count == 0)
        {
            return;
        }

        throw AppException.BadRequest(code, message, ValidationDetails.AsFlattened(_issues));
    }

    public static bool IsAbsent(JsonElement element) => element.ValueKind == JsonValueKind.Undefined;

    public static bool IsNull(JsonElement element) => element.ValueKind == JsonValueKind.Null;

    /// <summary>Present and not null — i.e. carries a value to validate.</summary>
    public static bool HasValue(JsonElement element) => !IsAbsent(element) && !IsNull(element);

    // ---- Strings ----------------------------------------------------------

    /// <summary>
    /// <c>z.string().trim().min(min).max(max)</c>. The <c>.trim()</c> sits FIRST in
    /// the Matters chains, so the length checks see the TRIMMED value — which is
    /// why a whitespace-only title is rejected rather than stored as <c>""</c>.
    /// (Contrast <c>NodeFieldRules.TryNormalizeDisplayName</c>, where the chain is
    /// the other way round.)
    /// </summary>
    public string? TrimmedString(JsonElement element, string field, int min, int max, bool required)
    {
        if (IsAbsent(element))
        {
            if (required)
            {
                AddIssue(field, ZodMessages.Required);
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            AddIssue(field, ZodMessages.ExpectedType("string", KindName(element)));
            return null;
        }

        var value = (element.GetString() ?? string.Empty).Trim();

        if (value.Length < min)
        {
            AddIssue(field, ZodMessages.TooShort(min));
            return null;
        }

        if (value.Length > max)
        {
            AddIssue(field, ZodMessages.TooLong(max));
            return null;
        }

        return value;
    }

    /// <summary><c>z.string().max(max)</c> — no trim, no minimum.</summary>
    public string? PlainString(JsonElement element, string field, int max)
    {
        if (!HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            AddIssue(field, ZodMessages.ExpectedType("string", KindName(element)));
            return null;
        }

        var value = element.GetString() ?? string.Empty;
        if (value.Length > max)
        {
            AddIssue(field, ZodMessages.TooLong(max));
            return null;
        }

        return value;
    }

    // ---- Enums ------------------------------------------------------------

    public string? Enum(JsonElement element, string field, IReadOnlyList<string> allowed, bool required)
    {
        if (IsAbsent(element))
        {
            if (required)
            {
                AddIssue(field, ZodMessages.Required);
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            AddIssue(field, ZodMessages.InvalidEnum(allowed, RawText(element)));
            return null;
        }

        var value = element.GetString();
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
        {
            AddIssue(field, ZodMessages.InvalidEnum(allowed, value));
            return null;
        }

        return value;
    }

    // ---- Dates ------------------------------------------------------------

    /// <summary><c>z.string().datetime()</c>, always surfaced as UTC.</summary>
    public DateTime? IsoDate(JsonElement element, string field)
    {
        if (!HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            AddIssue(field, ZodMessages.ExpectedType("string", KindName(element)));
            return null;
        }

        var raw = element.GetString();
        if (!TryParseIsoInstant(raw, out var parsed))
        {
            AddIssue(field, ZodMessages.InvalidDatetime);
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// zod's <c>.datetime()</c> accepts an ISO-8601 instant only — a bare
    /// <c>2026-09-01</c> is rejected, which is exactly what the list query's
    /// <c>dueBefore</c> test pins.
    /// </summary>
    public static bool TryParseIsoInstant(string? raw, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('T') < 0)
        {
            return false;
        }

        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var value))
        {
            return false;
        }

        parsed = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return true;
    }

    // ---- Numbers ----------------------------------------------------------

    public int? Int(JsonElement element, string field, int min, int max, bool required)
    {
        if (IsAbsent(element))
        {
            if (required)
            {
                AddIssue(field, ZodMessages.Required);
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            AddIssue(field, ZodMessages.ExpectedType("number", KindName(element)));
            return null;
        }

        if (value < min)
        {
            AddIssue(field, ZodMessages.NumberTooSmall(min));
            return null;
        }

        if (value > max)
        {
            AddIssue(field, ZodMessages.NumberTooBig(max));
            return null;
        }

        return value;
    }

    /// <summary>
    /// <see cref="Int"/> widened to 64 bits, for money.
    ///
    /// <para>
    /// An amount is a count of minor units, so int32 would cap a matter at about
    /// 21 million major units — fine for a household bill and wrong for a currency
    /// with a small unit (a sum in IDR or VND reaches that range on an ordinary
    /// purchase). The ceiling belongs to the money gate, not to the width of the
    /// integer that carried it here.
    /// </para>
    /// </summary>
    public long? Long(JsonElement element, string field, long min, long max, bool required)
    {
        if (IsAbsent(element))
        {
            if (required)
            {
                AddIssue(field, ZodMessages.Required);
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            AddIssue(field, ZodMessages.ExpectedType("number", KindName(element)));
            return null;
        }

        if (value < min)
        {
            AddIssue(field, ZodMessages.NumberTooSmall(min));
            return null;
        }

        if (value > max)
        {
            AddIssue(field, ZodMessages.NumberTooBig(max));
            return null;
        }

        return value;
    }

    /// <summary>
    /// A figure in MAJOR units — "500", "49.99" — as an extractor reports one.
    ///
    /// <para>
    /// <c>decimal</c>, never <c>double</c>: this is the only reader that carries a
    /// fractional amount of money, and binary floating point cannot hold 142.37.
    /// It is read straight out of the JSON number rather than through a double,
    /// so the value that reaches <c>MoneyVocabulary.Normalize</c> is the one that
    /// was written. Everything downstream counts minor units; this is where the
    /// last decimal point in the system is turned into one.
    /// </para>
    /// </summary>
    public decimal? Decimal(JsonElement element, string field, decimal min, decimal max, bool required)
    {
        if (IsAbsent(element))
        {
            if (required)
            {
                AddIssue(field, ZodMessages.Required);
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
        {
            AddIssue(field, ZodMessages.ExpectedType("number", KindName(element)));
            return null;
        }

        if (value < min)
        {
            AddIssue(field, ZodMessages.NumberTooSmall((long)min));
            return null;
        }

        if (value > max)
        {
            AddIssue(field, ZodMessages.NumberTooBig((long)max));
            return null;
        }

        return value;
    }

    public bool? Bool(JsonElement element, string field)
    {
        if (!HasValue(element))
        {
            return null;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddIssue(field, ZodMessages.ExpectedType("boolean", KindName(element)));
            return null;
        }

        return element.GetBoolean();
    }

    // ---- Arrays -----------------------------------------------------------

    /// <summary>
    /// <c>z.array(z.string().max(itemMax)).max(arrayMax)</c>. Returns the raw
    /// strings; per-field normalisation (tag folding, id parsing) is the caller's.
    /// </summary>
    public List<string>? StringArray(
        JsonElement element,
        string field,
        int itemMax,
        int arrayMax,
        int arrayMin = 0)
    {
        if (!HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            AddIssue(field, ZodMessages.ExpectedType("array", KindName(element)));
            return null;
        }

        var items = new List<string>();
        var failed = false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                AddIssue(field, ZodMessages.ExpectedType("string", KindName(item)));
                failed = true;
                continue;
            }

            var value = item.GetString() ?? string.Empty;
            if (value.Length > itemMax)
            {
                AddIssue(field, ZodMessages.TooLong(itemMax));
                failed = true;
                continue;
            }

            items.Add(value);
        }

        if (items.Count > arrayMax)
        {
            AddIssue(field, ZodMessages.ArrayTooBig(arrayMax));
            return null;
        }

        if (arrayMin > 0 && items.Count < arrayMin)
        {
            AddIssue(field, ZodMessages.ArrayTooSmall(arrayMin));
            return null;
        }

        return failed ? null : items;
    }

    private static string KindName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    private static string RawText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
}
