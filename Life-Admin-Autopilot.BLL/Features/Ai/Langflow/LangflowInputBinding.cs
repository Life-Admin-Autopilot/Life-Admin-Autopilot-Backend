using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
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
/// <b>What a bound turn actually carries.</b> Not just the prompt: the flow is handed
/// the same grounding Node assembles for EVERY AI turn in
/// <c>modules/ai/contextBuilder.ts</c> — <c>currentDate</c> (offset-bearing now),
/// <c>dateReference</c> (the 14-day weekday table plus literal phrase anchors) and
/// <c>myTasks</c> (the user's existing open matters, capped). Sending only the
/// transcript left the model doing relative-date arithmetic unaided and blind to what
/// the user already had, so it guessed weekdays and created duplicates. See
/// <see cref="DateGrounding"/> and <see cref="TaskGrounding"/>.
/// </para>
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
///   <item><term><c>Ai:Langflow:Fields:CurrentDate</c></term><description><c>currentDate</c></description></item>
///   <item><term><c>Ai:Langflow:Fields:Mode</c></term><description><c>mode</c></description></item>
///   <item><term><c>Ai:Langflow:Fields:DateReference</c></term><description><c>dateReference</c></description></item>
///   <item><term><c>Ai:Langflow:Fields:MyTasks</c></term><description><c>myTasks</c></description></item>
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
///
/// <para>
/// <b>That slice will also need <c>mode</c> to become caller-supplied.</b>
/// <see cref="BuildRequest"/> defaults it to <see cref="ChatMode"/> because
/// <c>/ai/ask</c> is the chat surface and has no other mode to report; clarification
/// mode is only entered when <c>mode=clarification</c> arrives, so a route that knows
/// the user is answering questions has to pass it in. The parameter is already there
/// — nothing calls it with anything else yet.
/// </para>
/// </summary>
public sealed class LangflowInputBinding
{
    public const string DefaultTranscriptField = "transcript";
    public const string DefaultCurrentDateField = "currentDate";
    public const string DefaultModeField = "mode";
    public const string DefaultDateReferenceField = "dateReference";
    public const string DefaultMyTasksField = "myTasks";

    /// <summary>
    /// The only mode this adapter produces. <c>/ai/ask</c> IS the chat surface; the
    /// flow's other three modes (<c>transcript</c>, <c>document</c>,
    /// <c>clarification</c>) belong to routes this file knows nothing about.
    /// </summary>
    public const string ChatMode = "chat";

    /// <summary>
    /// The field every TOOL component reads its bearer from.
    ///
    /// <para>
    /// Snake_case, unlike the input node's fields, because it is not a prompt slot: it
    /// is the name of an input on the eleven tool components, fixed by their own source.
    /// Not configurable for the same reason — renaming it here could not rename it there.
    /// </para>
    /// </summary>
    public const string ToolAccessTokenField = "access_token";

    /// <summary>
    /// The caller's UTC offset, injected into <c>queryTasks</c> so its <c>due_on</c>
    /// filter can expand a bare <c>YYYY-MM-DD</c> into that whole LOCAL day.
    ///
    /// <para>
    /// <b>Server-injected rather than asked of the model</b>, for the same reason the
    /// access token is: the point of <c>due_on</c> is to take the timezone conversion
    /// off the agent, and sourcing the offset from the agent would hand it straight
    /// back. It is not in the tool's exposed arg schema.
    /// </para>
    /// </summary>
    public const string ToolUtcOffsetField = "utc_offset";

    /// <summary>
    /// The turn's mode as the TOOLS see it — the same value <see cref="ModeField"/>
    /// carries to the prompt, injected separately because a tool cannot read the
    /// prompt's inputs.
    ///
    /// <para>
    /// Server-injected, never a model argument: which surface the user is on is not
    /// something the agent should be able to claim. Only <c>createTask</c> declares
    /// an input by this name today; Langflow discards a tweak for a field a node
    /// does not have, so the other ten are unaffected.
    /// </para>
    /// </summary>
    public const string ToolModeField = "mode";

    /// <summary>
    /// The eleven tool components of <c>planning-agent.v4</c>, which each need the
    /// caller's bearer to call this API back as that user.
    ///
    /// <para>
    /// <b>Why the backend sends this and the agent no longer does.</b> Each of these
    /// fields used to be <c>tool_mode: true</c>, so the token was published into the
    /// model's context and copied back out as a tool argument on every call. A model
    /// that forgets to copy it gets <c>"access_token is empty"</c> and has to repair by
    /// re-issuing the whole batch — which happened in 4 separate turns and, on
    /// 2026-08-17, left a failed <c>holdForClarification</c> beside its successful
    /// retry and rendered as a phantom question card in the chat. Tweaking the field
    /// directly removes the failure mode rather than asking the model to stop having it,
    /// and keeps a live JWT out of the model's context entirely.
    /// </para>
    ///
    /// <para>
    /// Node ids are a contract with the flow. A tweak naming a node the flow does not
    /// have is ignored by Langflow, so a deployment pointed at a different flow degrades
    /// to "tools get no token from us" rather than erroring — which is why
    /// <c>PLANNING-AGENT.md</c> §6 lists these ids beside the tweak table.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ToolNodes = new[]
    {
        "CreateTaskTool-v4",
        "UpdateTaskTool-v4",
        "CompleteTaskTool-v4",
        "DeleteTaskTool-v4",
        "DeleteAllTasksTool-v4",
        "SnoozeTaskTool-v4",
        "QueryTasksTool-v4",
        "AddSubtaskTool-v4",
        "ToggleSubtaskTool-v4",
        "RemoveSubtaskTool-v4",
        "HoldForClarificationTool-v4",
    };

    /// <summary>Unset means "this flow has a ChatInput" — the whole binding is skipped.</summary>
    public string? InputNode { get; init; }

    public string TranscriptField { get; init; } = DefaultTranscriptField;

    public string CurrentDateField { get; init; } = DefaultCurrentDateField;

    public string ModeField { get; init; } = DefaultModeField;

    /// <summary>
    /// The 14-day weekday→date table and the phrase anchors. See
    /// <see cref="DateGrounding"/> for why the model is handed a lookup table instead
    /// of being trusted with weekday arithmetic.
    /// </summary>
    public string DateReferenceField { get; init; } = DefaultDateReferenceField;

    /// <summary>
    /// The user's existing open matters, capped. See <see cref="TaskGrounding"/>.
    /// </summary>
    public string MyTasksField { get; init; } = DefaultMyTasksField;

    /// <summary>Static, per-node, from configuration. Applied before the dynamic values.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StaticTweaks { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public bool IsBound => !string.IsNullOrWhiteSpace(InputNode);

    public static LangflowInputBinding FromConfiguration(IConfiguration configuration) => new()
    {
        InputNode = Read(configuration, "LANGFLOW_INPUT_NODE", "Ai:Langflow:InputNode"),
        TranscriptField = Read(configuration, "LANGFLOW_FIELD_TRANSCRIPT", "Ai:Langflow:Fields:Transcript")
                          ?? DefaultTranscriptField,
        CurrentDateField = Read(configuration, "LANGFLOW_FIELD_CURRENT_DATE", "Ai:Langflow:Fields:CurrentDate")
                           ?? DefaultCurrentDateField,
        ModeField = Read(configuration, "LANGFLOW_FIELD_MODE", "Ai:Langflow:Fields:Mode")
                    ?? DefaultModeField,
        DateReferenceField = Read(configuration, "LANGFLOW_FIELD_DATE_REFERENCE", "Ai:Langflow:Fields:DateReference")
                             ?? DefaultDateReferenceField,
        MyTasksField = Read(configuration, "LANGFLOW_FIELD_MY_TASKS", "Ai:Langflow:Fields:MyTasks")
                       ?? DefaultMyTasksField,
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
    ///
    /// <para>
    /// <paramref name="myTasks"/> is the rendered <c>MY TASKS</c> block. It arrives as
    /// a parameter rather than being built here because it needs a database read, and
    /// this type is deliberately pure. The date grounding beside it is NOT a parameter
    /// for the mirror-image reason: it is a total function of
    /// <paramref name="now"/> and <paramref name="timezone"/>, both of which are
    /// already here, so making the caller pass it would only create a second place to
    /// forget it.
    /// </para>
    /// </summary>
    public LangflowRunRequest BuildRequest(
        string prompt,
        string sessionId,
        string? accessToken,
        DateTimeOffset now,
        string? timezone = null,
        string mode = ChatMode,
        string? myTasks = null)
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
            target[CurrentDateField] = DateGrounding.FormatNow(now, timezone);
            target[DateReferenceField] = DateGrounding.BuildDateReference(now, timezone);

            // Always sent, never omitted. "(no open tasks)" is a fact the agent needs
            // to state; skipping the tweak instead falls back to the node's stored
            // empty string, which reads as a missing block rather than an empty one.
            target[MyTasksField] = myTasks ?? TaskGrounding.NoTasks;

            // The token goes to the TOOLS, not into the prompt. See ToolNodes: the
            // agent used to carry it and could drop it, and a dropped token is a failed
            // tool call the user sees. Sent on every turn and in every mode, because
            // every mode can call tools.
            //
            // An absent token stays absent rather than being sent as "": an empty tweak
            // would overwrite a value an operator had pinned on the node, and a token
            // this layer does not have is not one it can invent.
            //
            // <b>It is no longer silent, though.</b> Every tool in such a run answers
            // `{"ok": false, "error": "misconfigured"}`, and the agent was measured
            // reporting that to the user as "I don't see anything on your schedule" — a
            // dead lookup rendered as an empty calendar. Two things now stop that
            // reaching the user as fact: the caller logs the missing token
            // (<c>LangflowAiProvider.WarnIfToolsCannotAuthenticate</c>), and
            // <see cref="FailedLookupGuard"/> refuses to let an absence claim stand on
            // top of a failed read. Refusing the whole run here was tried and reverted —
            // it turns "hi" into a 500, and a greeting needs no tools.
            if (!string.IsNullOrEmpty(accessToken))
            {
                var utcOffset = DateGrounding.UtcOffset(now, timezone);

                foreach (var node in ToolNodes)
                {
                    var tool = Target(tweaks, node);
                    tool[ToolAccessTokenField] = accessToken;
                    tool[ToolUtcOffsetField] = utcOffset;

                    // The turn's mode, to the TOOLS as well as to the prompt.
                    //
                    // `createTask` asks the server to raise a question about any
                    // gap it can see in what was just filed, and that is a CHAT
                    // behaviour: a person is present and can answer. The same tool
                    // serves transcript and document runs, where nobody is there —
                    // a question raised then is a card nobody asked for, about a
                    // matter read out of a file.
                    //
                    // INSIDE the token block, with the rest. A tokenless run sends
                    // NO tool tweaks at all (LangflowProviderTests pins that: an
                    // empty tweak would overwrite a value an operator pinned on the
                    // node), and it costs nothing here — every tool in such a run
                    // answers "misconfigured" and writes nothing, so there is no
                    // matter for a mode to decide anything about.
                    tool[ToolModeField] = mode;
                }
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
