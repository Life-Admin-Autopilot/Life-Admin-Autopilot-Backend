namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// SEVEN of the EIGHT frame shapes the shipped chat UI parses, built in one place
/// so no caller hand-rolls a payload dictionary and gets a key wrong.
///
/// <para>
/// <b>The eighth is <c>conversation</c>, and it is not here on purpose.</b> It
/// reports which thread the turn was filed under, which is knowledge the ROUTE has
/// and a provider does not — so it is written by <c>AiStreamTurns</c> through
/// <c>AiSseWriter.SendRawAsync</c> rather than minted from this vocabulary. It is
/// the FIRST frame of every <c>/ai/ask</c> turn, ahead of <c>sources</c>, because a
/// client that sent no <c>conversationId</c> has no other way to learn where its
/// text belongs and must not start appending before it knows. Counting it is the
/// only reason this doc says eight: see
/// <c>docs/contract/paths.integrations.yaml</c> <c>AiStreamEvent</c>.
/// </para>
///
/// <para>
/// <b>Key order is part of the contract.</b> Node's <c>send()</c> does
/// <c>{ type: event.kind, ...event }</c> and then deletes <c>kind</c>, so
/// <c>type</c> is always the FIRST key and the rest follow in the generator's own
/// order. The writer emits <c>type</c> first and then walks the payload in
/// insertion order, which is why every factory below inserts in the reference
/// order and why a plain <see cref="Dictionary{TKey,TValue}"/> (insertion-ordered
/// as long as nothing is removed) is enough.
/// </para>
///
/// <para>
/// <b>The emission sequence</b> is
/// <c>conversation → sources → (tool_call → tool_result)* → token* → done → quota</c>.
/// The two ends belong to the ROUTE, never to a provider: <c>conversation</c> opens
/// the turn (it is resolved before the headers flush) and <c>quota</c> is synthesised
/// immediately after <c>done</c>, so it is always the last frame of a turn that
/// completed.
/// </para>
/// </summary>
public static class AiStreamEvents
{
    public const string SourcesKind = "sources";
    public const string TokenKind = "token";
    public const string ToolCallKind = "tool_call";
    public const string ToolResultKind = "tool_result";
    public const string DoneKind = "done";
    public const string ErrorKind = "error";
    public const string QuotaKind = "quota";

    /// <summary>
    /// <c>{"type":"sources","sources":[{kind,id,title}]}</c>. Always the first frame
    /// of a turn, even when the list is empty — the client uses it to clear the
    /// previous turn's citation chips.
    /// </summary>
    public static AiStreamEvent Sources(IReadOnlyList<AiStreamSource> sources) =>
        new(SourcesKind, new Dictionary<string, object?>(1) { ["sources"] = sources });

    /// <summary><c>{"type":"token","text":"..."}</c>. The client appends; it does not replace.</summary>
    public static AiStreamEvent Token(string text) =>
        new(TokenKind, new Dictionary<string, object?>(1) { ["text"] = text });

    /// <summary>
    /// <c>{"type":"tool_call","callId","name","args","needsConfirmation"}</c>.
    ///
    /// <para>
    /// <c>needsConfirmation</c> is true for <c>deleteAllTasks</c> and NOTHING else —
    /// see <c>AiToolCatalog.RequiresConfirmation</c>. Getting it wrong either strands
    /// a destructive call behind a card nobody can dismiss, or runs a bulk wipe with
    /// no speed bump.
    /// </para>
    /// </summary>
    public static AiStreamEvent ToolCall(
        string callId,
        string name,
        object? args,
        bool needsConfirmation) =>
        new(ToolCallKind, new Dictionary<string, object?>(4)
        {
            ["callId"] = callId,
            ["name"] = name,
            ["args"] = args,
            ["needsConfirmation"] = needsConfirmation,
        });

    /// <summary>
    /// <c>{"type":"tool_result","callId","result","error"}</c>.
    ///
    /// <para>
    /// <b>BOTH <c>result</c> and <c>error</c> are always present</b>, and the unused
    /// one is an explicit <c>null</c>. The client branches on
    /// <c>error !== null</c>, so omitting the key entirely reads as "succeeded" —
    /// which is why the frame writer must not use the response serializer's
    /// <c>WhenWritingNull</c> policy.
    /// </para>
    /// </summary>
    public static AiStreamEvent ToolResult(string callId, object? result, string? error) =>
        new(ToolResultKind, new Dictionary<string, object?>(3)
        {
            ["callId"] = callId,
            ["result"] = result,
            ["error"] = error,
        });

    /// <summary>
    /// <c>{"type":"done","usage":{...}}</c>. <c>usage</c> is an OBJECT, never null;
    /// the decline path and the not-configured continuation both send <c>{}</c>.
    /// </summary>
    public static AiStreamEvent Done(IReadOnlyDictionary<string, object?>? usage = null) =>
        new(DoneKind, new Dictionary<string, object?>(1)
        {
            ["usage"] = usage ?? EmptyUsage,
        });

    /// <summary>
    /// <c>{"type":"error","code","message"}</c> — a failure that happened AFTER the
    /// headers flushed. Before the flush the same failure is an ordinary JSON HTTP
    /// error with a real status code; clients branch on <c>Content-Type</c>.
    /// </summary>
    public static AiStreamEvent Error(string code, string message) =>
        new(ErrorKind, new Dictionary<string, object?>(2)
        {
            ["code"] = code,
            ["message"] = message,
        });

    /// <summary>
    /// <c>{"type":"quota","tier","quotas":[...]}</c>. Synthesised by the route right
    /// after <c>done</c>, so it is the last frame. No provider ever emits it.
    /// </summary>
    public static AiStreamEvent Quota(string tier, IReadOnlyList<AiQuotaStatusDto> quotas) =>
        new(QuotaKind, new Dictionary<string, object?>(2)
        {
            ["tier"] = tier,
            ["quotas"] = quotas,
        });

    /// <summary>Shared so every <c>usage:{}</c> is the same empty object.</summary>
    public static readonly IReadOnlyDictionary<string, object?> EmptyUsage =
        new Dictionary<string, object?>(0);
}

/// <summary>
/// One grounding citation inside a <c>sources</c> frame. Mirrors
/// <c>contextBuilder.ts</c>'s <c>ContextSource</c> and the persisted
/// <c>AiConversationSourceDocument</c>: <c>kind</c> is <c>task</c> or <c>voice</c>,
/// and <c>id</c> is a plain field, not a document key.
/// </summary>
public sealed class AiStreamSource
{
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; init; } = "task";

    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
}
