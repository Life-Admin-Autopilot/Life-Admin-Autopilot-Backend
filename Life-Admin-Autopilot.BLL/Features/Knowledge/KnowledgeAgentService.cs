using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Time;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>Today's briefing: a phrased summary plus the matters it was built from.</summary>
public sealed record DailyBriefing(
    string Headline,
    string Summary,
    IReadOnlyList<BriefingItem> Items,
    IReadOnlyList<MatterConflict> Conflicts,
    bool Phrased);

public sealed record BriefingItem(ObjectId TaskId, string Title, string Domain, DateTime? DueAt, bool Overdue);

/// <summary>
/// One clash with BOTH sides named.
///
/// <para>
/// <see cref="MatterConflict"/> on its own describes only the matter that was run
/// INTO — which is all a read-only banner needs, because the surface showing it
/// already knows which matter it is about. A list of every clash in the account has
/// no such context: nothing on that screen says which matter the row belongs to, and
/// either side may be the one the user moves. So the scan reports the pair.
/// </para>
/// </summary>
/// <param name="Other">The matter <paramref name="TaskId"/> ran into.</param>
public sealed record MatterClash(ObjectId TaskId, string Title, DateTime? DueAt, MatterConflict Other)
{
    /// <summary>
    /// The side that should move — the lower urgency of the two.
    ///
    /// <para>
    /// <see cref="MatterConflict.Yields"/> is stated from the scanned matter's point
    /// of view ("the candidate is the one that should move"), so reading it here is
    /// the only place that mapping is made and callers never have to remember which
    /// side "candidate" meant.
    /// </para>
    /// </summary>
    public ObjectId YieldsTaskId => Other.Yields ? TaskId : Other.TaskId;
}

/// <summary>
/// The Knowledge Agent — background work only, exactly as <c>ai_flow_V4</c> scopes
/// it: the daily briefing and the conflict re-check. Conflict checking at CREATION
/// belongs to the Planning Agent and is not duplicated here.
///
/// <para>
/// <b>The facts are computed, only the wording is generated.</b> Counts, due dates
/// and overdue flags come from the database; the model is handed that summary and
/// asked to phrase it. A model that invents a deadline in a briefing is worse than
/// no briefing, and this ordering makes that impossible — if the phrasing call
/// fails, <see cref="DailyBriefing.Phrased"/> is false and a deterministic sentence
/// is used instead. The briefing is never blocked on the model.
/// </para>
/// </summary>
public sealed class KnowledgeAgentService
{
    private readonly HttpClient _http;
    private readonly PlanningOptions _options;
    private readonly ConflictService _conflicts;
    private readonly ILogger<KnowledgeAgentService> _logger;

    public KnowledgeAgentService(
        HttpClient http,
        PlanningOptions options,
        ConflictService conflicts,
        ILogger<KnowledgeAgentService> logger)
    {
        _http = http;
        _options = options;
        _conflicts = conflicts;
        _logger = logger;
    }

    // ---- Conflict re-check ------------------------------------------------

    /// <summary>
    /// Re-run conflict detection for one existing task. Triggered by an edit — the
    /// "Task Edited Later" arrow on the flow diagram.
    /// </summary>
    public async Task<IReadOnlyList<MatterConflict>> RecheckAsync(
        ObjectId userId,
        TaskDocument task,
        CancellationToken cancellationToken = default)
    {
        var pool = await _conflicts.OpenMattersAsync(userId, cancellationToken).ConfigureAwait(false);
        return await _conflicts
            .CheckAsync(
                userId,
                ConflictService.MatterCandidate.From(task),
                task.DueAt,
                pool,
                excludeTaskId: task.Id,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    // ---- Daily briefing ---------------------------------------------------

    public async Task<DailyBriefing> BriefAsync(
        ObjectId userId,
        string? timezone,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var open = await _conflicts.OpenMattersAsync(userId, cancellationToken).ConfigureAwait(false);

        // Today's window in the user's zone, so "today" means their today.
        var zone = ResolveZone(timezone);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var endOfDay = TimeZoneInfo.ConvertTimeToUtc(localNow.Date.AddDays(1).AddTicks(-1), zone);

        var items = open
            .Where(t => t.DueAt is null || t.DueAt <= endOfDay)
            .OrderBy(t => t.DueAt ?? DateTime.MaxValue)
            .Select(t => new BriefingItem(
                t.Id,
                t.Title,
                t.Domain,
                t.DueAt,
                t.DueAt is { } d && d < now))
            .ToList();

        // Only clashes among what is actually on today's plate are worth surfacing,
        // which is what the `until` bound expresses. The scan itself is shared with
        // GET /me/conflicts, which passes no bound and therefore sees the account.
        //
        // Flattened back to one side per clash: the briefing's response shape and the
        // phrasing prompt both predate the pair, and on a screen that already says
        // "today" the matter run INTO is the only half that carries information.
        var conflicts = (await ScanAsync(userId, endOfDay, open, now, cancellationToken)
                .ConfigureAwait(false))
            .Select(c => c.Other)
            .ToList();

        var overdue = items.Count(i => i.Overdue);
        var fallback = DeterministicSummary(items.Count, overdue, conflicts.Count);

        var phrased = await PhraseAsync(items, overdue, conflicts, localNow, cancellationToken)
            .ConfigureAwait(false);

        return new DailyBriefing(
            phrased?.Headline ?? Headline(items.Count, overdue),
            phrased?.Summary ?? fallback,
            items,
            conflicts,
            phrased is not null);
    }

    // ---- Account-wide scan ------------------------------------------------

    /// <summary>
    /// Every clash between the user's open matters, each reported once.
    ///
    /// <para>
    /// <b>A conflict is a fact about the data, not an event.</b> Nothing records that
    /// voice, chat, a manual create or a document scan produced one — a clash simply
    /// IS two saved matters whose windows overlap, so asking the question again is
    /// what makes the answer current. That is why there is no conflict collection to
    /// keep in step, and why a clash resolved on any surface disappears from every
    /// other one.
    /// </para>
    ///
    /// <para>
    /// Driven off the task documents rather than any projection of them: the check
    /// needs the priority and estimate that decide how long a matter runs and which
    /// side yields, and every lighter shape in this file deliberately drops both.
    /// </para>
    /// </summary>
    /// <param name="until">
    /// Latest due date to scan, or null for all of them. The briefing passes the end
    /// of the user's today; the conflicts list passes nothing.
    /// </param>
    public async Task<IReadOnlyList<MatterClash>> ScanAsync(
        ObjectId userId,
        DateTime? until = null,
        IReadOnlyList<TaskDocument>? pool = null,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;
        var open = pool ?? await _conflicts.OpenMattersAsync(userId, cancellationToken).ConfigureAwait(false);

        // Undated matters cannot clash with anything — they occupy no span — so they
        // are not scanned. They still belong to the pool, which is what every other
        // caller of CheckAsync passes.
        var candidates = open
            .Where(t => t.DueAt is { } due && (until is null || due <= until))
            .OrderBy(t => t.DueAt)
            .ToList();

        var clashes = new List<MatterClash>();
        var seen = new HashSet<(ObjectId, ObjectId)>();

        foreach (var task in candidates)
        {
            var found = await _conflicts
                .CheckAsync(
                    userId,
                    ConflictService.MatterCandidate.From(task),
                    task.DueAt,
                    open,
                    excludeTaskId: task.Id,
                    now: now,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var c in found.Where(c => c.Kind == MatterConflict.TimeClash))
            {
                // "A clashes with B" is the same fact as "B clashes with A", and the
                // walk meets it from both ends. Ordering the pair by id gives the two
                // encounters one key, so the second is dropped.
                var key = task.Id < c.TaskId ? (task.Id, c.TaskId) : (c.TaskId, task.Id);
                if (seen.Add(key)) clashes.Add(new MatterClash(task.Id, task.Title, task.DueAt, c));
            }
        }

        return clashes;
    }

    private static string Headline(int total, int overdue) =>
        total == 0 ? "Nothing on today" :
        overdue > 0 ? $"{total} on today, {overdue} overdue" :
        $"{total} on today";

    private static string DeterministicSummary(int total, int overdue, int conflicts)
    {
        if (total == 0) return "Your day is clear. Nothing is due.";

        var parts = new List<string> { $"You have {total} matter{(total == 1 ? "" : "s")} on today" };
        if (overdue > 0) parts.Add($"{overdue} already overdue");
        if (conflicts > 0) parts.Add($"{conflicts} scheduling clash{(conflicts == 1 ? "" : "es")}");
        return string.Join(", ", parts) + ".";
    }

    private sealed record Phrasing(string Headline, string Summary);

    /// <summary>
    /// Hand the model the computed facts and ask only for wording. Returns null on
    /// any failure, which is what keeps the briefing deterministic under an outage.
    /// </summary>
    private async Task<Phrasing?> PhraseAsync(
        IReadOnlyList<BriefingItem> items,
        int overdue,
        IReadOnlyList<MatterConflict> conflicts,
        DateTime localNow,
        CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || items.Count == 0) return null;

        var facts = new
        {
            localTime = localNow.ToString("yyyy-MM-dd HH:mm"),
            total = items.Count,
            overdue,
            conflicts = conflicts.Select(c => new { c.Title, c.Reason }),
            matters = items.Select(i => new
            {
                i.Title,
                i.Domain,
                due = i.DueAt?.ToString("yyyy-MM-dd HH:mm") ?? "no date",
                i.Overdue,
            }),
        };

        var system =
            "You write a short daily briefing for a personal admin assistant.\n"
            + "You are given FACTS as JSON. Use only those facts — never invent a matter, "
            + "a date or a count.\n"
            + "Reply with ONLY JSON: {\"headline\":string,\"summary\":string}.\n"
            + "headline: at most 6 words. summary: 1-2 warm, plain sentences, no lists, "
            + "no markdown. Mention overdue items and clashes if present. "
            + "Reply in the language the matters are written in.";

        var request = new GeminiRequest(
            new[] { new GeminiContent(new[] { new GeminiPart(JsonSerializer.Serialize(facts)) }) },
            new GeminiSystem(new[] { new GeminiPart(system) }),
            // Thinking off, and the budget raised well past what two sentences of
            // JSON need. Reasoning tokens come out of the SAME allowance as the
            // answer, so at 512-with-thinking this call spent its budget deliberating
            // and returned either a truncated object or nothing — and because a
            // half-written object fails to parse, the failure was invisible: the
            // briefing simply reported `phrased: false` forever and nobody could tell
            // it apart from an unreachable model. Same defect as DigestProseWriter's,
            // one surface along.
            new GeminiConfig(0.4, 1024, new GeminiThinking(0)));

        foreach (var model in _options.ModelChain)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _options.GenerateUriFor(model))
                {
                    Content = JsonContent.Create(request),
                };
                message.Headers.Add("x-goog-api-key", _options.ApiKey);

                using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode is 503 or 429) continue;
                    _logger.LogWarning("briefing:phrasing-failed status={Status}", (int)response.StatusCode);
                    return null;
                }

                return ParsePhrasing(body);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "briefing:phrasing-error model={Model}", model);
                return null;
            }
        }

        return null;
    }

    private Phrasing? ParsePhrasing(string body)
    {
        try
        {
            var candidate = JsonDocument.Parse(body).RootElement.GetProperty("candidates")[0];

            // A candidate that stopped for any reason other than finishing is a
            // fragment. Here that usually surfaces as unparsable JSON a moment later,
            // but catching it by name logs WHY the briefing fell back to its
            // deterministic sentence instead of leaving "unparsable" as the only clue.
            if (candidate.TryGetProperty("finishReason", out var finish)
                && finish.GetString() is { } reason
                && !string.Equals(reason, "STOP", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("briefing:phrasing-unfinished reason={Reason}", reason);
                return null;
            }

            var text = candidate
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString();

            if (string.IsNullOrWhiteSpace(text)) return null;

            var json = text.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var start = json.IndexOf('\n');
                var end = json.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start) json = json[(start + 1)..end].Trim();
            }

            var root = JsonDocument.Parse(json).RootElement;
            var headline = root.TryGetProperty("headline", out var h) ? h.GetString() : null;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;

            return string.IsNullOrWhiteSpace(summary) ? null : new Phrasing(headline ?? string.Empty, summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "briefing:unparsable-phrasing");
            return null;
        }
    }

    /// <summary>
    /// An unknown zone must not fail the briefing. It resolves to the product
    /// default, which produces the right day for this product's users; UTC produced
    /// one that rolled over at 02:00 or 03:00 their time.
    /// </summary>
    private static TimeZoneInfo ResolveZone(string? timezone) => AppTimeZone.Resolve(timezone);

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("systemInstruction")] GeminiSystem SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GeminiConfig GenerationConfig);

    private sealed record GeminiContent([property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiSystem([property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiPart([property: JsonPropertyName("text")] string Text);

    private sealed record GeminiConfig(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
        [property: JsonPropertyName("thinkingConfig")] GeminiThinking Thinking);

    /// <summary>Zero, for the reason given at the call site.</summary>
    private sealed record GeminiThinking(
        [property: JsonPropertyName("thinkingBudget")] int ThinkingBudget);
}
