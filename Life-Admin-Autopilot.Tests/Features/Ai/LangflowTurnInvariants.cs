using System.Text.Json;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Features.Ai;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The invariants one Langflow turn must satisfy, each one a bug that actually
/// shipped in this adapter.
///
/// <para>
/// <b>Why they live in their own type rather than on a test class.</b> A public
/// non-test method on a test class is an xUnit1013 warning, and more usefully these
/// belong to the turn rather than to whichever test happens to run them. Three
/// callers share them: <see cref="LangflowSmokeTests"/> runs them against a real
/// Langflow, <c>LangflowProviderTests</c> runs them against a stubbed turn so the
/// assertion logic is proven where no Langflow exists, and
/// <see cref="LangflowTurnInvariantsTests"/> feeds each one the defect it was written
/// for and proves it goes red. Without that third caller a green live run would mean
/// nothing: an assertion that cannot fail is worse than no assertion.
/// </para>
///
/// <para>
/// <b>Nothing here reads the reply text as evidence.</b> Every claim about what
/// happened is either a property of the frames themselves or a value the CALLER
/// measured in Mongo or through the API and passed in — counts, a stored status, a
/// stored <c>dueAt</c>. That is deliberate: every defect this file exists to catch
/// was invisible in a reply that read perfectly well.
/// </para>
///
/// <para>
/// <b>Payload values are read tolerantly</b> (see <see cref="Text"/>). An event
/// produced in-process carries CLR values; the same event parsed back off an SSE wire
/// carries <see cref="JsonElement"/>s. Both must reach the same assertions, or the
/// confirm stream — which only exists over HTTP — could not be checked at all.
/// </para>
/// </summary>
public static class LangflowTurnInvariants
{
    /// <summary>
    /// The wrappers <see cref="Life_Admin_Autopilot.BLL.Features.Ai.Langflow.LangflowToolResult"/>
    /// strips. Seeing one of them still on a <c>tool_result</c> is the double-encoding
    /// regression, so they are asserted absent rather than merely dug through.
    /// </summary>
    private static readonly string[] WrapperKeys = { "content", "value", "artifact", "output" };

    // ---- 0. the shape of any turn ------------------------------------------

    public static void AssertTurnShape(IReadOnlyList<AiStreamEvent> events)
    {
        Assert.NotEmpty(events);

        // 1. The turn opens with sources and closes with done.
        Assert.Equal(AiStreamEvents.SourcesKind, events[0].Kind);
        Assert.Equal(AiStreamEvents.DoneKind, events[^1].Kind);

        var calls = events.Where(e => e.Kind == AiStreamEvents.ToolCallKind).ToList();
        var callIds = calls.Select(c => Text(c, "callId")!).ToList();

        // 2. No tool is announced twice under the SAME id. Weak on its own — see
        //    AssertOneFramePerToolInvocation for the redelivery case, which mints a
        //    DIFFERENT id each time and sails straight through this check.
        Assert.Equal(callIds.Count, callIds.Distinct(StringComparer.Ordinal).Count());

        var resultIds = events
            .Where(e => e.Kind == AiStreamEvents.ToolResultKind)
            .Select(e => Text(e, "callId")!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var call in calls)
        {
            var callId = Text(call, "callId")!;

            // 3. A gated call is never resolved by the stream — Langflow's dry-run
            //    output is a preview, and treating it as an outcome killed every
            //    confirmation. The confirm route emits the only tool_result.
            if (Flag(call, "needsConfirmation"))
            {
                Assert.DoesNotContain(callId, resultIds);
            }
        }

        // 4. Every result belongs to a call that was announced.
        Assert.All(resultIds, id => Assert.Contains(id, callIds));
    }

    // ---- 1. the tool result the card actually reads -------------------------

    /// <summary>
    /// <b>A created matter must arrive as a CARD, not a bare ledger row.</b>
    /// <c>ToolCallCard.tsx</c> renders <c>result.task</c>; what Langflow puts on the
    /// wire is that object behind two layers of JSON-<i>string</i> encoding —
    /// <c>{"content":"{\"value\":\"{\\\"ok\\\":true,\\\"task\\\":{…}}\"}"}</c>. With
    /// the unwrapping gone, <c>result.task</c> is <c>undefined</c>, the card silently
    /// degrades, and the matter the agent just created is invisible. The reply still
    /// says "Added it", which is why no reply-shaped test could ever have caught this.
    /// </summary>
    /// <returns>The task id the result claims, for the caller to resolve against
    /// Mongo or the API — the frame saying <c>task.id</c> is not evidence a row
    /// exists.</returns>
    public static string AssertToolResultCarriesTaskAtTopLevel(
        IReadOnlyList<AiStreamEvent> events,
        string toolName)
    {
        var callIds = events
            .Where(e => e.Kind == AiStreamEvents.ToolCallKind)
            .Where(e => string.Equals(Text(e, "name"), toolName, StringComparison.Ordinal))
            .Select(e => Text(e, "callId")!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            callIds.Count > 0,
            $"the turn announced no '{toolName}' tool_call, so there is no result to check — "
            + $"frames were: {Describe(events)}");

        var results = events
            .Where(e => e.Kind == AiStreamEvents.ToolResultKind)
            .Where(e => callIds.Contains(Text(e, "callId") ?? string.Empty))
            .ToList();

        Assert.True(
            results.Count > 0,
            $"'{toolName}' ran but no tool_result reached the client, so the card has nothing to render.");

        string? taskId = null;

        foreach (var result in results)
        {
            var error = Text(result, "error");
            Assert.True(error is null, $"'{toolName}' failed: {error}");

            var payload = Element(result, "result");
            Assert.True(
                payload is { ValueKind: JsonValueKind.Object },
                $"tool_result.result is {payload?.ValueKind.ToString() ?? "absent"}, not an object — "
                + "the card cannot read a task off it.");

            var body = payload!.Value;

            foreach (var wrapper in WrapperKeys)
            {
                Assert.False(
                    body.TryGetProperty(wrapper, out _),
                    $"tool_result.result is still wrapped in '{wrapper}' — this is the double-encoding "
                    + $"that makes result.task undefined and renders a created matter as a bare ledger "
                    + $"row. Got: {Excerpt(body.GetRawText())}");
            }

            Assert.True(
                body.TryGetProperty("task", out var task),
                "tool_result.result has no TOP-LEVEL 'task'. ToolCallCard.tsx reads exactly that key; "
                + $"anything else renders as a plain row. Got: {Excerpt(body.GetRawText())}");

            Assert.Equal(JsonValueKind.Object, task.ValueKind);

            Assert.True(
                task.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()),
                $"result.task carries no id, so nothing can be resolved against the database. "
                + $"Got: {Excerpt(task.GetRawText())}");

            taskId = id.GetString();
        }

        return taskId!;
    }

    /// <summary>
    /// <b>The id on the frame must name a row that exists.</b> The card links to it,
    /// so an id the store does not recognise is a matter the user can see and cannot
    /// open — and the frame alone can never prove otherwise, because the frame is
    /// where the claim came from.
    /// </summary>
    /// <param name="storedById">Measured through the API or Mongo by the caller: every
    /// open matter the user actually has, and its stored <c>dueAt</c>.</param>
    /// <returns>That matter's stored <c>dueAt</c>, for <see cref="AssertStatedTimeRoundTrips"/>.</returns>
    public static DateTime? AssertClaimedMatterIsInTheStore(
        string taskId,
        IReadOnlyDictionary<string, DateTime?> storedById)
    {
        Assert.True(
            storedById.ContainsKey(taskId),
            $"the tool_result claimed matter '{taskId}' but the store has no such open row — it holds "
            + $"[{string.Join(", ", storedById.Keys)}]. The chat is showing a card for something that "
            + "does not exist.");

        return storedById[taskId];
    }

    // ---- 2. the envelope must not reach the bubble --------------------------

    /// <summary>
    /// An opening brace or bracket immediately followed by one of the planning
    /// agent's four envelope keys. Deliberately structural rather than a bare word
    /// match: a reply may legitimately contain the word "mode", but never
    /// <c>{"mode":</c>.
    /// </summary>
    private static readonly Regex EnvelopeOpening = new(
        @"[\{\[]\s*""(?:mode|reply|tasks|clarifications|pendingConfirmations)""\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Two keys with no business in prose at all, checked separately so a leak whose
    /// opening brace landed in an earlier turn is still caught.
    /// </summary>
    private static readonly Regex EnvelopeKey = new(
        @"""(?:pendingConfirmations|clarifications)""\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// <b>The chat bubble prints tokens verbatim.</b> The agent does not answer in
    /// prose — it answers with a JSON envelope
    /// (<c>{"mode","reply","tasks","clarifications","pendingConfirmations"}</c>) and
    /// <c>PlanningEnvelopeReader</c> streams out only what is inside <c>reply</c>.
    /// When that decoder breaks, the user reads raw JSON, and the failure is invisible
    /// to anything that checks tool calls or database rows.
    ///
    /// <para>
    /// <b>Checked on the CONCATENATION, not per frame.</b> Tokens arrive in arbitrary
    /// chunks, so <c>{"mo</c> and <c>de":</c> can be two separate frames and neither
    /// matches on its own.
    /// </para>
    ///
    /// <para>
    /// <b>Requiring prose is half the point.</b> A turn that emitted no token at all
    /// would satisfy "no leakage" vacuously, and an envelope reader that swallowed
    /// everything would look exactly like that.
    /// </para>
    /// </summary>
    /// <returns>Everything the user would have read.</returns>
    public static string AssertTokensCarryProseNotTheEnvelope(IReadOnlyList<AiStreamEvent> events)
    {
        var tokens = events
            .Where(e => e.Kind == AiStreamEvents.TokenKind)
            .Select(e => Text(e, "text") ?? string.Empty)
            .ToList();

        Assert.True(
            tokens.Count > 0,
            $"the turn produced no token frame, so the chat bubble stayed empty — frames were: {Describe(events)}");

        var prose = string.Concat(tokens);

        Assert.False(
            string.IsNullOrWhiteSpace(prose),
            "the turn produced token frames carrying nothing but whitespace.");

        Assert.False(
            EnvelopeOpening.IsMatch(prose),
            "the agent's JSON envelope leaked into the chat bubble — the user is reading the document "
            + $"instead of the reply. Got: {Excerpt(prose)}");

        Assert.False(
            EnvelopeKey.IsMatch(prose),
            $"an envelope key reached the chat bubble as prose. Got: {Excerpt(prose)}");

        Assert.False(
            prose.TrimStart().StartsWith("```", StringComparison.Ordinal),
            $"the reply opens with a code fence, so the envelope is being printed inside one. Got: {Excerpt(prose)}");

        return prose;
    }

    // ---- 3. one frame per tool invocation -----------------------------------

    /// <summary>
    /// <b>Langflow redelivers the same <c>add_message</c> row as it fills in.</b> Its
    /// <c>tool_use</c> blocks carry no id of their own, so an id minted per delivery
    /// produced SEVEN <c>tool_call</c> frames for one <c>queryTasks</c> — and seven
    /// confirmation cards for one bulk delete.
    ///
    /// <para>
    /// <b><see cref="AssertTurnShape"/>'s distinct-id check cannot see this</b>, which
    /// is exactly how the bug survived: the seven ids WERE distinct. The only stable
    /// signature of one invocation is what the agent asked for, so the fingerprint
    /// here is <c>(name, args)</c>.
    /// </para>
    /// </summary>
    public static void AssertOneFramePerToolInvocation(IReadOnlyList<AiStreamEvent> events)
    {
        var duplicates = events
            .Where(e => e.Kind == AiStreamEvents.ToolCallKind)
            .GroupBy(
                e => $"{Text(e, "name")}({Element(e, "args")?.GetRawText() ?? "null"})",
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "the same tool invocation was announced more than once, so the client renders one action as "
            + "several. Langflow redelivers a row as it fills in; a call id minted per delivery defeats the "
            + "dedup while still looking unique. Repeated: "
            + string.Join(
                "; ",
                duplicates.Select(g => $"{g.Count()}× {Excerpt(g.Key)} as {string.Join(",", g.Select(e => Text(e, "callId")))}")));
    }

    /// <summary>
    /// The same claim, anchored to something outside the stream: <b>N announcements
    /// of a tool must mean N things actually happened.</b> Frame-only checks can be
    /// fooled by a fingerprint that legitimately differs; a row count in Mongo cannot.
    /// </summary>
    /// <param name="observedSideEffects">What the caller counted in Mongo or through
    /// the API — created rows, deleted rows — never anything read off the reply.</param>
    public static void AssertToolCallCountMatchesSideEffects(
        IReadOnlyList<AiStreamEvent> events,
        string toolName,
        int observedSideEffects)
    {
        var announced = events
            .Count(e => e.Kind == AiStreamEvents.ToolCallKind
                        && string.Equals(Text(e, "name"), toolName, StringComparison.Ordinal));

        Assert.True(
            announced == observedSideEffects,
            $"the client was shown {announced} '{toolName}' call(s) but the database recorded "
            + $"{observedSideEffects}. Every one of those frames is a pill the user sees, so the two must "
            + "agree or the chat is narrating work that never happened.");
    }

    // ---- 4. confirmation gating ---------------------------------------------

    /// <summary>
    /// <b>A gated call is STAGED, never run.</b> Langflow executes
    /// <c>deleteAllTasks</c>'s dry run and redelivers the row with <c>output</c>
    /// populated inside the same turn — a preview that says so
    /// (<c>"executed": false</c>). Reading it as an outcome flipped the stored record
    /// to <c>executed</c>, and <c>RequirePendingToolCallAsync</c> then 404'd every
    /// confirmation the user tried to press.
    ///
    /// <para>Three independent witnesses, because any one of them alone has been
    /// wrong before: no <c>tool_result</c> frame, the stored status the confirm route
    /// actually reads, and the row count — the only one that proves nothing was
    /// deleted.</para>
    /// </summary>
    /// <returns>The pending call id, for the caller to confirm.</returns>
    public static string AssertGatedCallIsStagedNotRun(
        IReadOnlyList<AiStreamEvent> events,
        IReadOnlyDictionary<string, string> storedStatuses,
        int openTasksBefore,
        int openTasksAfter)
    {
        var gated = events
            .Where(e => e.Kind == AiStreamEvents.ToolCallKind && Flag(e, "needsConfirmation"))
            .ToList();

        Assert.True(
            gated.Count == 1,
            $"expected exactly one confirmation-gated tool_call, saw {gated.Count} — frames were: {Describe(events)}");

        var callId = Text(gated[0], "callId")!;

        Assert.Equal(AiToolCatalog.DeleteAllTasks, Text(gated[0], "name"));

        Assert.DoesNotContain(
            events,
            e => e.Kind == AiStreamEvents.ToolResultKind
                 && string.Equals(Text(e, "callId"), callId, StringComparison.Ordinal));

        var storedStatus = storedStatuses.GetValueOrDefault(callId, "<no record at all>");

        Assert.True(
            string.Equals(storedStatus, AiConversationVocabulary.PendingConfirmation, StringComparison.Ordinal),
            $"the durable record for {callId} says '{storedStatus}', not "
            + $"'{AiConversationVocabulary.PendingConfirmation}'. The confirm route reads exactly that field, "
            + "so any other value means the card the user is looking at can never be actioned.");

        Assert.True(
            openTasksAfter == openTasksBefore,
            $"asking for a bulk delete removed {openTasksBefore - openTasksAfter} matter(s) BEFORE the user "
            + "confirmed anything. deleteAllTasks is a dry run until the confirm route says otherwise.");

        return callId;
    }

    /// <summary>
    /// The other half: <b>once confirmed, it really runs.</b> A gate that never opens
    /// is as broken as one that never closes, and the pending-record bug above was
    /// only visible from this side.
    /// </summary>
    public static void AssertConfirmedCallActuallyRan(
        IReadOnlyList<AiStreamEvent> confirmEvents,
        string callId,
        IReadOnlyDictionary<string, string> storedStatuses,
        int openTasksBefore,
        int openTasksAfter,
        string? confirmRefusal = null)
    {
        // The route refusing outright is the loudest form of this failure and the one
        // the regression actually produced: the stream resolved the gated call from
        // Langflow's dry-run preview, the record stopped being pending, and the button
        // on the card the user is looking at answers 404 forever.
        Assert.True(
            confirmRefusal is null,
            $"POST /ai/tools/confirm/{callId} never streamed — it answered {confirmRefusal}. "
            + $"The durable record says '{storedStatuses.GetValueOrDefault(callId, "<no record at all>")}'.");

        var results = confirmEvents
            .Where(e => e.Kind == AiStreamEvents.ToolResultKind
                        && string.Equals(Text(e, "callId"), callId, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            results.Count == 1,
            $"the confirm stream carried {results.Count} tool_result frames for {callId}; the client renders "
            + "one outcome per action.");

        var error = Text(results[0], "error");
        Assert.True(error is null, $"the confirmed delete failed: {error}");

        var storedStatus = storedStatuses.GetValueOrDefault(callId, "<no record at all>");

        Assert.True(
            string.Equals(storedStatus, "executed", StringComparison.Ordinal),
            $"the durable record for {callId} says '{storedStatus}' after a successful confirm, so the same "
            + "card could be pressed again.");

        Assert.True(
            openTasksAfter < openTasksBefore,
            $"the confirm reported success but the open-matter count did not move ({openTasksBefore} → "
            + $"{openTasksAfter}). A trailing error frame on the continuation does NOT mean the action "
            + "failed — the row count is the only thing that settles it.");
    }

    // ---- 5. the time the user actually said ---------------------------------

    /// <summary>
    /// <b>A time the user stated must round-trip exactly.</b> The offset on
    /// <c>currentDate</c> is what makes this work, and omitting it fails SILENTLY: the
    /// agent invents <c>+00:00</c> rather than erroring, so every derived
    /// <c>dueAt</c> lands out by the user's whole UTC offset. Measured on the live
    /// stack with <c>Africa/Cairo</c>: a 15:00 local reminder stored as 15:00Z instead
    /// of 12:00Z, with a reply that read perfectly.
    ///
    /// <para><paramref name="storedUtc"/> comes from the stored row, never from the
    /// reply — the reply says "3 PM" in both the correct and the broken case.</para>
    /// </summary>
    public static void AssertStatedTimeRoundTrips(
        DateTime expectedUtc,
        DateTime? storedUtc,
        string statedLocally)
    {
        Assert.True(
            storedUtc.HasValue,
            $"the user said '{statedLocally}' and the stored matter has no dueAt at all — a missing time was "
            + "silently defaulted away.");

        Assert.True(
            DateTime.SpecifyKind(storedUtc!.Value, DateTimeKind.Utc) == expectedUtc,
            $"the user said '{statedLocally}'. Expected {expectedUtc:yyyy-MM-dd'T'HH:mm:ss}Z, stored "
            + $"{storedUtc.Value:yyyy-MM-dd'T'HH:mm:ss}Z — a whole-offset error is what an offset-free "
            + "currentDate produces, and nothing anywhere reports it.");
    }

    // ---- tolerant payload readers -------------------------------------------

    /// <summary>
    /// A payload value as text. In-process events carry CLR values; the same events
    /// parsed back off an SSE wire carry <see cref="JsonElement"/>s. Both must reach
    /// the same assertions — the confirm stream exists only over HTTP.
    /// </summary>
    private static string? Text(AiStreamEvent value, string key)
    {
        if (!value.Payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => raw.ToString(),
        };
    }

    private static bool Flag(AiStreamEvent value, string key) =>
        value.Payload.TryGetValue(key, out var raw)
        && raw switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false,
        };

    /// <summary>
    /// A payload value as JSON. Anything that is not already a
    /// <see cref="JsonElement"/> — the confirm runner returns a plain dictionary — is
    /// serialized with the frame serializer, so what is inspected is exactly what the
    /// client would have received.
    /// </summary>
    private static JsonElement? Element(AiStreamEvent value, string key)
    {
        if (!value.Payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Null ? null : element;
        }

        return JsonSerializer.SerializeToElement(raw, AiStreamJson.Frame);
    }

    /// <summary>
    /// The frame sequence, with any <c>error</c> spelled out. A turn truncated by
    /// Mistral's free-tier 429 arrives as an <c>error</c> frame inside a healthy 200,
    /// and without its message the resulting failure reads as a flow defect rather
    /// than as "the suite ran out of quota".
    /// </summary>
    private static string Describe(IReadOnlyList<AiStreamEvent> events) =>
        string.Join(
            " → ",
            events.Select(e => e.Kind == AiStreamEvents.ErrorKind
                ? $"error({Excerpt(Text(e, "message") ?? "?")})"
                : e.Kind));

    private static string Excerpt(string text) =>
        text.Length <= 240 ? text : text[..240] + "…";
}
