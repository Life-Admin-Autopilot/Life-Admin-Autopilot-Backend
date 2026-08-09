using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.Tasks;

/// <summary>
/// Port of <c>server/src/models/TranslationUsageCounter.ts</c> — the once-a-month,
/// per-language allowance for translating a backlog.
///
/// <para>
/// The bucket carries the TARGET LOCALE, so switching to a third language next
/// week is not blocked by having translated into Arabic today. The unique
/// <c>{userId, month, locale}</c> index is created by <c>KernelIndexProvider</c>
/// (§7) — it is load-bearing, not an optimisation: the admission path's
/// duplicate-key retry only works because the index exists to produce the error.
/// </para>
/// </summary>
public sealed class TranslationUsageCounterDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public ObjectId UserId { get; set; }

    /// <summary>A <c>YYYY-MM</c> UTC bucket.</summary>
    public string Month { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The UTC month bucket and its reset instant, ported from
/// <c>models/DocumentScanUsageCounter.ts</c> and <c>modules/tasks/translateQuota.ts</c>.
/// </summary>
public static class TranslationUsageBuckets
{
    /// <summary>Current <c>YYYY-MM</c> in UTC.</summary>
    public static string UtcMonth(DateTime? now = null) =>
        (now ?? DateTime.UtcNow).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Midnight UTC on the 1st of the month FOLLOWING <paramref name="month"/>.
    ///
    /// <para>
    /// Node builds this with <c>Date.UTC(year, mon, 1)</c> where <c>mon</c> is the
    /// 1-based month parsed out of the bucket. <c>Date.UTC</c> takes a 0-indexed
    /// month, so passing the 1-based value straight through lands on the first of
    /// the NEXT month, and December rolls into January via the same overflow.
    /// Deliberate there, so reproduced here rather than "corrected".
    /// </para>
    /// </summary>
    public static DateTime NextMonthStart(string month)
    {
        var parts = month.Split('-');
        var year = parts.Length > 0 && int.TryParse(parts[0], out var y) ? y : 1970;
        var mon = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 1;

        // AddMonths on the 1st of `mon` reproduces the JS month overflow, including
        // the December -> January year roll.
        return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(mon);
    }
}
