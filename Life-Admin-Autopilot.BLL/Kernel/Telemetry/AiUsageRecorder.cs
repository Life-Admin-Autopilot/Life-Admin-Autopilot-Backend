using Life_Admin_Autopilot.DAL.Kernel.Activity;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Kernel.Telemetry;

/// <summary>
/// The recorder that actually writes.
///
/// <para>
/// The contract and the no-op live in <c>DAL.Kernel.Telemetry</c> — see
/// <see cref="IAiUsageRecorder"/> for why they are split. This half is here because
/// it needs <see cref="ModelPricing"/>, which is BLL configuration.
/// </para>
/// </summary>
public sealed class AiUsageRecorder : IAiUsageRecorder
{
    private readonly IAiUsageStore _store;
    private readonly ModelPricing _pricing;
    private readonly TimeProvider _time;
    private readonly ILogger<AiUsageRecorder> _logger;
    private readonly IAdminActivityBus _activity;

    public AiUsageRecorder(
        IAiUsageStore store,
        ModelPricing pricing,
        TimeProvider time,
        ILogger<AiUsageRecorder> logger,
        IAdminActivityBus? activity = null)
    {
        _store = store;
        _pricing = pricing;
        _time = time;
        _logger = logger;
        _activity = activity ?? new AdminActivityBus();
    }

    public async Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;

            // Langflow reports tokens but not the model, so chat falls back to the
            // configured default. Unset ⇒ no price ⇒ Priced = false, and the console
            // says so rather than quietly counting the turn as free.
            var model = record.Model
                ?? (record.Feature == AiUsageFeature.Chat ? _pricing.DefaultChatModel : null);

            var cost = _pricing.Estimate(model, record.InputTokens, record.OutputTokens);

            var document = new AiUsageEventDocument
            {
                UserId = record.UserId,
                At = now,

                // The same UTC bucket keys the quota primitive uses, so a usage row and
                // a quota row for one call always agree about which day they landed in.
                Day = UsageQuotaBuckets.UtcDate(now),
                Month = UsageQuotaBuckets.UtcMonth(now),

                Feature = record.Feature,
                Provider = record.Provider,
                Model = model,
                InputTokens = Math.Max(0, record.InputTokens),
                OutputTokens = Math.Max(0, record.OutputTokens),
                TotalTokens = Math.Max(0, record.InputTokens) + Math.Max(0, record.OutputTokens),
                EstimatedCostUsd = cost.Usd,
                Priced = cost.Priced,
                LatencyMs = Math.Max(0, record.LatencyMs),
                Outcome = record.Outcome,
                ErrorCode = record.ErrorCode,
                CorrelationId = record.CorrelationId,
                ExpiresAt = now.Add(AiUsageIndexes.EventRetention),
            };

            await _store.RecordAsync(document, cancellationToken).ConfigureAwait(false);

            var failed = record.Outcome == AiUsageOutcome.Error;

            _activity.Publish(
                failed ? AdminActivityKind.AiError : AdminActivityKind.AiTurn,
                failed
                    ? $"{record.Feature} failed — {record.ErrorCode ?? "no cause reported"}"
                    : $"{record.Feature} · {document.TotalTokens:N0} tokens",
                failed ? AdminActivitySeverity.Warning : AdminActivitySeverity.Info,
                detail: cost.Priced ? $"${cost.Usd:0.0000}" : "unpriced",
                userId: record.UserId.ToString());
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, and deliberately logged loudly enough to notice.
            // A dashboard with a hole in it is a much smaller problem than a turn that
            // 500s after the model already answered.
            _logger.LogError(
                ex,
                "ai:usage-record-failed feature={Feature} provider={Provider} user={User}",
                record.Feature,
                record.Provider,
                record.UserId);
        }
    }
}
