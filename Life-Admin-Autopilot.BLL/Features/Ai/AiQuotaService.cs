using System.Globalization;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>One meter, one kind. <c>GET /ai/quota</c> returns an array of these.</summary>
public sealed class AiQuotaStatusDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = AiQuotaService.MessageKind;

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("used")]
    public int Used { get; init; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; init; }

    /// <summary>
    /// Next UTC midnight, as a JS <c>toISOString()</c> string. Emitted as a raw
    /// string rather than a <c>DateTime</c> so it round-trips byte-identically
    /// whether it comes from here or from a 402 <c>details</c> body.
    /// </summary>
    [JsonPropertyName("resetAt")]
    public string ResetAt { get; init; } = string.Empty;
}

/// <summary>The <c>GET /ai/quota</c> envelope.</summary>
public sealed class AiQuotaResponse
{
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = AiQuotaService.FreeTier;

    [JsonPropertyName("quotas")]
    public IReadOnlyList<AiQuotaStatusDto> Quotas { get; init; } = Array.Empty<AiQuotaStatusDto>();
}

/// <summary>
/// Port of <c>server/src/modules/ai/quota.ts</c>, on top of the kernel's single
/// <c>IUsageQuotaStore</c> primitive (KERNEL.md §8.3) rather than a fourth clone of
/// the atomic-admission dance.
///
/// <para>
/// <b>Reserve/release, not check-then-increment.</b> <see cref="AdmitAsync"/>
/// consumes the slot BEFORE the expensive work; a caller whose work fails before
/// producing a result calls <see cref="ReleaseAsync"/> exactly once. Never release
/// on undo — that would make do → undo → do an unlimited loop around the cap.
/// </para>
/// </summary>
public sealed class AiQuotaService
{
    /// <summary><c>AI_USAGE_KINDS</c> currently has exactly one member.</summary>
    public const string MessageKind = "message";

    public const string FreeTier = "free";

    public const string ProTier = "pro";

    /// <summary><c>AI_QUOTA_FREE_DAILY</c> — <c>z.coerce.number().int().positive().default(30)</c>.</summary>
    public const int DefaultFreeDaily = 30;

    /// <summary><c>AI_QUOTA_PRO_DAILY</c> — same coercion, default 300.</summary>
    public const int DefaultProDaily = 300;

    public static readonly IReadOnlyList<string> UsageKinds = new[] { MessageKind };

    private readonly IUsageQuotaStore _store;
    private readonly int _freeDaily;
    private readonly int _proDaily;

    public AiQuotaService(IUsageQuotaStore store, IConfiguration configuration)
    {
        _store = store;
        _freeDaily = PositiveInt(configuration, "AI_QUOTA_FREE_DAILY", "Ai:QuotaFreeDaily", DefaultFreeDaily);
        _proDaily = PositiveInt(configuration, "AI_QUOTA_PRO_DAILY", "Ai:QuotaProDaily", DefaultProDaily);
    }

    /// <summary>
    /// Node's <c>resolveTier()</c>. <b>Hard-coded to <c>free</c> for every user</b> —
    /// v1 has no Pro path and the comment on the Node function says so explicitly.
    /// Reproduced as a constant rather than read from
    /// <c>user.subscription.tier</c>, because reading it would make Pro
    /// subscribers diverge the day someone sets that field.
    /// </summary>
    public static string ResolveTier() => FreeTier;

    /// <summary>Per-tier daily ceiling. Anything but <c>message</c> is zero, as in Node.</summary>
    public int LimitFor(string tier, string kind) =>
        kind != MessageKind ? 0 : tier == ProTier ? _proDaily : _freeDaily;

    /// <summary>
    /// <c>getQuotaStatus</c> — one entry per member of <c>AI_USAGE_KINDS</c>, whether
    /// or not a counter row exists.
    /// </summary>
    public async Task<IReadOnlyList<AiQuotaStatusDto>> GetStatusAsync(
        ObjectId userId,
        string tier,
        string? today = null,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var date = today ?? UsageQuotaBuckets.UtcDate(at);
        var resetAt = UsageQuotaBuckets.NextUtcMidnightIso(at);

        var statuses = new List<AiQuotaStatusDto>(UsageKinds.Count);
        foreach (var kind in UsageKinds)
        {
            var limit = LimitFor(tier, kind);
            var used = await _store.ReadUsedAsync(Bucket(userId, date, kind, limit), cancellationToken)
                .ConfigureAwait(false);

            statuses.Add(new AiQuotaStatusDto
            {
                Kind = kind,
                Limit = limit,
                Used = used,
                Remaining = Math.Max(0, limit - used),
                ResetAt = resetAt,
            });
        }

        return statuses;
    }

    /// <summary>
    /// <c>admitWithinQuota</c>. Throws the 402 on refusal.
    ///
    /// <para>
    /// <b>The details shape is AI's own:</b> <c>{kind, tier, limit, used, resetAt}</c>.
    /// The document-scan 402 sends <c>{tier, limit, used}</c> and translate sends
    /// <c>{locale, limit, used, resetAt}</c> — three different bodies under codes
    /// that are also different, which is exactly why the kernel store returns an
    /// admission instead of throwing.
    /// </para>
    /// </summary>
    public async Task AdmitAsync(
        ObjectId userId,
        string tier,
        string kind = MessageKind,
        string? today = null,
        DateTime? at = null,
        CancellationToken cancellationToken = default)
    {
        var date = today ?? UsageQuotaBuckets.UtcDate(at);
        var limit = LimitFor(tier, kind);

        var admission = await _store
            .TryAdmitAsync(Bucket(userId, date, kind, limit), cancellationToken)
            .ConfigureAwait(false);

        if (admission.Admitted)
        {
            return;
        }

        throw QuotaExceeded(tier, kind, admission.Limit, admission.Used, at);
    }

    /// <summary>
    /// Hands a reserved slot back. Call at most once per <see cref="AdmitAsync"/>,
    /// and only when the work produced no result.
    ///
    /// <para>
    /// The bucket's limit is filled in from the free tier and is <b>deliberately not
    /// a parameter</b>: only <c>TryAdmitAsync</c>'s guard filter reads
    /// <c>UsageQuotaBucket.Limit</c>; release and record are plain <c>$inc</c>s
    /// against the identity key. If the store ever starts consulting the limit on
    /// these two paths, this call and <see cref="RecordAsync"/> need the caller's
    /// real tier threaded through.
    /// </para>
    /// </summary>
    public Task ReleaseAsync(
        ObjectId userId,
        string kind = MessageKind,
        string? today = null,
        DateTime? at = null,
        CancellationToken cancellationToken = default) =>
        _store.ReleaseAsync(
            Bucket(userId, today ?? UsageQuotaBuckets.UtcDate(at), kind, LimitFor(FreeTier, kind)),
            cancellationToken);

    /// <summary>
    /// Node's <c>recordUsage</c> — the UNGATED increment. Used only for the
    /// post-confirmation continuation, which is the same logical turn the user
    /// already paid for: it is counted so the visible meter reflects the extra
    /// model round, but it is never refused.
    /// </summary>
    public Task RecordAsync(
        ObjectId userId,
        string kind = MessageKind,
        string? today = null,
        DateTime? at = null,
        CancellationToken cancellationToken = default) =>
        _store.RecordAsync(
            Bucket(userId, today ?? UsageQuotaBuckets.UtcDate(at), kind, LimitFor(FreeTier, kind)),
            cancellationToken);

    /// <summary>
    /// The 402. Message is <c>`You've hit today's limit of ${limit} ${kind}s.`</c> —
    /// note the pluralising <c>s</c> is appended to the KIND, so it reads
    /// "30 messages".
    /// </summary>
    public static AppException QuotaExceeded(string tier, string kind, int limit, int used, DateTime? at = null) =>
        AppException.PaymentRequired(
            "quota_exceeded",
            $"You've hit today's limit of {limit} {kind}s.",
            new AiQuotaExceededDetails
            {
                Kind = kind,
                Tier = tier,
                Limit = limit,
                Used = used,
                ResetAt = UsageQuotaBuckets.NextUtcMidnightIso(at),
            });

    private static UsageQuotaBucket Bucket(ObjectId userId, string date, string kind, int limit) =>
        new(
            MongoCollections.AiUsageCounters,
            userId,
            new Dictionary<string, string> { ["date"] = date, ["kind"] = kind },
            limit);

    /// <summary>
    /// <c>z.coerce.number().int().positive()</c> with a default: a missing or
    /// unparseable value falls back rather than failing the request.
    /// </summary>
    private static int PositiveInt(IConfiguration configuration, string envKey, string sectionKey, int fallback)
    {
        var raw = configuration[envKey] ?? configuration[sectionKey];

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}

/// <summary>
/// The AI 402's <c>details</c>. A DIFFERENT shape from the document-scan 402's
/// <c>{tier, limit, used}</c> — it adds <c>kind</c> and <c>resetAt</c>.
/// </summary>
public sealed class AiQuotaExceededDetails
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = AiQuotaService.MessageKind;

    [JsonPropertyName("tier")]
    public string Tier { get; init; } = AiQuotaService.FreeTier;

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("used")]
    public int Used { get; init; }

    [JsonPropertyName("resetAt")]
    public string ResetAt { get; init; } = string.Empty;
}
