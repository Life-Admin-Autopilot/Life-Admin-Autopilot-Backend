using MongoDB.Bson;

namespace Life_Admin_Autopilot.DAL.Kernel.Quota;

/// <summary>
/// Identifies ONE counter row and the ceiling that applies to it.
///
/// Node clones the same counter three times — <c>modules/ai/quota.ts</c>
/// (daily, keyed by <c>kind</c>), <c>lib/documentScanQuota.ts</c> (monthly, no
/// discriminator) and <c>modules/tasks/translateQuota.ts</c> (monthly, keyed by
/// <c>locale</c>). The only thing that varies is the collection, the bucket keys
/// and the limit, so all three are expressed as one of these.
/// </summary>
/// <param name="Collection">Mongo collection holding the counter rows.</param>
/// <param name="UserId">Owner. Always part of the unique key.</param>
/// <param name="Keys">
/// The remaining unique-key fields IN INDEX ORDER, e.g.
/// <c>{ ["date"] = "2026-08-09", ["kind"] = "message" }</c> or
/// <c>{ ["month"] = "2026-08", ["locale"] = "ar" }</c>. These are equality
/// fields, so Mongo seeds them onto the upserted document.
/// </param>
/// <param name="Limit">Ceiling. Admission requires <c>count &lt; Limit</c>.</param>
public readonly record struct UsageQuotaBucket(
    string Collection,
    ObjectId UserId,
    IReadOnlyDictionary<string, string> Keys,
    int Limit);

/// <summary>
/// Outcome of <see cref="IUsageQuotaStore.TryAdmitAsync"/>.
///
/// <b>Deliberately not an exception.</b> The two 402 payloads differ by domain —
/// document scans send <c>{tier,limit,used}</c>, AI sends
/// <c>{kind,tier,limit,used,resetAt}</c>, translate sends
/// <c>{locale,limit,used,resetAt}</c> — so the caller builds the
/// <c>AppException</c>, not the store.
/// </summary>
public readonly record struct UsageQuotaAdmission(bool Admitted, int Used, int Limit)
{
    public static UsageQuotaAdmission Allow(int limit) => new(true, 0, limit);

    public static UsageQuotaAdmission Deny(int used, int limit) => new(false, used, limit);
}

/// <summary>
/// The single quota primitive. Build the 402 in the caller.
///
/// <para><b>The reserve/release contract:</b> the slot is consumed by
/// <see cref="TryAdmitAsync"/>, BEFORE the expensive work runs. A caller whose
/// work fails before producing a result MUST call <see cref="ReleaseAsync"/>
/// exactly once, or a crashed turn permanently burns a slot. Never release on
/// undo — that turns do → undo → do into an unlimited loop around the cap.</para>
/// </summary>
public interface IUsageQuotaStore
{
    /// <summary>
    /// Atomic admission. One conditional <c>UpdateOne</c> closes the
    /// check-then-increment TOCTOU race.
    /// </summary>
    Task<UsageQuotaAdmission> TryAdmitAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default);

    /// <summary>Current count, or 0 when no row exists. For read-only meters.</summary>
    Task<int> ReadUsedAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unconditional <c>$inc</c> with upsert — count something that is NOT subject
    /// to the gate (Node's <c>recordUsage</c>, used for the post-confirmation
    /// continuation of a turn the user already paid for).
    /// </summary>
    Task RecordAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default);

    /// <summary>Hand a reserved slot back. Guarded so the counter never goes negative.</summary>
    Task ReleaseAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default);
}

/// <summary>UTC bucket keys and reset instants, ported verbatim from the three Node quotas.</summary>
public static class UsageQuotaBuckets
{
    /// <summary><c>YYYY-MM-DD</c>, UTC. Node: <c>date.toISOString().slice(0,10)</c>.</summary>
    public static string UtcDate(DateTime? at = null) =>
        (at ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd");

    /// <summary><c>YYYY-MM</c>, UTC. Node: <c>date.toISOString().slice(0,7)</c>.</summary>
    public static string UtcMonth(DateTime? at = null) =>
        (at ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM");

    /// <summary>
    /// Next UTC midnight as an ISO-8601 string with milliseconds and a <c>Z</c>
    /// suffix — the exact format <c>Date#toISOString()</c> produces, because it
    /// ships inside the 402 body.
    /// </summary>
    public static string NextUtcMidnightIso(DateTime? now = null)
    {
        var n = (now ?? DateTime.UtcNow).ToUniversalTime();
        var next = new DateTime(n.Year, n.Month, n.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
        return ToIsoString(next);
    }

    /// <summary>
    /// Midnight UTC on the 1st of the month AFTER <paramref name="monthBucket"/>
    /// (<c>YYYY-MM</c>). Node passes the 1-based month straight into the
    /// 0-indexed <c>Date.UTC</c>, which lands on the following month and rolls
    /// December into January — deliberate there, reproduced here.
    /// </summary>
    public static string NextMonthStartIso(string monthBucket)
    {
        var parts = monthBucket.Split('-');
        var year = parts.Length > 0 && int.TryParse(parts[0], out var y) ? y : 1970;
        var month = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 1;
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(month);
        return ToIsoString(start);
    }

    /// <summary>JavaScript <c>Date#toISOString()</c>: always 3 fractional digits, always <c>Z</c>.</summary>
    public static string ToIsoString(DateTime instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
