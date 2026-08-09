using System.Globalization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Kernel.Binding;

/// <summary>
/// Strict query-string binding, the way every Node query schema does it.
///
/// <para>
/// <b>Unknown parameters are a 400, never silently ignored.</b> A typo'd filter
/// that quietly returns everything is worse than an error, and the frontend
/// depends on the rejection. <b>Empty-string parameters are rejected too</b> —
/// <c>?status=</c> and <c>?q=</c> both fail on the live Node server, because the
/// value reaches the field's own <c>min(1)</c>.
/// </para>
///
/// <para><b>Usage — accumulate, then throw once.</b> zod reports every issue in one
/// response, so never throw per field:</para>
/// <code>
/// var q = new QueryReader(context.Request.Query, "sort", "limit", "cursor", "status", "q");
/// var status = q.CsvEnum("status", TaskVocabulary.Statuses);
/// var text   = q.String("q", minLength: 1, maxLength: 200);
/// var limit  = q.Int("limit", min: 1, max: 200, fallback: 50);
/// q.ThrowIfInvalid("invalid_query", "Invalid task list query.");
/// </code>
/// </summary>
public sealed class QueryReader
{
    private readonly IQueryCollection _query;
    private readonly HashSet<string> _allowed;
    private readonly List<ValidationIssue> _issues = new();

    public QueryReader(IQueryCollection query, params string[] allowedKeys)
    {
        _query = query;
        _allowed = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
        CollectUnknownKeys();
    }

    public bool HasIssues => _issues.Count > 0;

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>
    /// Throws <c>400 {code}</c> with <c>{formErrors, fieldErrors}</c> when anything
    /// failed. Call it exactly once, after reading every parameter.
    /// </summary>
    public void ThrowIfInvalid(string code, string message)
    {
        if (_issues.Count == 0)
        {
            return;
        }

        throw AppException.BadRequest(code, message, ValidationDetails.AsFlattened(_issues));
    }

    public void AddIssue(string field, string message) => _issues.Add(ValidationIssue.At(field, message));

    public void AddFormIssue(string message) => _issues.Add(ValidationIssue.Form(message));

    /// <summary>
    /// Raw value, or null when absent. An EMPTY value is recorded as an issue and
    /// returned as null so the caller does not also fail on it.
    /// </summary>
    public string? Raw(string key, string? emptyMessage = null)
    {
        if (!_query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        if (value.Length == 0)
        {
            AddIssue(key, emptyMessage ?? ZodMessages.TooShort(1));
            return null;
        }

        return value;
    }

    public string? String(string key, int? minLength = null, int? maxLength = null)
    {
        var raw = Raw(key, minLength is > 0 ? ZodMessages.TooShort(minLength.Value) : null);
        if (raw is null)
        {
            return null;
        }

        // Node's `q` is `z.string().trim().min(1)` — trim runs BEFORE the length
        // check, so a whitespace-only value fails.
        var value = raw.Trim();
        if (minLength.HasValue && value.Length < minLength.Value)
        {
            AddIssue(key, ZodMessages.TooShort(minLength.Value));
            return null;
        }

        if (maxLength.HasValue && value.Length > maxLength.Value)
        {
            AddIssue(key, ZodMessages.TooLong(maxLength.Value));
            return null;
        }

        return value;
    }

    /// <summary>
    /// Comma-separated multi-value enum: <c>?status=open,snoozed</c>. Rejects the
    /// whole request on an unknown member rather than silently ignoring it, and
    /// reports EVERY offending member — matching <c>csvEnum</c>'s superRefine loop.
    /// </summary>
    public IReadOnlyList<string>? CsvEnum(string key, IReadOnlyList<string> allowed)
    {
        var raw = Raw(key, ZodMessages.MustNotBeEmpty);
        if (raw is null)
        {
            return null;
        }

        var parts = raw
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var invalid = false;
        foreach (var part in parts.Where(p => !allowed.Contains(p, StringComparer.Ordinal)))
        {
            AddIssue(key, ZodMessages.CsvEnumMember(allowed, part));
            invalid = true;
        }

        if (parts.Count == 0)
        {
            AddIssue(key, ZodMessages.MustNotBeEmpty);
            return null;
        }

        return invalid ? null : parts;
    }

    public string? Enum(string key, IReadOnlyList<string> allowed, string? fallback = null)
    {
        var raw = Raw(key);
        if (raw is null)
        {
            return fallback;
        }

        if (!allowed.Contains(raw, StringComparer.Ordinal))
        {
            AddIssue(key, ZodMessages.InvalidEnum(allowed, raw));
            return fallback;
        }

        return raw;
    }

    public int Int(string key, int? min = null, int? max = null, int fallback = 0)
    {
        var raw = Raw(key);
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            AddIssue(key, ZodMessages.ExpectedType("number", "nan"));
            return fallback;
        }

        if (min.HasValue && value < min.Value)
        {
            AddIssue(key, ZodMessages.NumberTooSmall(min.Value));
            return fallback;
        }

        if (max.HasValue && value > max.Value)
        {
            AddIssue(key, ZodMessages.NumberTooBig(max.Value));
            return fallback;
        }

        return value;
    }

    /// <summary>
    /// Query strings arrive as <c>"true"</c>/<c>"false"</c>. Parsed EXPLICITLY —
    /// a coercing boolean would turn the string <c>"false"</c> into <c>true</c>,
    /// which is the bug the Node comment warns about.
    /// </summary>
    public bool? Bool(string key)
    {
        var raw = Raw(key);
        if (raw is null)
        {
            return null;
        }

        return raw switch
        {
            "true" => true,
            "false" => false,
            _ => Invalid(),
        };

        bool? Invalid()
        {
            AddIssue(key, ZodMessages.InvalidEnum(new[] { "true", "false" }, raw));
            return null;
        }
    }

    /// <summary>ISO-8601 instant, matching zod's <c>.datetime()</c>. Always returned as UTC.</summary>
    public DateTime? IsoDate(string key)
    {
        var raw = Raw(key);
        if (raw is null)
        {
            return null;
        }

        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            AddIssue(key, ZodMessages.InvalidDatetime);
            return null;
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private void CollectUnknownKeys()
    {
        var unknown = _query.Keys.Where(k => !_allowed.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            AddFormIssue(ZodMessages.UnrecognizedKeys(unknown));
        }
    }
}
