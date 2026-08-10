using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// Port of <c>isTransientGeminiError</c> plus the worker's backoff ladder from
/// <c>lib/documentScanWorker.ts</c>.
///
/// <para>
/// This classifier is why an unconfigured server takes ~20 s to settle a scan
/// rather than failing instantly: <c>ai_not_configured</c> is a 503, 503 reads as
/// transient, and the worker therefore burns the whole attempt ladder before
/// giving up. That is the reference behaviour, so it is reproduced rather than
/// short-circuited.
/// </para>
/// </summary>
public static class DocumentScanRetryPolicy
{
    private const int BackoffBaseMs = 2_000;
    private const int BackoffCapMs = 60_000;

    /// <summary>Multi-page vision calls run longer than a single voice extraction.</summary>
    public static readonly TimeSpan Lock = TimeSpan.FromMilliseconds(180_000);

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2_000);

    private static readonly int[] TransientStatuses = { 429, 500, 503, 504 };

    private static readonly Regex TransientMessage = new(
        "unavailable|overloaded|high demand|try again later|rate.?limit|internal error",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsTransient(Exception error) =>
        (error is AppException app && TransientStatuses.Contains(app.Status))
        || TransientMessage.IsMatch(error.Message);

    /// <summary>
    /// Exponential from the CURRENT attempt count, capped, plus up to a second of
    /// jitter so a fleet of workers recovering from the same outage does not
    /// stampede the provider in lockstep.
    /// </summary>
    public static TimeSpan Backoff(int attempts, Random random)
    {
        var exponent = Math.Max(0, attempts - 1);
        var scaled = exponent >= 31
            ? BackoffCapMs
            : (int)Math.Min(BackoffCapMs, (long)BackoffBaseMs << exponent);

        return TimeSpan.FromMilliseconds(scaled + random.Next(0, 1_000));
    }
}
