using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot_Backend.Kernel.RateLimiting;

/// <summary>
/// The eight limiter names from <c>server/src/middleware/rateLimit.ts</c>. Use the
/// constant, never a literal — three of these are SHARED buckets and a typo
/// silently creates a private one.
/// </summary>
public static class KernelRateLimiters
{
    // --- express-rate-limit, fixed window, keyed on the socket IP -----------

    /// <summary>15 min / 20. The general auth endpoints.</summary>
    public const string Auth = "authLimiter";

    /// <summary>60 min / 5. Code sends and other expensive auth actions.</summary>
    public const string StrictAuth = "strictAuthLimiter";

    // --- hand-rolled sliding window, keyed on the authed user then the IP ---

    /// <summary>1 min / 30 — <c>POST /ai/ask</c>.</summary>
    public const string AiAsk = "aiAskLimiter";

    /// <summary>1 min / 30 — matters NL search (one Gemini round per submitted query).</summary>
    public const string TaskSearch = "taskSearchLimiter";

    /// <summary>
    /// 1 min / 10. <b>SHARED bucket</b> across every summary route — the instance,
    /// and therefore the counter, is one object in Node.
    /// </summary>
    public const string TaskSummary = "taskSummaryLimiter";

    /// <summary>1 min / 30 — <c>POST /ai/tools/confirm</c>, the same logical turn.</summary>
    public const string AiConfirm = "aiConfirmLimiter";

    /// <summary>1 min / 12. <b>SHARED bucket</b> across voice upload and chat transcribe.</summary>
    public const string AiVoice = "aiVoiceLimiter";

    /// <summary>
    /// 1 min / 6. <b>SHARED bucket</b> across every document-scan route. Lower than
    /// voice because scans are multi-page vision calls; the monthly quota guards
    /// sustained cost, this is only the burst guard.
    /// </summary>
    public const string DocumentScan = "documentScanLimiter";
}

public sealed class KernelRateLimitOptions
{
    public const string SectionName = "Kernel:RateLimit";

    /// <summary>
    /// Node skips every limiter when <c>NODE_ENV === 'test'</c> so suites can hit an
    /// endpoint repeatedly. The test fixture sets this false for the same reason.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public interface IKernelRateLimiter
{
    string Name { get; }

    /// <summary>
    /// Records a hit. Throws <see cref="AppException"/> with status 429 when the
    /// bucket is full, after setting the headers the Node limiter sets.
    /// </summary>
    void Apply(HttpContext context);
}

/// <summary>
/// Resolves a limiter by name. Registered as a SINGLETON, which is what makes the
/// three shared buckets actually shared: every route that names
/// <c>taskSummaryLimiter</c> gets the same counter instance, exactly as Node's
/// module-level <c>const</c> does.
/// </summary>
public sealed class KernelRateLimiterRegistry
{
    private readonly IReadOnlyDictionary<string, IKernelRateLimiter> _limiters;

    public KernelRateLimiterRegistry(IEnumerable<IKernelRateLimiter> limiters)
    {
        _limiters = limiters.ToDictionary(l => l.Name, StringComparer.Ordinal);
    }

    public IKernelRateLimiter Get(string name) =>
        _limiters.TryGetValue(name, out var limiter)
            ? limiter
            : throw new InvalidOperationException(
                $"Unknown rate limiter '{name}'. Use a constant from {nameof(KernelRateLimiters)}.");
}

/// <summary>Shared key derivation. <b>Trust proxy is OFF</b>, so forwarded headers are ignored.</summary>
internal static class RateLimitKeys
{
    /// <summary>
    /// The RAW socket IP. Express with <c>trust proxy</c> disabled returns
    /// <c>req.socket.remoteAddress</c> and ignores <c>X-Forwarded-For</c> entirely —
    /// reading the forwarded header here would let any client spoof its way past
    /// every limiter.
    /// </summary>
    public static string SocketIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// The hand-rolled limiter's key: the authenticated user id, falling back to the
    /// socket IP. It runs AFTER authentication for exactly this reason.
    /// </summary>
    public static string UserThenIp(HttpContext context) =>
        context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? SocketIp(context);
}

/// <summary>
/// Clone of <c>express-rate-limit</c> v7 as configured for the two auth limiters:
/// fixed window, <c>standardHeaders: true</c>, <c>legacyHeaders: false</c>.
///
/// <para>Header set verified live — <c>RateLimit-Policy</c>, <c>RateLimit-Limit</c>,
/// <c>RateLimit-Remaining</c> and <c>RateLimit-Reset</c> on EVERY response, plus
/// <c>Retry-After</c> and <c>X-Content-Type-Options: nosniff</c> on the 429.</para>
/// </summary>
public sealed class FixedWindowRateLimiter : IKernelRateLimiter
{
    private sealed class Window
    {
        public int Count;
        public DateTimeOffset ResetAt;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeSpan _windowLength;
    private readonly int _max;
    private readonly string _message;
    private readonly Func<bool> _enabled;

    public FixedWindowRateLimiter(string name, TimeSpan windowLength, int max, string message, Func<bool> enabled)
    {
        Name = name;
        _windowLength = windowLength;
        _max = max;
        _message = message;
        _enabled = enabled;
    }

    public string Name { get; }

    public void Apply(HttpContext context)
    {
        if (!_enabled())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var key = RateLimitKeys.SocketIp(context);

        var window = _windows.AddOrUpdate(
            key,
            _ => new Window { Count = 1, ResetAt = now + _windowLength },
            (_, existing) =>
            {
                lock (existing)
                {
                    if (existing.ResetAt <= now)
                    {
                        existing.Count = 1;
                        existing.ResetAt = now + _windowLength;
                    }
                    else
                    {
                        existing.Count += 1;
                    }
                }

                return existing;
            });

        int count;
        DateTimeOffset resetAt;
        lock (window)
        {
            count = window.Count;
            resetAt = window.ResetAt;
        }

        Sweep(now);

        var windowSeconds = (int)_windowLength.TotalSeconds;
        var resetSeconds = Math.Max(0, (int)Math.Ceiling((resetAt - now).TotalSeconds));
        var headers = context.Response.Headers;

        headers["RateLimit-Policy"] = $"{_max};w={windowSeconds}";
        headers["RateLimit-Limit"] = _max.ToString(CultureInfo.InvariantCulture);
        headers["RateLimit-Remaining"] = Math.Max(0, _max - count).ToString(CultureInfo.InvariantCulture);
        headers["RateLimit-Reset"] = resetSeconds.ToString(CultureInfo.InvariantCulture);

        if (count <= _max)
        {
            return;
        }

        headers["Retry-After"] = resetSeconds.ToString(CultureInfo.InvariantCulture);
        headers["X-Content-Type-Options"] = "nosniff";
        throw new AppException(429, "rate_limited", _message);
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
        {
            if (window.ResetAt <= now)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>
/// Clone of the hand-rolled per-user limiter in <c>rateLimit.ts</c>.
///
/// <para>
/// A TRUE sliding window: it records each hit's timestamp and only counts hits
/// inside the trailing window, so a burst at a window boundary cannot sneak
/// through the way a fixed-window counter allows. In-memory, single-process — a
/// stale-entry sweep on each hit keeps the map bounded and idle keys fall out on
/// their own.
/// </para>
///
/// <para>On rejection it sets only <c>Retry-After</c> (no <c>RateLimit-*</c>
/// headers) and throws the shared 429 message.</para>
/// </summary>
public sealed class SlidingWindowRateLimiter : IKernelRateLimiter
{
    /// <summary>The one message every hand-rolled limiter emits. Copied verbatim, em dash included.</summary>
    public const string Message = "You are going a little fast — give it a moment and try again.";

    private readonly ConcurrentDictionary<string, List<long>> _hits = new(StringComparer.Ordinal);
    private readonly TimeSpan _windowLength;
    private readonly int _max;
    private readonly Func<bool> _enabled;

    public SlidingWindowRateLimiter(string name, TimeSpan windowLength, int max, Func<bool> enabled)
    {
        Name = name;
        _windowLength = windowLength;
        _max = max;
        _enabled = enabled;
    }

    public string Name { get; }

    public void Apply(HttpContext context)
    {
        if (!_enabled())
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = (long)_windowLength.TotalMilliseconds;
        var cutoff = nowMs - windowMs;
        var key = RateLimitKeys.UserThenIp(context);

        var bucket = _hits.GetOrAdd(key, _ => new List<long>());

        long oldest;
        int count;
        lock (bucket)
        {
            bucket.RemoveAll(t => t <= cutoff);
            count = bucket.Count;
            oldest = count > 0 ? bucket[0] : nowMs;

            if (count < _max)
            {
                bucket.Add(nowMs);
            }
        }

        SweepIdleKeys(cutoff);

        if (count < _max)
        {
            return;
        }

        var retryAfterSec = Math.Max(1, (int)Math.Ceiling((oldest + windowMs - nowMs) / 1000.0));
        context.Response.Headers["Retry-After"] = retryAfterSec.ToString(CultureInfo.InvariantCulture);
        throw new AppException(429, "rate_limited", Message);
    }

    private void SweepIdleKeys(long cutoff)
    {
        foreach (var (key, timestamps) in _hits)
        {
            lock (timestamps)
            {
                var newest = timestamps.Count > 0 ? timestamps[^1] : 0;
                if (newest <= cutoff)
                {
                    _hits.TryRemove(key, out _);
                }
            }
        }
    }
}
