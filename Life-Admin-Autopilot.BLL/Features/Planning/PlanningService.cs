using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Knowledge;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Time;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Planning;

/// <summary>
/// One conflict found against something the user already has.
///
/// <para>
/// <b><see cref="Kind"/> is carried, not re-derived.</b> It used to be dropped on
/// the way out of <see cref="MatterConflict"/>, which left every consumer to work
/// out what sort of clash it had from the prose in <see cref="Reason"/> — the voice
/// policy did it with <c>Reason.Contains("already")</c>. That is a classifier built
/// on a sentence nobody promised to keep stable: reword the reason, or localise it,
/// and every duplicate silently becomes a time clash and gets asked the wrong
/// question. The value exists upstream; it just has to survive the trip.
/// </para>
/// </summary>
/// <param name="Kind"><c>time_clash</c> or <c>duplicate</c> — see <see cref="MatterConflict"/>.</param>
public sealed record PlanningConflict(
    ObjectId TaskId,
    string Title,
    DateTime? DueAt,
    string Reason,
    string Kind = MatterConflict.TimeClash);

/// <summary>A proposed task. <b>Nothing here has been saved.</b></summary>
/// <param name="TimeAssumed">
/// The DAY came from the user; the CLOCK TIME did not.
///
/// <para>
/// A task row stores one instant, so "Wednesday" has to become some specific
/// moment — and the extractor will pick 09:00 whether or not anyone said so. Left
/// unmarked that guess is presented to the user as though they had chosen it,
/// which is how three matters dictated in one breath all ended up at 09:00 with
/// nothing asked. The flag does not change the instant; it records that the
/// instant is ours, not theirs, so the UI can ask for the real one before saving.
/// </para>
/// </param>
public sealed record TaskDraft(
    string Title,
    string Domain,
    string Priority,
    string? Kind,
    DateTime? DueAt,
    string? Notes,
    string SourceType,
    double Confidence,
    IReadOnlyList<PlanningConflict> Conflicts,
    bool TimeAssumed = false,
    MoneyDocument? Amount = null);

/// <summary>
/// The propose half of propose/commit: turn one utterance into draft tasks and
/// check each against what the user already has — <b>all before saving</b>, which
/// is the ordering the SRS calls deliberate.
///
/// <para>
/// <b>Why this does not go through Langflow.</b> The shipped planning flow's tools
/// (<c>createTask</c>, <c>holdForClarification</c>) write to Mongo the moment the
/// agent calls them. That is the opposite of a proposal, and no prompt change makes
/// a tool-calling agent reliably not use its tools. So this path calls the model
/// directly for extraction only — no tools bound, nothing it can write with.
/// </para>
/// </summary>
public sealed class PlanningService
{
    /// <summary>The frontend's enums. A draft outside these is unrenderable.</summary>
    private static readonly string[] Domains = { "health", "home", "car", "finance", "family", "pets" };
    private static readonly string[] Priorities = { "low", "normal", "high", "urgent" };

    private readonly HttpClient _http;
    private readonly PlanningOptions _options;
    private readonly TaskRepository _tasks;
    private readonly ConflictService _conflicts;
    private readonly ILogger<PlanningService> _logger;

    public PlanningService(
        HttpClient http,
        PlanningOptions options,
        TaskRepository tasks,
        ConflictService conflicts,
        ILogger<PlanningService> logger)
    {
        _http = http;
        _options = options;
        _tasks = tasks;
        _conflicts = conflicts;
        _logger = logger;
    }

    /// <summary>
    /// Extraction on its own: one utterance in, unconflicted drafts out.
    ///
    /// <para>
    /// Public because the VOICE worker needs the same reading of a sentence that
    /// <c>/api/planning/propose</c> gets, and needs it without the conflict pass —
    /// it runs its own, against a pool it has already loaded. If the two paths
    /// extracted differently, the same sentence typed and spoken would produce
    /// different matters, and neither answer would be explainable.
    /// </para>
    /// </summary>
    /// <param name="at">
    /// The instant relative dates are read against. The worker passes when the user
    /// SPOKE, not when the note was picked up: a note captured at 23:55 and processed
    /// at 00:02 must still resolve "tomorrow" to the day the speaker meant. Defaults
    /// to now, which is right for the interactive route.
    /// </param>
    public async Task<IReadOnlyList<TaskDraft>> ExtractDraftsAsync(
        string text,
        string? timezone,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        RequireConfigured();
        return await ExtractAsync(text, timezone, at, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskDraft>> ProposeAsync(
        ObjectId userId,
        string text,
        string? timezone,
        CancellationToken cancellationToken = default)
    {
        RequireConfigured();

        var extracted = await ExtractAsync(text, timezone, at: null, cancellationToken).ConfigureAwait(false);

        // One read of the user's open matters, reused for every draft.
        var open = await _tasks.Tasks
            .Find(Builders<TaskDocument>.Filter.And(
                Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
                Builders<TaskDocument>.Filter.Eq(t => t.Status, "open")))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var drafts = new List<TaskDraft>(extracted.Count);
        foreach (var raw in extracted)
        {
            // Same rules the Knowledge Agent re-runs after an edit — one
            // implementation, so a draft and a saved task can never disagree about
            // what counts as a conflict.
            var found = await _conflicts
                .CheckAsync(
                    userId,
                    // A draft has no estimate — nothing in the extraction schema asks
                    // for one — so its span comes from the duration table, exactly as
                    // it will once saved.
                    new ConflictService.MatterCandidate(raw.Title, raw.Domain, raw.Priority),
                    raw.DueAt,
                    open,
                    excludeTaskId: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            drafts.Add(raw with
            {
                Conflicts = found
                    .Select(c => new PlanningConflict(c.TaskId, c.Title, c.DueAt, c.Reason, c.Kind))
                    .ToList(),
            });
        }

        return drafts;
    }

    // ---- Extraction -------------------------------------------------------

    private void RequireConfigured()
    {
        if (!_options.IsConfigured)
        {
            throw new AppException(
                503,
                "planning_not_configured",
                "Planning is not configured. Set PLANNING_API_KEY to enable it.");
        }
    }

    private async Task<List<TaskDraft>> ExtractAsync(
        string text,
        string? timezone,
        DateTime? at,
        CancellationToken cancellationToken)
    {
        var now = at is { } anchor
            ? new DateTimeOffset(DateTime.SpecifyKind(anchor, DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;
        var zone = AppTimeZone.ResolveId(timezone);

        var system =
            "You split a user's message into the distinct tasks it describes.\n"
            + $"Current UTC time: {now:o}. The user's timezone: {zone}.\n"
            + "Reply with ONLY a JSON array, no prose and no code fence. Each element:\n"
            + "{\"title\":string, \"domain\":one of health|home|car|finance|family|pets, "
            + "\"priority\":one of low|normal|high|urgent, \"kind\":\"reminder\"|\"list\", "
            + "\"dueAt\":ISO-8601 with offset or null, "
            + "\"duePrecision\":\"time\"|\"day\"|\"none\", \"notes\":string|null, "
            + "\"amount\":number|null, \"currency\":ISO-4217 code|null, "
            + "\"confidence\":number between 0 and 1}\n"
            // "the user's own language" was read as their PREFERRED language, not this
            // sentence's: the same English utterance came back once as "Renew passport"
            // and once as "تجديد جواز السفر". A translated title is a task the user
            // cannot find by searching the words they said.
            + "Rules: write the title in the SAME LANGUAGE AS THE INPUT ABOVE — copy the "
            + "user's own words where you can, and NEVER translate between Arabic and "
            + "English in either direction. One element per distinct "
            + "task. Use \"reminder\" only when a time is known, otherwise \"list\" with "
            + "dueAt null. Never invent a time that was not implied. confidence reflects "
            + "how sure you are the task was actually requested.\n"
            // priority was a listed enum with no rule for choosing it, so every voice
            // item came back "normal" — the same defect the chat prompt had. It is not
            // cosmetic here either: VoiceAutoFilePolicy.CostOfWrong reads high/urgent,
            // and ReminderUrgency scores on it. This rubric is a WORD-FOR-WORD PEER of
            // the one in the chat prompt's HOW TO HOLD section; the two lanes must
            // agree or the same sentence gets a different priority depending on
            // whether it was typed or spoken. Change one, change the other.
            + "priority is a JUDGEMENT about what the matter costs if it slips, never a "
            + "default:\n"
            + "  \"urgent\" — a hard external deadline with a penalty, or health and "
            + "safety: a court date, a visa or passport expiring, an overdue bill, a "
            + "flight, a medical appointment, medication, a car inspection now due.\n"
            + "  \"high\" — a fixed commitment involving someone else's time, or money "
            + "moving by a date: an appointment, rent, an instalment, a fee, an exam, a "
            + "booking already made, anything a person is waiting on.\n"
            + "  \"normal\" — an ordinary dated errand with no penalty for being a day "
            + "late: a haircut, groceries, a chore with a day on it.\n"
            + "  \"low\" — nothing happens if it slips: buy bread, tidy the garage, text "
            + "mom back, an undated someday-wish.\n"
            + "Read the MATTER, not the words. Domain is a hint and not the answer.\n"
            // duePrecision is the whole point of the schema change: without somewhere to
            // SAY "they gave me a day but no clock time", a model asked for one ISO
            // instant has to invent 09:00 and cannot report that it did.
            + "duePrecision is REQUIRED and describes how much of dueAt the user actually "
            + "said:\n"
            + "  \"time\" — they stated or clearly implied a clock time (\"at 4\", "
            + "\"tonight\", \"first thing\").\n"
            + "  \"day\" — they named a day or date but NO clock time (\"Wednesday\", "
            + "\"next week\", \"الأربع\"). Still fill dueAt: use 09:00 in the user's "
            + "timezone as a placeholder, and mark it \"day\" so it can be confirmed.\n"
            + "  \"none\" — no date at all; dueAt is null.\n"
            // The figure the user SAID, not one inferred from what a thing usually
            // costs. "Pay 200 dollars for Claude" carries an amount; "pay the
            // electricity bill" does not, and guessing one there would put an
            // invented number into a spending total the user checks against paper.
            + "amount is the figure the user actually STATED, in MAJOR units as "
            + "spoken (200 for \"200 dollars\", 49.99 for \"49.99\"). Never infer a "
            + "price from what something typically costs — no figure stated means "
            + "amount null.\n"
            // Symbols are ambiguous by construction: $ is USD, CAD, AUD and more.
            // MoneyVocabulary.NormalizeCurrency refuses anything that is not three
            // letters, so an unresolvable currency drops the whole amount rather
            // than filing it under a guessed one.
            + "currency is the ISO 4217 CODE (USD, EGP, EUR) — never a symbol. "
            + "Resolve \"dollars\" to USD, \"pounds\"/\"جنيه\" to EGP for this user "
            + "unless they clearly meant another country's. If amount is null, "
            + "currency is null; if you cannot tell the currency, set BOTH to null.";

        var request = new GeminiRequest(
            new[] { new GeminiContent(new[] { new GeminiPart(text) }) },
            new GeminiSystem(new[] { new GeminiPart(system) }),
            new GeminiConfig(0.2, 2048));

        // Walk the model chain. 503/429 is a capacity answer about ONE model, so the
        // next one is worth trying immediately; any other status is about the request
        // itself and would fail identically everywhere, so it stops the walk.
        foreach (var model in _options.ModelChain)
        {
            // A per-attempt budget, so the chain can actually reach its fallbacks.
            // Without it one model that HANGS consumes the whole HttpClient timeout
            // and there is no time left to try the others — which is the failure this
            // walk exists to survive.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds));

            string body;
            HttpStatusCode statusCode;

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _options.GenerateUriFor(model))
                {
                    Content = JsonContent.Create(request),
                };
                message.Headers.Add("x-goog-api-key", _options.ApiKey);

                using var response = await _http.SendAsync(message, attempt.Token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
                statusCode = response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
                                       && !cancellationToken.IsCancellationRequested)
            {
                // A hang or a dropped connection is the commonest shape of "this model
                // is unavailable", and it has to be treated exactly like the 503 it
                // would have been if the model had managed to answer at all. Letting it
                // escape turned one slow model into a 500 for the whole request.
                //
                // The caller's own cancellation is excluded by the filter: the user
                // navigating away is not a model failure and must not walk the chain.
                _logger.LogWarning(
                    "planning:model-unreachable model={Model} reason={Reason}",
                    model,
                    ex.GetType().Name);
                continue;
            }

            if (statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
            {
                if (!string.Equals(model, _options.Model, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("planning:used-fallback model={Model}", model);
                }

                return Parse(body);
            }

            var status = (int)statusCode;
            _logger.LogWarning(
                "planning:model-failed model={Model} status={Status} body={Body}",
                model,
                status,
                body.Length > 300 ? body[..300] : body);

            if (status is not (503 or 429)) break;
        }

        throw new AppException(
            503,
            "planning_unavailable",
            "The planning model is unavailable right now. Try again in a moment.");
    }

    private List<TaskDraft> Parse(string body)
    {
        var drafts = new List<TaskDraft>();

        string? raw;
        try
        {
            raw = JsonDocument.Parse(body)
                .RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "planning:unrecognised-model-response");
            throw new AppException(502, "planning_bad_response", "The planning model returned an unusable response.");
        }

        if (string.IsNullOrWhiteSpace(raw)) return drafts;

        // Models fence JSON even when told not to; strip it rather than fail.
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('\n');
            var end = json.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) json = json[(start + 1)..end].Trim();
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "planning:model-returned-non-json payload={Payload}", json[..Math.Min(300, json.Length)]);
            throw new AppException(502, "planning_bad_response", "The planning model returned an unusable response.");
        }

        if (root.ValueKind != JsonValueKind.Array) return drafts;

        foreach (var element in root.EnumerateArray())
        {
            var title = Str(element, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var dueAt = Date(element, "dueAt");

            drafts.Add(new TaskDraft(
                title.Trim(),
                NormaliseDomain(Str(element, "domain")),
                NormalisePriority(Str(element, "priority")),
                Str(element, "kind") is "reminder" or "list" ? Str(element, "kind") : null,
                dueAt,
                Str(element, "notes"),
                SourceType: "text",
                Confidence: Num(element, "confidence") ?? 0.5,
                Conflicts: Array.Empty<PlanningConflict>(),
                TimeAssumed: IsTimeAssumed(element, dueAt),
                // Through the same gate a scanned figure goes through, so a
                // spoken amount and a read one are bounded, rounded and
                // currency-checked identically. It returns null on anything it
                // cannot make safe — no currency, absurd magnitude — and a
                // draft with no amount is the ordinary case, not a failure.
                Amount: MoneyVocabulary.Normalize(
                    (decimal?)Num(element, "amount"),
                    Str(element, "currency"),
                    source: "ai")));
        }

        return drafts;
    }

    /// <summary>
    /// Did the user actually give a clock time, or did the model supply one?
    ///
    /// <para>
    /// <b>Trusts the model's own answer, then checks its work.</b> <c>duePrecision</c>
    /// is what the prompt asks for, but an older or fallback model that has never seen
    /// that instruction simply omits the field — and treating "absent" as "the user
    /// said a time" reinstates exactly the silent 09:00 this exists to stop. So a
    /// missing field falls back to the giveaway: a due instant sitting precisely on
    /// the hour, at one of the hours nobody dictates by name, is a default rather than
    /// a decision. This can misfire on a user who really did say "nine o'clock" — the
    /// cost of that is one confirmable time chip, against the cost of the other error,
    /// which is a matter silently scheduled at a time they never chose.
    /// </para>
    /// </summary>
    private static bool IsTimeAssumed(JsonElement element, DateTime? dueAt)
    {
        if (dueAt is null) return false;

        var precision = Str(element, "duePrecision");
        if (precision is not null)
        {
            return precision.Equals("day", StringComparison.OrdinalIgnoreCase);
        }

        // dueAt has been normalised to UTC by now, so the local hour is unknown here —
        // hence the minute/second test, which survives any offset that is a whole
        // number of hours, plus a UTC-hour window wide enough to cover the usual
        // placeholders (09:00 local) across the timezones this ships to.
        var due = dueAt.Value;
        return due is { Minute: 0, Second: 0 } && due.Hour is >= 3 and <= 12;
    }

    // ---- Vocabulary guards ------------------------------------------------
    //
    // The model is told the enums but is not bound by them; an out-of-vocabulary
    // domain would render as an unknown category chip. Falling back beats rejecting
    // an otherwise good draft.

    public static string NormaliseDomain(string? value) =>
        value is not null && Domains.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : "home";

    public static string NormalisePriority(string? value) =>
        value is not null && Priorities.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : "normal";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(
            v.GetString(),
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("systemInstruction")] GeminiSystem SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GeminiConfig GenerationConfig);

    private sealed record GeminiContent([property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiSystem([property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiPart([property: JsonPropertyName("text")] string Text);

    private sealed record GeminiConfig(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);
}
