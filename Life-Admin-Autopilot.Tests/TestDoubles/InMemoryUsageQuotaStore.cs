using System.Collections.Concurrent;
using Life_Admin_Autopilot.DAL.Kernel.Quota;

namespace Life_Admin_Autopilot.Tests.TestDoubles;

/// <summary>
/// The kernel's one quota primitive, in memory.
///
/// <para>
/// It exists so an endpoint test about the SSE contract does not need the parity
/// Mongo running. It reproduces the ONE property those tests depend on — reserve on
/// admit, hand back on release, refuse at the ceiling — and nothing else; the real
/// atomicity is <c>MongoUsageQuotaStore</c>'s job and is covered by
/// <c>UsageQuotaTests</c>.
/// </para>
/// </summary>
public sealed class InMemoryUsageQuotaStore : IUsageQuotaStore
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Every admission that was refused, so a test can assert the gate ran.</summary>
    public int Denials { get; private set; }

    public Task<UsageQuotaAdmission> TryAdmitAsync(
        UsageQuotaBucket bucket,
        CancellationToken cancellationToken = default)
    {
        var key = KeyOf(bucket);
        var used = _counts.GetValueOrDefault(key);

        if (used >= bucket.Limit)
        {
            Denials++;
            return Task.FromResult(UsageQuotaAdmission.Deny(used, bucket.Limit));
        }

        _counts[key] = used + 1;
        return Task.FromResult(UsageQuotaAdmission.Allow(bucket.Limit));
    }

    public Task<int> ReadUsedAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default) =>
        Task.FromResult(_counts.GetValueOrDefault(KeyOf(bucket)));

    public Task RecordAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default)
    {
        _counts.AddOrUpdate(KeyOf(bucket), 1, (_, used) => used + 1);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(UsageQuotaBucket bucket, CancellationToken cancellationToken = default)
    {
        // Guarded so the counter never goes negative, like the Mongo one.
        _counts.AddOrUpdate(KeyOf(bucket), 0, (_, used) => Math.Max(0, used - 1));
        return Task.CompletedTask;
    }

    public int UsedFor(UsageQuotaBucket bucket) => _counts.GetValueOrDefault(KeyOf(bucket));

    private static string KeyOf(UsageQuotaBucket bucket) =>
        $"{bucket.Collection}|{bucket.UserId}|{string.Join(",", bucket.Keys.Select(k => $"{k.Key}={k.Value}"))}";
}
