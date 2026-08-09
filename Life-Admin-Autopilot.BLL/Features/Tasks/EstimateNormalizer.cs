using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Port of the estimate ladder in <c>server/src/models/Task.ts</c>.
///
/// <para>
/// Buckets, not integers, are the whole point: a model that answers "23 minutes"
/// is claiming a precision it does not have, and the user reads that precision as
/// a promise. Every value that reaches the database is snapped onto the ladder,
/// including one the client typed — the UI offers buckets, but the server does
/// not take that on trust.
/// </para>
/// </summary>
public static class EstimateNormalizer
{
    public static readonly IReadOnlyList<int> Buckets = new[] { 5, 10, 15, 30, 45, 60, 90, 120, 180, 240 };

    /// <summary>
    /// Snap an arbitrary minute count onto the ladder. Ties round UP: people
    /// underestimate how long admin takes, so the generous side is the safer error.
    ///
    /// <para>
    /// The floor is handled up front rather than left to the distance comparison,
    /// matching Node — and the comparison uses <c>&lt;=</c> so that on an exact tie
    /// the LARGER bucket wins, because the scan runs in ascending order.
    /// </para>
    /// </summary>
    public static int Snap(double minutes)
    {
        var smallest = Buckets[0];
        if (double.IsNaN(minutes) || minutes <= smallest)
        {
            return smallest;
        }

        var best = smallest;
        foreach (var bucket in Buckets)
        {
            if (Math.Abs(bucket - minutes) <= Math.Abs(best - minutes))
            {
                best = bucket;
            }
        }

        return best;
    }

    /// <summary>
    /// The single hardening gate for every estimate entering the system. A caller
    /// that swapped the bounds gets a usable range rather than a rejection —
    /// sorting the surviving bounds is what enforces <c>max &gt;= min</c>.
    /// </summary>
    /// <param name="source">
    /// <c>"user"</c> is authoritative forever: no AI pass may overwrite it. That
    /// rule is enforced at each write site, not here.
    /// </param>
    public static TaskEstimateDocument? Normalize(double? minMinutes, double? maxMinutes, string source)
    {
        var bounds = new List<double>(2);
        if (minMinutes.HasValue && !double.IsNaN(minMinutes.Value) && !double.IsInfinity(minMinutes.Value))
        {
            bounds.Add(minMinutes.Value);
        }

        if (maxMinutes.HasValue && !double.IsNaN(maxMinutes.Value) && !double.IsInfinity(maxMinutes.Value))
        {
            bounds.Add(maxMinutes.Value);
        }

        // One bound alone is still an estimate — a point estimate. No bound is not.
        if (bounds.Count == 0)
        {
            return null;
        }

        return new TaskEstimateDocument
        {
            MinMinutes = Snap(bounds.Min()),
            MaxMinutes = Snap(bounds.Max()),
            Source = source,
        };
    }
}
