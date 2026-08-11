using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// Everything the adapter needs to reach a Langflow server, and <b>nothing is a
/// literal in the provider</b>. Host, flow and key differ per environment; the
/// timeout differs per model.
///
/// <para><b>Keys</b>, env var first then the configuration section, the same
/// two-name pattern the rest of the AI slice uses (see <c>AiQuotaService</c>):</para>
///
/// <list type="table">
///   <listheader><term>env</term><description>section — default</description></listheader>
///   <item><term><c>LANGFLOW_BASE_URL</c></term><description><c>Ai:Langflow:BaseUrl</c> — none</description></item>
///   <item><term><c>LANGFLOW_FLOW_ID</c></term><description><c>Ai:Langflow:FlowId</c> — none</description></item>
///   <item><term><c>LANGFLOW_API_KEY</c></term><description><c>Ai:Langflow:ApiKey</c> — none, optional</description></item>
///   <item><term><c>LANGFLOW_TIMEOUT_SECONDS</c></term><description><c>Ai:Langflow:TimeoutSeconds</c> — 120</description></item>
/// </list>
///
/// <para>
/// <b><see cref="IsConfigured"/> is the switch that picks the provider.</b> Base URL
/// and flow id must both be present and the URL absolute; the API key is optional
/// because a Langflow started with <c>LANGFLOW_AUTO_LOGIN=true</c> needs none. When
/// it is false the container keeps <c>NotConfiguredAiProvider</c>, which is what
/// holds the parity target — "behaves exactly like the reference with no AI key".
/// </para>
/// </summary>
public sealed class LangflowOptions
{
    public const string HttpClientName = "langflow";

    public const int DefaultTimeoutSeconds = 120;

    public string? BaseUrl { get; init; }

    public string? FlowId { get; init; }

    public string? ApiKey { get; init; }

    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>Both halves of an address, or the adapter is not wired at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(FlowId)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out _);

    /// <summary>
    /// <c>POST /api/v1/run/{flowId}?stream=true</c> — the STREAMING run endpoint.
    ///
    /// <para>
    /// The non-streaming form of the same route returns one JSON blob when the whole
    /// turn is finished. Using it would make the chat UI sit blank for the length of
    /// a multi-tool plan and then paint everything at once, which is the UX
    /// regression this adapter exists to avoid. <c>stream=true</c> is not optional.
    /// </para>
    /// </summary>
    public Uri RunUri => new(new Uri(BaseUrl!.TrimEnd('/') + "/"), $"api/v1/run/{FlowId}?stream=true");

    /// <summary>
    /// <c>DELETE /api/v1/monitor/messages/session/{sessionId}</c> — the messages
    /// Langflow itself stores for one session, which is the memory the Agent
    /// component replays (its <c>n_messages</c> window reads exactly this table).
    ///
    /// <para>
    /// Used only by the reset path. The session id is ours and contains nothing but
    /// hex and a colon, but it goes through <see cref="Uri.EscapeDataString"/> anyway
    /// — a path segment assembled from a value is a path segment worth escaping,
    /// whatever today's value happens to look like.
    /// </para>
    /// </summary>
    public Uri SessionMessagesUri(string sessionId) =>
        new(
            new Uri(BaseUrl!.TrimEnd('/') + "/"),
            $"api/v1/monitor/messages/session/{Uri.EscapeDataString(sessionId)}");

    public static LangflowOptions FromConfiguration(IConfiguration configuration) => new()
    {
        BaseUrl = Read(configuration, "LANGFLOW_BASE_URL", "Ai:Langflow:BaseUrl"),
        FlowId = Read(configuration, "LANGFLOW_FLOW_ID", "Ai:Langflow:FlowId"),
        ApiKey = Read(configuration, "LANGFLOW_API_KEY", "Ai:Langflow:ApiKey"),
        TimeoutSeconds = PositiveInt(
            configuration,
            "LANGFLOW_TIMEOUT_SECONDS",
            "Ai:Langflow:TimeoutSeconds",
            DefaultTimeoutSeconds),
    };

    private static string? Read(IConfiguration configuration, string envKey, string sectionKey)
    {
        var value = configuration[envKey] ?? configuration[sectionKey];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>A missing or unparseable value falls back rather than failing startup.</summary>
    private static int PositiveInt(IConfiguration configuration, string envKey, string sectionKey, int fallback)
    {
        var raw = Read(configuration, envKey, sectionKey);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
