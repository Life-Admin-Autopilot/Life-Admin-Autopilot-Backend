using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

public sealed class TranslateQuotaDto
{
    [JsonPropertyName("locale")]
    public string Locale { get; init; } = string.Empty;

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("used")]
    public int Used { get; init; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; init; }

    /// <summary>Midnight UTC on the 1st of the following month.</summary>
    [JsonPropertyName("resetAt")]
    public DateTime ResetAt { get; init; }
}

/// <summary>
/// The read-only half of <c>server/src/modules/tasks/translateQuota.ts</c> plus
/// <c>countTranslatable</c> from <c>translateMatters/run.ts</c>.
///
/// <para>
/// The count endpoint exists so the sheet can show what a selection would cost
/// BEFORE the user spends the month on it — the allowance is one backlog sweep
/// per language per month, and it is not refunded on undo.
/// </para>
///
/// <para>The admission/release half belongs to the AI slice: with no
/// <c>GEMINI_API_KEY</c> the translate route short-circuits to 503 long before a
/// run is claimed.</para>
/// </summary>
public sealed class TranslateQuotaService
{
    /// <summary>One backlog sweep per language per month, every tier.</summary>
    public const int RunsPerMonth = 1;

    /// <summary>Arabic, Arabic Supplement, Extended-A/B, and the presentation forms.</summary>
    private static readonly Regex ArabicScript = new(
        "[؀-ۿݐ-ݿࡰ-࢏ࢠ-ࣿﭐ-﷿ﹰ-﻿]",
        RegexOptions.Compiled);

    private readonly IMongoCollection<TranslationUsageCounterDocument> _counters;
    private readonly IMongoCollection<TaskDocument> _tasks;

    public TranslateQuotaService(IMongoDatabase database)
    {
        _counters = database.GetCollection<TranslationUsageCounterDocument>(
            MongoCollections.TranslationUsageCounters);
        _tasks = database.GetCollection<TaskDocument>(MongoCollections.Tasks);
    }

    public async Task<TranslateQuotaDto> ReadAsync(
        ObjectId userId,
        string locale,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var month = TranslationUsageBuckets.UtcMonth(at);

        var row = await _counters
            .Find(Builders<TranslationUsageCounterDocument>.Filter.And(
                Builders<TranslationUsageCounterDocument>.Filter.Eq(c => c.UserId, userId),
                Builders<TranslationUsageCounterDocument>.Filter.Eq(c => c.Month, month),
                Builders<TranslationUsageCounterDocument>.Filter.Eq(c => c.Locale, locale)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var used = row?.Count ?? 0;

        return new TranslateQuotaDto
        {
            Locale = locale,
            Limit = RunsPerMonth,
            Used = used,
            Remaining = Math.Max(0, RunsPerMonth - used),
            ResetAt = TranslationUsageBuckets.NextMonthStart(month),
        };
    }

    /// <summary>
    /// How many matters a selection would translate. Cheap — a scan of three
    /// projected fields, no model call.
    /// </summary>
    public async Task<int> CountTranslatableAsync(
        ObjectId userId,
        string targetLocale,
        string preset,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var now = at ?? DateTime.UtcNow;

        var filter = new BsonDocument
        {
            ["userId"] = userId,
            ["deletedAt"] = new BsonDocument("$exists", false),
            ["status"] = new BsonDocument("$in", new BsonArray { "open", "snoozed" }),
        };

        // Presets are bounded by DUE DATE and always include anything overdue —
        // `$lte: end` alone covers it, since an overdue dueAt is in the past. An
        // UNDATED matter has no due date to bound, so it appears only in `all`.
        var end = EndOfPreset(preset, now);
        if (end.HasValue)
        {
            filter["dueAt"] = new BsonDocument { ["$ne"] = BsonNull.Value, ["$lte"] = end.Value };
        }

        var rows = await _tasks
            .Find(new BsonDocumentFilterDefinition<TaskDocument>(filter))
            .Project(Builders<TaskDocument>.Projection
                .Include(t => t.Title)
                .Include(t => t.SourceLocale)
                .Include(t => t.I18n))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Count(row => NeedsTranslation(row, targetLocale));
    }

    private static DateTime? EndOfPreset(string preset, DateTime now) => preset switch
    {
        "all" => null,
        "thisWeek" => now.AddDays(7),

        // Calendar month, matching JS `setUTCMonth(+1)`.
        "thisMonth" => now.AddMonths(1),
        _ => null,
    };

    private static bool NeedsTranslation(BsonDocument row, string target)
    {
        var stored = row.TryGetValue("sourceLocale", out var raw) && raw.IsString ? raw.AsString : null;
        var title = row.TryGetValue("title", out var t) && t.IsString ? t.AsString : null;

        var source = AiLocales.Resolve(stored ?? DetectSourceLocale(title));
        if (source == target)
        {
            return false;
        }

        // Already has a copy in this language — re-translating would spend the
        // month reproducing text the user can already read, and would overwrite
        // any edit they have since made to it.
        if (row.TryGetValue("i18n", out var i18n) && i18n.IsBsonDocument)
        {
            return !i18n.AsBsonDocument.Contains(target);
        }

        return true;
    }

    /// <summary>
    /// Which language a matter's text is written in, inferred from the SCRIPT.
    /// Free, instant, and for the two languages this app ships it is exact —
    /// Arabic and English share no letters.
    /// </summary>
    public static string DetectSourceLocale(params string?[] text) =>
        text.Any(v => !string.IsNullOrEmpty(v) && ArabicScript.IsMatch(v)) ? "ar" : AiLocales.Default;
}
