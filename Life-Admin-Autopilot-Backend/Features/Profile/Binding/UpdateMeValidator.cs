using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Validation;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Profile.Binding;

/// <summary>
/// Ports <c>UpdateMeSchema</c> + <c>buildSet</c> from <c>routes/me.ts</c>: validate
/// the patch, then flatten it into the dot-notation <c>$set</c> the update runs
/// with.
///
/// <para>
/// <b>Fields are read in SCHEMA declaration order, and that is observable.</b> zod
/// walks its shape, not the request body, so a body of
/// <c>{privacy, mic, notifications}</c> reports its errors as <c>mic</c>,
/// <c>notifications</c>, <c>privacy</c>. Measured against the reference.
/// </para>
/// </summary>
public static class UpdateMeValidator
{
    public const string Code = "invalid_body";
    public const string Message = "Some of those settings looked off.";

    /// <summary>
    /// The four sub-objects that are MERGED key-by-key instead of replaced.
    ///
    /// <para>
    /// This is the single most load-bearing behaviour in the route. Sending
    /// <c>{notifications: {push: false}}</c> emits <c>$set: {"notifications.push":
    /// false}</c>, so <c>emailDigest</c> and <c>marketing</c> survive untouched. A
    /// port that sets <c>notifications</c> wholesale silently resets whichever
    /// sub-keys the client did not send — and the frontend sends one at a time.
    /// Everything else in the schema, INCLUDING <c>preferredDomains</c> and
    /// <c>onboardingAnswers</c>, is replaced wholesale.
    /// </para>
    /// </summary>
    private static readonly string[] NestedKeys = { "mic", "notifications", "privacy", "imports" };

    /// <summary>
    /// Validated patch as ordered dot-notation <c>$set</c> pairs. Empty is valid and
    /// means "touch only" — the reference answers 200 and bumps <c>updatedAt</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, BsonValue>> BuildSet(UpdateMeBody body)
    {
        var issues = new List<ValidationIssue>();
        var set = new List<KeyValuePair<string, BsonValue>>();

        ReadDisplayName(body.DisplayName, set, issues);
        ReadPreferredDomains(body.PreferredDomains, set, issues);
        ReadBool(body.HasOnboarded, "hasOnboarded", set, issues);
        ReadOnboardingAnswers(body.OnboardingAnswers, set, issues);
        ReadRefinedString(body.Timezone, "timezone", 1, 64, NodeIntl.IsValidTimezone, NotATimezone, set, issues);
        ReadBool(body.TimezoneFollowsDevice, "timezoneFollowsDevice", set, issues);
        ReadRefinedString(body.Locale, "locale", 2, 35, NodeIntl.IsValidLocale, NotALocale, set, issues);
        ReadBool(body.LocaleFollowsDevice, "localeFollowsDevice", set, issues);
        ReadEnum(body.Theme, "theme", UserVocabulary.Themes, set, issues);
        ReadEnum(body.TextSize, "textSize", UserVocabulary.TextSizes, set, issues);
        ReadMic(body.Mic, set, issues);
        ReadNotifications(body.Notifications, set, issues);
        ReadPrivacy(body.Privacy, set, issues);
        ReadImports(body.Imports, set, issues);

        if (issues.Count > 0)
        {
            throw AppException.BadRequest(Code, Message, ValidationDetails.AsFlattened(issues));
        }

        return set;
    }

    /// <summary>The two custom refinement messages, verbatim from the Node schema.</summary>
    private const string NotATimezone = "Not a recognised time zone.";

    private const string NotALocale = "Not a recognised locale.";

    // ---- Scalars ------------------------------------------------------------

    /// <summary>
    /// <c>z.string().min(1).max(80).trim()</c> — the LENGTH CHECKS RUN BEFORE THE
    /// TRIM, so <c>"   "</c> is three characters, passes <c>min(1)</c>, and is then
    /// stored as the EMPTY STRING. Measured. Do not "fix" it into a rejection;
    /// <c>NodeFieldRules.TryNormalizeDisplayName</c> exists to keep the order right.
    /// </summary>
    private static void ReadDisplayName(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (IsAbsent(element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(TypeIssue("displayName", "string", element));
            return;
        }

        var raw = element.GetString() ?? string.Empty;
        if (!NodeFieldRules.TryNormalizeDisplayName(raw, out var normalized))
        {
            // The helper folds both length failures into false; only one of the two
            // can ever fire, so the untrimmed length picks the message.
            issues.Add(ValidationIssue.At(
                "displayName",
                raw.Length < 1 ? ZodMessages.TooShort(1) : ZodMessages.TooLong(80)));
            return;
        }

        set.Add(new("displayName", normalized));
    }

    private static void ReadBool(
        JsonElement element,
        string field,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (IsAbsent(element))
        {
            return;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            issues.Add(TypeIssue(field, "boolean", element));
            return;
        }

        set.Add(new(field, element.GetBoolean()));
    }

    private static void ReadEnum(
        JsonElement element,
        string field,
        IReadOnlyList<string> allowed,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (IsAbsent(element))
        {
            return;
        }

        var value = ReadEnumValue(element, field, allowed, issues);
        if (value is not null)
        {
            set.Add(new(field, value));
        }
    }

    /// <summary>
    /// <c>z.string().min(a).max(b).refine(probe, message)</c>.
    ///
    /// <para>
    /// <b>The refinement runs EVEN WHEN THE LENGTH CHECK FAILED</b>, so
    /// <c>{"timezone":""}</c> answers with BOTH
    /// <c>"String must contain at least 1 character(s)"</c> AND
    /// <c>"Not a recognised time zone."</c>, in that order. That is not an accident
    /// of the schema: a zod string length failure marks the result <i>dirty</i>
    /// rather than <i>aborted</i>, and only an abort short-circuits a
    /// <c>ZodEffects</c> refinement. A WRONG TYPE does abort, which is why
    /// <c>{"timezone":123}</c> reports the type issue alone. Both measured.
    /// </para>
    /// </summary>
    private static void ReadRefinedString(
        JsonElement element,
        string field,
        int min,
        int max,
        Func<string, bool> probe,
        string refinementMessage,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (IsAbsent(element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(TypeIssue(field, "string", element));
            return;
        }

        var raw = element.GetString() ?? string.Empty;
        var lengthFailed = false;

        if (raw.Length < min)
        {
            issues.Add(ValidationIssue.At(field, ZodMessages.TooShort(min)));
            lengthFailed = true;
        }
        else if (raw.Length > max)
        {
            issues.Add(ValidationIssue.At(field, ZodMessages.TooLong(max)));
            lengthFailed = true;
        }

        if (!probe(raw))
        {
            issues.Add(ValidationIssue.At(field, refinementMessage));
            return;
        }

        if (!lengthFailed)
        {
            // Stored raw. The schema refines, it does not transform, so "EN-gb"
            // and "utc" persist exactly as sent. Measured.
            set.Add(new(field, raw));
        }
    }

    // ---- Arrays -------------------------------------------------------------

    /// <summary>
    /// <c>z.array(z.enum(DOMAINS))</c>. Replaced WHOLESALE — an empty array is a
    /// valid patch that clears every preferred domain.
    /// </summary>
    private static void ReadPreferredDomains(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (IsAbsent(element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            issues.Add(TypeIssue("preferredDomains", "array", element));
            return;
        }

        var domains = new BsonArray();
        var failed = false;

        foreach (var item in element.EnumerateArray())
        {
            var value = ReadEnumValue(item, "preferredDomains", TaskVocabulary.Domains, issues);
            if (value is null)
            {
                failed = true;
                continue;
            }

            domains.Add(value);
        }

        if (!failed)
        {
            set.Add(new("preferredDomains", domains));
        }
    }

    /// <summary>
    /// <c>z.array(z.object({id,question,answer})).max(20)</c>. Onboarding Q&amp;A
    /// captured as AI personalization memory, replaced as a whole array rather than
    /// merged.
    /// </summary>
    private static void ReadOnboardingAnswers(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        const string Field = "onboardingAnswers";

        if (IsAbsent(element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            issues.Add(TypeIssue(Field, "array", element));
            return;
        }

        var answers = new BsonArray();
        var failed = false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add(TypeIssue(Field, "object", item));
                failed = true;
                continue;
            }

            var id = ReadAnswerMember(item, "id", 100, Field, issues, ref failed);
            var question = ReadAnswerMember(item, "question", 200, Field, issues, ref failed);
            var answer = ReadAnswerMember(item, "answer", 500, Field, issues, ref failed);

            if (id is null || question is null || answer is null)
            {
                continue;
            }

            answers.Add(new BsonDocument
            {
                { "id", id },
                { "question", question },
                { "answer", answer },
            });
        }

        // The array-level `.max(20)` runs after the element checks.
        if (answers.Count > UserVocabulary.MaxOnboardingAnswers)
        {
            issues.Add(ValidationIssue.At(
                Field,
                ZodMessages.ArrayTooBig(UserVocabulary.MaxOnboardingAnswers)));
            return;
        }

        if (!failed)
        {
            set.Add(new(Field, answers));
        }
    }

    /// <summary>
    /// One required <c>z.string().max(n)</c> inside an onboarding answer. Every
    /// issue is keyed under the TOP-LEVEL <c>onboardingAnswers</c>, because zod's
    /// <c>flatten()</c> groups by <c>path[0]</c>.
    /// </summary>
    private static string? ReadAnswerMember(
        JsonElement item,
        string name,
        int max,
        string field,
        List<ValidationIssue> issues,
        ref bool failed)
    {
        if (!item.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Undefined)
        {
            issues.Add(ValidationIssue.At(field, ZodMessages.Required));
            failed = true;
            return null;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            issues.Add(TypeIssue(field, "string", member));
            failed = true;
            return null;
        }

        var value = member.GetString() ?? string.Empty;
        if (value.Length > max)
        {
            issues.Add(ValidationIssue.At(field, ZodMessages.TooLong(max)));
            failed = true;
            return null;
        }

        return value;
    }

    // ---- The four merged sub-objects ---------------------------------------

    private static void ReadMic(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (!TryOpenNested(element, "mic", issues, out var mic))
        {
            return;
        }

        if (Member(mic, "quality") is { } quality)
        {
            var value = ReadEnumValue(quality, "mic", UserVocabulary.MicQualities, issues);
            if (value is not null)
            {
                set.Add(new("mic.quality", value));
            }
        }
    }

    private static void ReadNotifications(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues) =>
        ReadBoolSubObject(element, "notifications", new[] { "push", "emailDigest", "marketing" }, set, issues);

    private static void ReadPrivacy(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues) =>
        ReadBoolSubObject(element, "privacy", new[] { "analytics", "crashReports" }, set, issues);

    private static void ReadImports(
        JsonElement element,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (!TryOpenNested(element, "imports", issues, out var imports))
        {
            return;
        }

        if (Member(imports, "defaultTimeOfDay") is not { } time)
        {
            return;
        }

        if (time.ValueKind != JsonValueKind.String)
        {
            issues.Add(TypeIssue("imports", "string", time));
            return;
        }

        var value = time.GetString() ?? string.Empty;

        // Validated strictly on purpose: a malformed value falling back to midnight
        // would nudge people at 00:00, which they do not report — they just mute
        // notifications.
        if (!NodeFieldRules.IsValidTimeOfDay(value))
        {
            issues.Add(ValidationIssue.At("imports", "Use a 24-hour time like 09:00."));
            return;
        }

        set.Add(new("imports.defaultTimeOfDay", value));
    }

    /// <summary>
    /// Sub-keys are visited in SCHEMA order, not body order — zod walks its own
    /// shape. A body of <c>{marketing, push, emailDigest}</c> all wrong reports
    /// three issues ordered push, emailDigest, marketing.
    /// </summary>
    private static void ReadBoolSubObject(
        JsonElement element,
        string field,
        IReadOnlyList<string> subKeys,
        List<KeyValuePair<string, BsonValue>> set,
        List<ValidationIssue> issues)
    {
        if (!TryOpenNested(element, field, issues, out var nested))
        {
            return;
        }

        foreach (var subKey in subKeys)
        {
            if (Member(nested, subKey) is not { } member)
            {
                continue;
            }

            if (member.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                issues.Add(TypeIssue(field, "boolean", member));
                continue;
            }

            set.Add(new($"{field}.{subKey}", member.GetBoolean()));
        }
    }

    /// <summary>
    /// A <c>.partial().optional()</c> sub-object: absent is fine, <c>{}</c> is fine
    /// and sets nothing, anything that is not an object is
    /// <c>"Expected object, received &lt;type&gt;"</c> keyed under the top-level
    /// name. Unknown sub-keys are STRIPPED — the sub-schemas are not
    /// <c>.strict()</c> either.
    /// </summary>
    private static bool TryOpenNested(
        JsonElement element,
        string field,
        List<ValidationIssue> issues,
        out JsonElement nested)
    {
        nested = element;

        if (IsAbsent(element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(TypeIssue(field, "object", element));
            return false;
        }

        return true;
    }

    private static JsonElement? Member(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value : null;

    // ---- Shared issue construction -----------------------------------------

    /// <summary>
    /// zod reports an enum miss two different ways, and the split is by TYPE, not by
    /// value: a non-string is the <c>invalid_type</c> form
    /// (<c>"Expected 'standard' | 'high', received null"</c>) while a string that is
    /// not a member is the <c>invalid_enum_value</c> form
    /// (<c>"Invalid enum value. Expected 'standard' | 'high', received 'ultra'"</c>).
    /// Both measured.
    /// </summary>
    private static string? ReadEnumValue(
        JsonElement element,
        string field,
        IReadOnlyList<string> allowed,
        List<ValidationIssue> issues)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            issues.Add(ValidationIssue.At(
                field,
                $"Expected {string.Join(" | ", allowed.Select(v => $"'{v}'"))}, received {ZodTypeName(element)}"));
            return null;
        }

        var value = element.GetString();
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
        {
            issues.Add(ValidationIssue.At(field, ZodMessages.InvalidEnum(allowed, value)));
            return null;
        }

        return value;
    }

    private static ValidationIssue TypeIssue(string field, string expected, JsonElement got) =>
        ValidationIssue.At(field, ZodMessages.ExpectedType(expected, ZodTypeName(got)));

    private static bool IsAbsent(JsonElement element) => element.ValueKind == JsonValueKind.Undefined;

    private static string ZodTypeName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };
}
