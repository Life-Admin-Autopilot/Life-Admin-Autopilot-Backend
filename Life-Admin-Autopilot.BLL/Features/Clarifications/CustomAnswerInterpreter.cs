using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// Port of <c>server/src/modules/ai/resolveClarificationAnswer.ts</c> — the one
/// bounded model call behind <c>{type:'custom'}</c> on
/// <c>POST /me/clarifications/{id}/resolve</c>.
///
/// <para>
/// <b>The contract is Node's, not a redesign.</b> One call, function calling
/// FORCED (<c>mode: ANY</c>, a single allowed function), temperature 0. The patch
/// is the truthy subset of <c>title, domain, priority, notes, dueAt</c>; anything
/// else the model invents is dropped. The recorded answer label is the user's raw
/// typed text — never the model's paraphrase. Any <c>dueAt</c> flips the task to a
/// live <c>reminder</c>, whatever kind of question was asked. Garbage input does
/// NOT 400: the forced call yields a title-only patch and the question resolves
/// with the garbage on record — the reference's own comment says this exists "so
/// the route can keep the clarification OPEN and never lose the held item", and
/// the two real failures behave exactly that way: no function call back is a 502
/// (<c>clarification_unresolved</c>) and invalid args a 400, both thrown BEFORE
/// the close-out so the question survives.
/// </para>
///
/// <para>
/// <b>Two deliberate divergences</b>, recorded in <c>docs/DIVERGENCES.md</c>: this
/// walks <see cref="PlanningOptions.ModelChain"/> with a per-attempt timeout
/// (Node had one hard-coded model and no timeout at all), and the model identity
/// comes from <see cref="PlanningOptions"/>, so Node's <c>gemini-2.5</c>-only
/// <c>thinkingBudget</c> clause has nothing to apply to.
/// </para>
///
/// <para>
/// <b>Why the gate is <see cref="PlanningOptions.IsConfigured"/> and not
/// <c>AiAvailability</c>.</b> The seam this call rides is the Gemini-direct
/// planning one (<c>PLANNING_API_KEY</c>). <c>AiAvailability</c> answers for
/// <c>GEMINI_API_KEY</c>, which this deployment deliberately leaves empty —
/// setting it to satisfy the old gate would push five Matters routes past their
/// honest 503s into <c>NotWiredHere</c> 500s (see the note on
/// <c>PlanningOptions.ApiKey</c>).
/// </para>
/// </summary>
public sealed class CustomAnswerInterpreter
{
    private readonly HttpClient _http;
    private readonly PlanningOptions _options;
    private readonly ILogger<CustomAnswerInterpreter> _logger;

    public CustomAnswerInterpreter(
        HttpClient http,
        PlanningOptions options,
        ILogger<CustomAnswerInterpreter> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// The typed answer, interpreted against the question it answers. Throws
    /// <c>502 clarification_unresolved</c> when the model returns no function call
    /// and <c>400 invalid_tool_args</c> when its arguments fail validation — the
    /// caller must let both escape before closing the clarification out.
    /// </summary>
    public async Task<ClarificationTaskPatch> InterpretAsync(
        ClarificationDocument doc,
        string text,
        string? timezone,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(doc, text, timezone, nowUtc);

        // The same chain walk as PlanningService: 503/429 (and the hang that would
        // have been one) is a capacity answer about ONE model; anything else would
        // fail identically everywhere and stops the walk.
        foreach (var model in _options.ModelChain)
        {
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
                _logger.LogWarning(
                    "clarification:model-unreachable model={Model} reason={Reason}",
                    model,
                    ex.GetType().Name);
                continue;
            }

            if (statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
            {
                return ToPatch(ParseFunctionArgs(body), timezone);
            }

            _logger.LogWarning(
                "clarification:model-failed model={Model} status={Status}",
                model,
                (int)statusCode);

            if ((int)statusCode is not (503 or 429)) break;
        }

        throw Unresolved();
    }

    /// <summary>Node's 502 — thrown before close-out, so the question stays open.</summary>
    private static AppException Unresolved() =>
        new(502, "clarification_unresolved", "Could not turn that answer into a task. Try rephrasing it.");

    private object BuildRequest(ClarificationDocument doc, string text, string? timezone, DateTime nowUtc)
    {
        // Node's anchor: "NOW (user local time): <iso+offset> (<weekday>)". The zone
        // math mirrors HoldTimeNormalizer's — unknown zones read as UTC, never the
        // server's own locale.
        var zone = ResolveZone(timezone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var offset = zone.GetUtcOffset(nowUtc);
        var anchor = string.Create(
            CultureInfo.InvariantCulture,
            $"{local:yyyy-MM-dd'T'HH:mm:ss}{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm} ({local.DayOfWeek})");

        var system =
            "You interpret a user's typed answer to a clarifying question about a task "
            + "they already filed. Call updateTask with ONLY the fields the answer "
            + "settles. Keep the title in the user's language. dueAt is ISO 8601 in "
            + "the user's local time.\n"
            + $"NOW (user local time): {anchor}\n"
            + $"QUESTION: {doc.Question}\n"
            + $"TASK: title \"{doc.Draft.Title}\", domain {doc.Draft.Domain}"
            + (doc.Draft.DueAt.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $", current dueAt {doc.Draft.DueAt.Value:o}")
                : ", no date yet");

        return new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = $"The user's answer: {text}" } } },
            },
            systemInstruction = new { parts = new[] { new { text = system } } },
            generationConfig = new { temperature = 0.0, maxOutputTokens = 1024 },
            tools = new[]
            {
                new
                {
                    functionDeclarations = new object[]
                    {
                        new
                        {
                            name = "updateTask",
                            description = "Apply what the user's answer settled onto the task.",
                            parameters = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["title"] = new { type = "string" },
                                    ["domain"] = new { type = "string", @enum = TaskVocabulary.Domains },
                                    ["priority"] = new { type = "string", @enum = TaskVocabulary.Priorities },
                                    ["notes"] = new { type = "string" },
                                    ["dueAt"] = new { type = "string" },
                                },
                            },
                        },
                    },
                },
            },
            toolConfig = new
            {
                functionCallingConfig = new
                {
                    mode = "ANY",
                    allowedFunctionNames = new[] { "updateTask" },
                },
            },
        };
    }

    private static TimeZoneInfo ResolveZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>The first functionCall's args, from any part of the first candidate.</summary>
    private JsonElement? ParseFunctionArgs(string body)
    {
        try
        {
            using var parsed = JsonDocument.Parse(body);
            if (!parsed.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return null;
            }

            if (!candidates[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var call)
                    && call.TryGetProperty("args", out var args)
                    && args.ValueKind == JsonValueKind.Object)
                {
                    return args.Clone();
                }
            }

            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("clarification:model-body-unparseable reason={Reason}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The truthy five-field subset, hard-validated. Enum misses and a malformed
    /// dueAt are Node's <c>400 invalid_tool_args</c> — reject, never clamp.
    /// </summary>
    private static ClarificationTaskPatch ToPatch(JsonElement? args, string? timezone)
    {
        if (args is not { } a) throw Unresolved();

        var title = Truthy(a, "title");
        var domain = Truthy(a, "domain");
        var priority = Truthy(a, "priority");
        var notes = Truthy(a, "notes");
        var dueAtIso = Truthy(a, "dueAt");

        var issues = new List<string>();
        if (domain is not null && !TaskVocabulary.Domains.Contains(domain)) issues.Add("domain");
        if (priority is not null && !TaskVocabulary.Priorities.Contains(priority)) issues.Add("priority");
        if (dueAtIso is not null && !HoldTimeNormalizer.IsStrictIso(dueAtIso)) issues.Add("dueAt");
        if (issues.Count > 0)
        {
            throw AppException.BadRequest(
                "invalid_tool_args",
                $"The interpreted answer failed validation: {string.Join(", ", issues)}.");
        }

        var dueAt = dueAtIso is null ? (DateTime?)null : HoldTimeNormalizer.Normalize(dueAtIso, timezone);

        return new ClarificationTaskPatch(
            Title: title,
            Notes: notes,
            DueAt: dueAt,
            // Node: `confirmsDate` — ANY resolution producing a dueAt arms the reminder.
            Kind: dueAt.HasValue ? "reminder" : null,
            Domain: domain,
            Priority: priority);
    }

    private static string? Truthy(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
        && text.Trim() is { Length: > 0 } trimmed
            ? trimmed
            : null;
}
