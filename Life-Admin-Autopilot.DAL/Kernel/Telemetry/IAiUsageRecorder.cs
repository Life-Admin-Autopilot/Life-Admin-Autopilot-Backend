using MongoDB.Bson;

namespace Life_Admin_Autopilot.DAL.Kernel.Telemetry;

/// <summary>One model call, as the call site knows it. Pricing happens downstream.</summary>
/// <param name="UserId">Who to bill it to.</param>
/// <param name="Feature">One of <see cref="AiUsageFeature"/>.</param>
/// <param name="Provider">Who we paid — <c>langflow</c>, <c>gemini</c>, the ASR vendor.</param>
/// <param name="Model">
/// The model id, when the provider reports one. Null is expected on the Langflow
/// path and falls back to the configured default chat model.
/// </param>
public readonly record struct AiUsageRecord(
    ObjectId UserId,
    string Feature,
    string Provider,
    string? Model,
    int InputTokens,
    int OutputTokens,
    long LatencyMs)
{
    public string Outcome { get; init; } = AiUsageOutcome.Ok;

    public string? ErrorCode { get; init; }

    /// <summary>Conversation or scan id, so one expensive turn can be traced back.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// The seam every cost figure enters the system through.
///
/// <para>
/// <b>Implementations must never throw.</b> Telemetry is not worth failing a turn
/// the user has already been charged a quota slot for, and a Mongo hiccup during a
/// write here would otherwise surface as a 500 on an answer the user already read.
/// </para>
///
/// <para>
/// <b>The contract lives in the DAL, the implementation in the BLL.</b> The real
/// recorder needs the price table, which is BLL configuration; but the kernel has to
/// be able to register the no-op default, and the DAL cannot reference upward. So
/// the interface and <see cref="NullAiUsageRecorder"/> sit here and
/// <c>AiUsageRecorder</c> sits there.
/// </para>
/// </summary>
public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default registration, and what every unit test gets for free.
///
/// <para>
/// Registered unconditionally by <c>AddKernelData()</c> so <c>LangflowAiProvider</c>
/// always resolves a recorder, and replaced with the real one only where the admin
/// slice is present. That ordering means turning telemetry off is a registration
/// change, not a null check scattered through the hot path.
/// </para>
/// </summary>
public sealed class NullAiUsageRecorder : IAiUsageRecorder
{
    public Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
