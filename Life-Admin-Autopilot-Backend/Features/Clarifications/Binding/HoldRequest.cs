using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Tasks;
using Life_Admin_Autopilot_Backend.Features.Tasks.Binding;

namespace Life_Admin_Autopilot_Backend.Features.Clarifications.Binding;

/// <summary>
/// The raw body of <c>POST /me/clarifications</c>.
///
/// <para>
/// Every member is a <see cref="JsonElement"/> for the same reason the Matters
/// bodies are: absent and present-but-wrong-typed have to stay distinguishable, and
/// a nullable CLR property collapses them.
/// </para>
///
/// <para>
/// The schema this mirrors — <c>holdForClarificationArgs</c> — is a plain
/// <c>z.object</c>, so it is NOT strict: unknown keys are stripped in silence.
/// </para>
/// </summary>
public sealed class HoldBody
{
    [JsonPropertyName("title")]
    public JsonElement Title { get; init; }

    [JsonPropertyName("domain")]
    public JsonElement Domain { get; init; }

    [JsonPropertyName("question")]
    public JsonElement Question { get; init; }

    [JsonPropertyName("kind")]
    public JsonElement Kind { get; init; }

    [JsonPropertyName("costOfWrong")]
    public JsonElement CostOfWrong { get; init; }

    [JsonPropertyName("priority")]
    public JsonElement Priority { get; init; }

    [JsonPropertyName("tags")]
    public JsonElement Tags { get; init; }

    [JsonPropertyName("notes")]
    public JsonElement Notes { get; init; }

    [JsonPropertyName("dueAtGuess")]
    public JsonElement DueAtGuess { get; init; }

    [JsonPropertyName("options")]
    public JsonElement Options { get; init; }

    /// <summary>
    /// ADDITIVE, and the whole point of the multi-question hold: one matter whose
    /// gaps are independently answerable.
    ///
    /// <para>
    /// Absent or empty is exactly the legacy payload — one row built from
    /// <see cref="Question"/>/<see cref="Kind"/>/<see cref="Options"/>. Present, it
    /// is the complete list of questions to raise against the ONE task this call
    /// creates, and each entry falls back to the top-level value for anything it
    /// omits. See <see cref="HoldBinder.MaxQuestions"/> for why three.
    /// </para>
    /// </summary>
    [JsonPropertyName("questions")]
    public JsonElement Questions { get; init; }

    /// <summary>
    /// A figure the user STATED, in major units — 500 for "send 500 EGP".
    ///
    /// <para>
    /// Held matters carry amounts for the same reason filed ones do. What made a
    /// matter uncertain is almost never the money: "send 500 EGP to Louisa" is a
    /// hold because the TIME is missing, and refusing the figure on those grounds
    /// loses the one fact the sentence stated plainly.
    /// </para>
    /// </summary>
    [JsonPropertyName("amount")]
    public JsonElement Amount { get; init; }

    /// <summary>ISO 4217 code for <see cref="Amount"/>. Never a symbol.</summary>
    [JsonPropertyName("currency")]
    public JsonElement Currency { get; init; }

    [JsonPropertyName("sourceText")]
    public JsonElement SourceText { get; init; }

    [JsonPropertyName("timezone")]
    public JsonElement Timezone { get; init; }
}

/// <summary>
/// Port of <c>holdForClarificationArgs</c>
/// (<c>server/src/modules/ai/toolRunner.ts</c>), read the way every other
/// <c>safeParse</c> route reads its body: accumulate zod-shaped issues, then throw
/// them once as <c>{formErrors, fieldErrors}</c>.
///
/// <para>
/// Nested issues are filed under the TOP-LEVEL name — an option's bad label is
/// reported at <c>"options"</c>, not <c>"options.0.label"</c> — because zod's
/// <c>flatten()</c> buckets by <c>issue.path[0]</c>.
/// </para>
/// </summary>
public static class HoldBinder
{
    public const string Code = "invalid_body";

    public const string Message = "Invalid clarification payload.";

    /// <summary>
    /// The custom message on <c>isoString</c> —
    /// <c>z.string().regex(STRICT_DATETIME_RE, 'must be ISO 8601 datetime')</c>.
    ///
    /// <para>
    /// Not in <c>ZodMessages</c>: that class holds the messages zod GENERATES, which
    /// are shared across every schema in the API. This one is a literal supplied by a
    /// single schema, so its one definition belongs with the schema it came from.
    /// </para>
    /// </summary>
    public const string NotIsoDatetime = "must be ISO 8601 datetime";

    /// <summary>
    /// An IANA zone we cannot resolve. There is no reference message to copy — the
    /// field does not exist on any Node route — so it says what is wrong.
    /// </summary>
    public const string UnknownTimezone = "unknown IANA time zone";

    /// <summary><c>CLARIFICATION_KINDS</c>, in schema order.</summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "date", "detail", "choice" };

    /// <summary><c>CLARIFICATION_COSTS</c>, in schema order.</summary>
    public static readonly IReadOnlyList<string> Costs = new[] { "low", "high" };

    /// <summary>
    /// <c>options</c> is <c>.max(4)</c>: past four, the model is guessing rather than
    /// disambiguating, and a card cannot show more.
    /// </summary>
    public const int MaxOptions = 4;

    /// <summary>
    /// The chat surface caps a question at 2000 characters
    /// (<c>modules/ai/schemas.ts</c>), so a chat-born quote cannot exceed it. Storage
    /// clamps again, to <see cref="SourceQuote.MaxSourceText"/>.
    /// </summary>
    public const int MaxSourceText = 2000;

    /// <summary>
    /// <c>secondary_question</c> repeated verbatim as the primary. Two identical
    /// sentences are one gap asked twice: the user answers the first card and the second
    /// stays open forever, which is the same "a question nothing can close" failure the
    /// date-survives-truncation rule exists to prevent.
    /// </summary>
    public const string DuplicateQuestion =
        "Two questions on this matter are the same sentence. Ask each gap once, with its own wording.";

    /// <summary>
    /// <c>questions</c> is <c>.max(3)</c>. A matter with four independent gaps is not
    /// a matter the model understood, and the card stack has to stay answerable in
    /// one sitting — the open-queue cap
    /// (<see cref="ClarificationHoldService.MaxOpenClarifications"/>) counts every
    /// row, so a generous limit here spends the user's whole queue on one item.
    /// </summary>
    public const int MaxQuestions = 3;

    public static HoldInput Parse(HoldBody body)
    {
        var f = new BodyFields();

        var title = f.TrimmedString(body.Title, "title", min: 1, max: 240, required: true);
        var domain = f.Enum(body.Domain, "domain", TaskVocabulary.Domains, required: true);
        var question = f.TrimmedString(body.Question, "question", min: 1, max: 300, required: true);
        var kind = f.Enum(body.Kind, "kind", Kinds, required: true);
        var costOfWrong = f.Enum(body.CostOfWrong, "costOfWrong", Costs, required: false);
        var priority = f.Enum(body.Priority, "priority", TaskVocabulary.Priorities, required: false);
        var tags = TaskFieldReaders.Tags(f, body.Tags);
        var notes = f.PlainString(body.Notes, "notes", max: 2000);
        var dueAtGuess = Iso(f, body.DueAtGuess, "dueAtGuess");
        var options = Options(f, body.Options, "options");
        var questions = Questions(f, body.Questions);
        var sourceText = f.PlainString(body.SourceText, "sourceText", max: MaxSourceText);

        // Through the money gate here rather than in the service, so an
        // unresolvable currency drops the FIGURE and still files the matter. The
        // model is guessing; a dropped guess costs the user a manual amount, and
        // rejecting the whole hold over it would cost them the task and the
        // question with it.
        var amount = MoneyVocabulary.Normalize(
            f.Decimal(body.Amount, "amount", min: 0m, max: MoneyVocabulary.MaxMajorUnits, required: false),
            f.PlainString(body.Currency, "currency", max: 3),
            source: "ai");
        var timezone = Timezone(f, body.Timezone);

        NoGapAskedTwice(f, questions);

        f.ThrowIfInvalid(Code, Message);

        return new HoldInput(
            title!,
            domain!,
            question!,
            kind!,
            priority,
            tags,
            notes,
            dueAtGuess,
            costOfWrong,
            options,
            sourceText,
            timezone,
            questions,
            amount);
    }

    /// <summary>
    /// Every question this hold will actually raise must be answerable.
    ///
    /// <para>
    /// Checked against the EFFECTIVE questions — the same fallback
    /// <c>ClarificationHoldService.BuildQuestions</c> applies — because that is what gets
    /// written. A <c>questions</c> entry that omits <c>kind</c> inherits the top-level
    /// one, so validating the raw payload would miss a date question that only becomes a
    /// date question after the fallback resolves.
    /// </para>
    ///
    /// <para>
    /// Deliberately here rather than in the service: this is the shape of a request the
    /// MODEL sends, and the value of rejecting it is that the model gets a 400 it can
    /// repair inside the same turn. The voice lane writes clarifications through the DAL
    /// and never passes this way, which is correct — a transcriber has no turn to repair
    /// in.
    /// </para>
    /// </summary>
    private static void NoGapAskedTwice(BodyFields f, IReadOnlyList<HoldRawQuestion> questions)
    {
        // Compared on the resolved text, trimmed and case-insensitively: the duplicate
        // this catches is `secondary_question` echoing `question`, and a model that
        // re-capitalises one of them has still asked one thing twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var question in questions)
        {
            if (!seen.Add(question.Question.Trim()))
            {
                f.AddIssue("questions", DuplicateQuestion);
                break;
            }
        }
    }

    /// <summary>
    /// <c>questions</c> — 0 to <see cref="MaxQuestions"/> independently-answerable
    /// gaps in ONE matter.
    ///
    /// <para>
    /// <b>Why this exists.</b> A hold used to carry a single question, so a matter
    /// with two gaps had to fold both into one sentence — "What time should I remind
    /// you, and which friend are you visiting?" — and one tapped option answered
    /// exactly one of them while the other silently ceased to exist. N gaps need N
    /// answer slots.
    /// </para>
    ///
    /// <para>
    /// Every member but <c>question</c> is optional and falls back to the TOP-LEVEL
    /// value, so the array can carry only what differs. The fallback is keyed on the
    /// KEY being absent, not on the value being empty: a secondary <c>detail</c>
    /// question sends <c>"options": []</c> to say "no chips here", and that must not
    /// inherit the primary's time chips.
    /// </para>
    /// </summary>
    private static List<HoldRawQuestion> Questions(BodyFields f, JsonElement element)
    {
        var questions = new List<HoldRawQuestion>();

        if (!BodyFields.HasValue(element))
        {
            return questions;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            f.AddIssue("questions", ZodMessages.ExpectedType("array", KindName(element.ValueKind)));
            return questions;
        }

        if (element.GetArrayLength() > MaxQuestions)
        {
            f.AddIssue("questions", ZodMessages.ArrayTooBig(MaxQuestions));
            return questions;
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                f.AddIssue("questions", ZodMessages.ExpectedType("object", KindName(entry.ValueKind)));
                continue;
            }

            var text = f.TrimmedString(Member(entry, "question"), "questions", min: 1, max: 300, required: true);
            var kind = f.Enum(Member(entry, "kind"), "questions", Kinds, required: false);
            var cost = f.Enum(Member(entry, "costOfWrong"), "questions", Costs, required: false);

            var rawOptions = Member(entry, "options");
            var options = BodyFields.HasValue(rawOptions) ? Options(f, rawOptions, "questions") : null;

            if (text is not null)
            {
                questions.Add(new HoldRawQuestion(text, kind, cost, options));
            }
        }

        return questions;
    }

    /// <summary><c>clarificationOptionArgs</c> — <c>z.array(...).max(4)</c>.</summary>
    private static List<HoldRawOption> Options(BodyFields f, JsonElement element, string field)
    {
        var options = new List<HoldRawOption>();

        if (!BodyFields.HasValue(element))
        {
            return options;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            f.AddIssue(field, ZodMessages.ExpectedType("array", KindName(element.ValueKind)));
            return options;
        }

        if (element.GetArrayLength() > MaxOptions)
        {
            f.AddIssue(field, ZodMessages.ArrayTooBig(MaxOptions));
            return options;
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                f.AddIssue(field, ZodMessages.ExpectedType("object", KindName(entry.ValueKind)));
                continue;
            }

            var label = f.TrimmedString(Member(entry, "label"), field, min: 1, max: 80, required: true);
            var dueAt = Iso(f, Member(entry, "dueAt"), field);
            var optionTitle = f.TrimmedString(Member(entry, "title"), field, min: 1, max: 240, required: false);
            var optionNotes = f.PlainString(Member(entry, "notes"), field, max: 2000);

            if (label is not null)
            {
                options.Add(new HoldRawOption(label, dueAt, optionTitle, optionNotes));
            }
        }

        return options;
    }

    /// <summary>
    /// <c>isoString</c>. Kept as the raw STRING: turning it into an instant needs the
    /// timezone, and doing that here would normalise the guess and the options in two
    /// different places.
    /// </summary>
    private static string? Iso(BodyFields f, JsonElement element, string field)
    {
        if (!BodyFields.HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            f.AddIssue(field, ZodMessages.ExpectedType("string", KindName(element.ValueKind)));
            return null;
        }

        var value = element.GetString();
        if (!HoldTimeNormalizer.IsStrictIso(value))
        {
            f.AddIssue(field, NotIsoDatetime);
            return null;
        }

        return value;
    }

    /// <summary>
    /// <c>z.string().min(1).max(64).optional()</c>, plus a resolvability check.
    ///
    /// <para>
    /// The check is why this is not just another string field. An unknown zone makes
    /// Node's <c>Intl</c> throw a <c>RangeError</c> nothing catches — a 500. Here the
    /// value arrives from a language model, so a mistyped zone would turn a modelling
    /// error into a server error; a 400 naming the field is the honest answer and is
    /// not a silent UTC fallback, which is the thing <c>KERNEL.md</c> §8.1 forbids.
    /// </para>
    /// </summary>
    private static string? Timezone(BodyFields f, JsonElement element)
    {
        if (!BodyFields.HasValue(element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            f.AddIssue("timezone", ZodMessages.ExpectedType("string", KindName(element.ValueKind)));
            return null;
        }

        var value = element.GetString() ?? string.Empty;

        if (value.Length < 1)
        {
            f.AddIssue("timezone", ZodMessages.TooShort(1));
            return null;
        }

        if (value.Length > 64)
        {
            f.AddIssue("timezone", ZodMessages.TooLong(64));
            return null;
        }

        if (!ImportedTimeResolver.IsValidTimeZone(value))
        {
            f.AddIssue("timezone", UnknownTimezone);
            return null;
        }

        return value;
    }

    /// <summary>An absent member reads as <c>Undefined</c>, which is what the readers expect.</summary>
    private static JsonElement Member(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value : default;

    private static string KindName(JsonValueKind kind) => kind switch
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
