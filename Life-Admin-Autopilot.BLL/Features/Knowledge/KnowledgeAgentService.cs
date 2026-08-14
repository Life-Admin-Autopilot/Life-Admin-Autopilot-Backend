using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
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
            .CheckAsync(userId, task.Title, task.DueAt, pool, excludeTaskId: task.Id, cancellationToken)
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

        // Only clashes among what is actually on today's plate are worth surfacing.
        var conflicts = new List<MatterConflict>();
        var seen = new HashSet<(ObjectId, ObjectId)>();
        foreach (var item in items.Where(i => i.DueAt is not null))
        {
            var found = await _conflicts
                .CheckAsync(userId, item.Title, item.DueAt, open, excludeTaskId: item.TaskId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var c in found.Where(c => c.Kind == MatterConflict.TimeClash))
            {
                // A clashes with B is the same fact as B clashes with A.
                var key = item.TaskId < c.TaskId ? (item.TaskId, c.TaskId) : (c.TaskId, item.TaskId);
                if (seen.Add(key)) conflicts.Add(c);
            }
        }

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
            new GeminiConfig(0.4, 512));

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
            var text = JsonDocument.Parse(body)
                .RootElement.GetProperty("candidates")[0]
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

    private static TimeZoneInfo ResolveZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception)
        {
            // An unknown zone must not fail the briefing — UTC still produces a
            // correct, if slightly off-boundary, day.
            return TimeZoneInfo.Utc;
        }
    }

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
