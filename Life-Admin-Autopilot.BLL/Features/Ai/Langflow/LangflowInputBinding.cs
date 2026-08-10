using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>Where the prompt goes.</b> The mirror image of
/// <see cref="LangflowWireContract"/>: that file is the one place that knows what
/// Langflow SENDS, this is the one place that knows what it EXPECTS.
///
/// <para><b>Why this exists.</b> <c>input_value</c> only reaches a flow that has a
/// ChatInput node. A flow built around a custom input component has no ChatInput at
/// all, and its only text entry point is a <c>tweaks</c> entry naming that component
/// and one of its fields. Posting <c>input_value</c> to such a flow is accepted,
/// streams a perfectly healthy <c>sources → token* → done</c> sequence, and hands the
/// agent an EMPTY prompt every single time — a failure with no error frame anywhere.
/// That was measured against a live Langflow 1.11.2 in the n-flowfix worktree; it
/// could not be reproduced from here, where nothing was listening on :7860.</para>
///
/// <para>
/// <b>Everything below is configuration, and the whole mechanism is off by
/// default.</b> With no <c>InputNode</c> set the request is exactly what it was —
/// <c>input_value</c> and nothing else — so the v3 baseline flow and any ordinary
/// ChatInput flow are unaffected. Set <c>InputNode</c> and the same prompt is
/// delivered as a tweak instead. Field names are settings rather than literals
/// precisely because a sibling is renaming them right now: re-pointing this is an
/// appsettings edit, not a code change.
/// </para>
///
/// <list type="table">
///   <listheader><term>key</term><description>meaning — default</description></listheader>
///   <item>
///     <term><c>LANGFLOW_INPUT_NODE</c> / <c>Ai:Langflow:InputNode</c></term>
///     <description>The component id the prompt is tweaked into, e.g.
///     <c>PlanningInput-v4</c>. Unset ⇒ plain <c>input_value</c>.</description>
///   </item>
///   <item><term><c>Ai:Langflow:Fields:Transcript</c></term><description><c>transcript</c></description></item>
///   <item><term><c>Ai:Langflow:Fields:AccessToken</c></term><description><c>accessToken</c></description></item>
///   <item><term><c>Ai:Langflow:Fields:CurrentDate</c></term><description><c>currentDate</c></description></item>
///   <item>
///     <term><c>Ai:Langflow:Tweaks:&lt;node&gt;:&lt;field&gt;</c></term>
///     <description>Any additional STATIC tweak, verbatim. This is the escape hatch
///     for a per-flow constant such as <c>mode</c>, and it needs no code at
///     all.</description>
///   </item>
/// </list>
///
/// <para>
/// <b>Not modelled: the per-turn dynamic tweaks.</b> A flow that wants
/// <c>pendingTasks</c> or prior <c>answers</c> needs state this adapter does not
/// assemble — those are conversation-shaped inputs, and inventing a shape for them
/// without a flow to test against would be guessing. They belong in a follow-up that
/// can measure the round trip.
/// </para>
/// </summary>
public sealed class LangflowInputBinding
{
    public const string DefaultTranscriptField = "transcript";
    public const string DefaultAccessTokenField = "accessToken";
    public const string DefaultCurrentDateField = "currentDate";

    /// <summary>Unset means "this flow has a ChatInput" — the whole binding is skipped.</summary>
    public string? InputNode { get; init; }

    public string TranscriptField { get; init; } = DefaultTranscriptField;

    public string AccessTokenField { get; init; } = DefaultAccessTokenField;

    public string CurrentDateField { get; init; } = DefaultCurrentDateField;

    /// <summary>Static, per-node, from configuration. Applied before the dynamic values.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StaticTweaks { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public bool IsBound => !string.IsNullOrWhiteSpace(InputNode);

    public static LangflowInputBinding FromConfiguration(IConfiguration configuration) => new()
    {
        InputNode = Read(configuration, "LANGFLOW_INPUT_NODE", "Ai:Langflow:InputNode"),
        TranscriptField = Read(configuration, "LANGFLOW_FIELD_TRANSCRIPT", "Ai:Langflow:Fields:Transcript")
                          ?? DefaultTranscriptField,
        AccessTokenField = Read(configuration, "LANGFLOW_FIELD_ACCESS_TOKEN", "Ai:Langflow:Fields:AccessToken")
                           ?? DefaultAccessTokenField,
        CurrentDateField = Read(configuration, "LANGFLOW_FIELD_CURRENT_DATE", "Ai:Langflow:Fields:CurrentDate")
                           ?? DefaultCurrentDateField,
        StaticTweaks = ReadStaticTweaks(configuration),
    };

    /// <summary>
    /// Build the run request for one turn.
    ///
    /// <para>
    /// <paramref name="accessToken"/> is the caller's own bearer token, forwarded so
    /// the flow's tools can call this API back AS that user rather than with ambient
    /// authority. It leaves the process only when a node is configured to receive it,
    /// and only to the configured Langflow host — it is never logged and never put in
    /// a query string.
    /// </para>
    /// </summary>
    public LangflowRunRequest BuildRequest(
        string prompt,
        string sessionId,
        string? accessToken,
        DateTimeOffset now)
    {
        var request = new LangflowRunRequest
        {
            // Still sent. A flow WITH a ChatInput needs it, and a flow without one
            // ignores it, so there is no case where omitting it is safer.
            InputValue = prompt,
            SessionId = sessionId,
        };

        if (!IsBound && StaticTweaks.Count == 0)
        {
            return request;
        }

        var tweaks = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var (node, fields) in StaticTweaks)
        {
            var target = Target(tweaks, node);
            foreach (var (field, value) in fields)
            {
                target[field] = value;
            }
        }

        if (IsBound)
        {
            var target = Target(tweaks, InputNode!);
            target[TranscriptField] = prompt;

            // ISO-8601 WITH the offset, per PLANNING-AGENT.md §6. A bare date is
            // the v3 mistake the redesign exists to correct: the agent cannot tell
            // what "tomorrow 9am" means without knowing which day it already is for
            // this user, and given no offset it invents one rather than failing.
            target[CurrentDateField] = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

            if (!string.IsNullOrEmpty(accessToken))
            {
                target[AccessTokenField] = accessToken;
            }
        }

        return request with { Tweaks = tweaks };
    }

    private static Dictionary<string, object?> Target(
        IDictionary<string, Dictionary<string, object?>> tweaks,
        string node)
    {
        if (!tweaks.TryGetValue(node, out var target))
        {
            target = new Dictionary<string, object?>(StringComparer.Ordinal);
            tweaks[node] = target;
        }

        return target;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadStaticTweaks(
        IConfiguration configuration)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var node in configuration.GetSection("Ai:Langflow:Tweaks").GetChildren())
        {
            var fields = node
                .GetChildren()
                .Where(field => field.Value is not null)
                .ToDictionary(field => field.Key, field => field.Value!, StringComparer.Ordinal);

            if (fields.Count > 0)
            {
                result[node.Key] = fields;
            }
        }

        return result;
    }

    private static string? Read(IConfiguration configuration, string envKey, string sectionKey)
    {
        var value = configuration[envKey] ?? configuration[sectionKey];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// The request body Langflow's run endpoint takes. snake_case — Langflow does not
/// camelCase its API, so every name is set explicitly rather than left to the
/// serializer's policy.
/// </summary>
public sealed record LangflowRunRequest
{
    [JsonPropertyName("input_value")]
    public string InputValue { get; init; } = string.Empty;

    [JsonPropertyName("input_type")]
    public string InputType { get; init; } = "chat";

    [JsonPropertyName("output_type")]
    public string OutputType { get; init; } = "chat";

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// Per-component overrides. Omitted entirely when empty — Langflow accepts an
    /// empty map, but sending one on every request makes the payload harder to read
    /// in a capture and says nothing.
    /// </summary>
    [JsonPropertyName("tweaks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, Dictionary<string, object?>>? Tweaks { get; init; }
}
