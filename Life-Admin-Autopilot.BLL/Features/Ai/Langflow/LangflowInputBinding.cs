using System.Globalization;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
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
///   <item><term><c>Ai:Langflow:Fields:Mode</c></term><description><c>mode</c></description></item>
///   <item>
///     <term><c>Ai:Langflow:Tweaks:&lt;node&gt;:&lt;field&gt;</c></term>
///     <description>Any additional STATIC tweak, verbatim. The escape hatch for a
///     per-flow constant, and it needs no code at all.</description>
///   </item>
/// </list>
///
/// <para>
/// <b>Setting a static tweak from a shell needs care.</b> Langflow node ids contain a
/// hyphen, so the double-underscore env form —
/// <c>Ai__Langflow__Tweaks__PlanningInput-v4__mode</c> — is not a valid shell
/// identifier and <c>export</c> rejects it. Use <c>env 'NAME=value' dotnet …</c>,
/// appsettings, or <c>--Ai:Langflow:Tweaks:PlanningInput-v4:mode=…</c> on the command
/// line. Nothing is broken; the shell simply cannot name the variable.
/// </para>
///
/// <para>
/// <b>Not modelled: the two clarification-mode inputs.</b> The flow also reads
/// <c>pendingTasks</c> and <c>answers</c>, but ONLY when <c>mode=clarification</c> —
/// a surface this adapter does not own, since <c>/ai/ask</c> is the chat route. The
/// shapes are recorded here so the follow-up does not have to rediscover them; both
/// are JSON-stringified arrays (measured against the live flow by n-flowfix):
/// <c>pendingTasks</c> is
/// <c>[{id,title,domain,kind,priority,dueAt,question}]</c> carrying each task's REAL
/// id, and <c>answers</c> is <c>[{taskId,question,answer}]</c>. Wiring them means
/// assembling open clarifications for the user, which is the clarifications slice's
/// data, not this one's.
/// </para>
/// </summary>
public sealed class LangflowInputBinding
{
    public const string DefaultTranscriptField = "transcript";
    public const string DefaultAccessTokenField = "accessToken";
    public const string DefaultCurrentDateField = "currentDate";
    public const string DefaultModeField = "mode";

    /// <summary>
    /// The only mode this adapter produces. <c>/ai/ask</c> IS the chat surface; the
    /// flow's other three modes (<c>transcript</c>, <c>document</c>,
    /// <c>clarification</c>) belong to routes this file knows nothing about.
    /// </summary>
    public const string ChatMode = "chat";

    /// <summary>Unset means "this flow has a ChatInput" — the whole binding is skipped.</summary>
    public string? InputNode { get; init; }

    public string TranscriptField { get; init; } = DefaultTranscriptField;

    public string AccessTokenField { get; init; } = DefaultAccessTokenField;

    public string CurrentDateField { get; init; } = DefaultCurrentDateField;

    public string ModeField { get; init; } = DefaultModeField;

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
        ModeField = Read(configuration, "LANGFLOW_FIELD_MODE", "Ai:Langflow:Fields:Mode")
                    ?? DefaultModeField,
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
        DateTimeOffset now,
        string? timezone = null,
        string mode = ChatMode)
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
            target[CurrentDateField] = CurrentDate(now, timezone);

            if (!string.IsNullOrEmpty(accessToken))
            {
                target[AccessTokenField] = accessToken;
            }

            // Mode is per-turn, but an explicit static tweak still wins: it is the
            // operator's pin, and this adapter only ever produces `chat` anyway.
            if (!target.ContainsKey(ModeField))
            {
                target[ModeField] = mode;
            }
        }

        return request with { Tweaks = tweaks };
    }

    /// <summary>
    /// <c>currentDate</c>, as ISO-8601 <b>carrying the user's UTC offset</b> —
    /// <c>2026-08-10T14:23:00+03:00</c>.
    ///
    /// <para>
    /// <b>The offset is the whole point, and omitting it fails silently.</b> A bare
    /// <c>yyyy-MM-dd</c> is the v3 format the flow was redesigned to stop accepting
    /// (PLANNING-AGENT.md §6): the agent does not reject it, it just invents
    /// <c>+00:00</c>. Measured on the live stack with <c>Africa/Cairo</c> (+03:00),
    /// that put every <c>dueAt</c> three hours early — a reminder for 12:00 local
    /// fired at 09:00 — with nothing anywhere reporting an error.
    /// </para>
    ///
    /// <para>
    /// An absent or unrecognised zone falls back to the instant as given rather than
    /// throwing. <c>timezone</c> is optional on the request and a turn is worth more
    /// than a perfect offset; validity uses the kernel's own predicate so this agrees
    /// with every other surface about which ids Node accepts.
    /// </para>
    /// </summary>
    private static string CurrentDate(DateTimeOffset now, string? timezone)
    {
        var local = now;

        if (ImportedTimeResolver.IsValidTimeZone(timezone))
        {
            local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timezone!));
        }

        return local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
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
