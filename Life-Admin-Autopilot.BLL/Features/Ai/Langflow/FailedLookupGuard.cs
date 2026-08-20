using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>Did the turn answer a data question after its data lookup failed?</b>
///
/// <para>
/// The failure this exists for, reproduced live against the running flow: a turn asked
/// "What do I have on Friday August 28?" called <c>queryTasks</c> with a correct
/// range, got back
/// <c>{"ok": false, "error": "misconfigured", "message": "access_token is empty…"}</c>,
/// and told the user <i>"I don't see anything on your schedule for Friday, August 28."</i>
/// </para>
///
/// <para>
/// <b>A failed lookup rendered as an empty calendar is the same failure class as
/// <see cref="FabricatedActionGuard"/>, pointed the other way.</b> There, the user is
/// told something was filed that never was. Here, they are told they have nothing when
/// nobody actually looked. Both are answers no other surface contradicts — the user
/// simply believes their day is clear and walks away. It is strictly worse than an
/// error, because an error makes them ask again.
/// </para>
///
/// <para>
/// <b>Why the tool's own status is not enough.</b> The translator marks a call
/// <c>failed</c> only when LANGFLOW reports an error. A tool that reaches our API and
/// gets a 401 — or refuses to call at all because its access token is blank — returns
/// HTTP 200 carrying <c>{"ok": false}</c>, so Langflow calls it a success and the call
/// is recorded <c>executed</c>. The failure is in the PAYLOAD, and that is where this
/// looks.
/// </para>
///
/// <para>
/// <b>Why a prompt line is not the fix</b>, for the same reason it is not the fix in
/// <see cref="FabricatedActionGuard"/>: the flow already tells the agent to report tool
/// failures, and the agent that ignores it is the degraded one. Worse, the prompt's own
/// prescribed wording for this case — "I don't have that in your data" — is itself
/// indistinguishable from a real empty result. Section 9 is being corrected alongside
/// this guard; the guard is what holds when the correction does not take.
/// </para>
/// </summary>
public static class FailedLookupGuard
{
    /// <summary>
    /// The code on the <c>error</c> frame. Distinct from <c>langflow_error</c> (the run
    /// itself broke) and from <c>unverified_action</c> (the answer claimed work): here
    /// the run succeeded and the answer is simply not founded on anything.
    /// </summary>
    public const string ErrorCode = "lookup_failed";

    /// <summary>
    /// What the user reads.
    ///
    /// <para>
    /// <b>Every clause is load-bearing and the wording was revised once already.</b> The
    /// first draft ended "Nothing here means your list is empty — please ask again",
    /// which parses two ways and the wrong one ("nothing here; [this] means your list is
    /// empty") says exactly the thing this guard exists to prevent. A message whose only
    /// job is to not be mistaken for emptiness cannot contain a sentence that can be
    /// read as confirming it. It now states the failure and the consequence in that
    /// order, with no negation to misparse.
    /// </para>
    /// </summary>
    public const string ErrorMessage =
        "Your matters could not be read just now, so the assistant does not know what you have. " +
        "This is not an empty list — please ask again.";

    /// <summary>
    /// The tools whose failure invalidates any statement about what the user has.
    ///
    /// <para>
    /// Only the read path. A failed <c>createTask</c> is already covered by
    /// <see cref="FabricatedActionGuard"/> (the turn claims a filing that returned no
    /// id); a failed <c>queryTasks</c> produces no claim to check, which is exactly why
    /// it needs its own guard.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ReadTools = new[] { "queryTasks" };

    private const string Executed = "executed";

    private const string Failed = "failed";

    /// <summary>The verdict: what failed, and whether the prose can survive it.</summary>
    /// <param name="ToolName">The read tool whose lookup did not answer.</param>
    /// <param name="WithholdAnswer">
    /// True when the turn produced NO successful tool call, so there is no true part of
    /// the answer worth keeping and the prose is pure invention. False when something
    /// real did happen this turn — a matter was created, a subtask ticked — because the
    /// user needs that receipt and dropping it would trade one silent failure for
    /// another.
    /// </param>
    public readonly record struct Verdict(string ToolName, bool WithholdAnswer);

    /// <summary>
    /// The first read lookup that failed, or null when every lookup answered — or when
    /// no lookup was attempted at all.
    ///
    /// <para>
    /// <b>It does not read the prose.</b> Deciding whether a sentence asserts absence is
    /// a language problem in two languages, and the product ships Arabic; a regex over
    /// "nothing" / "لا يوجد" would both miss and misfire. The question this asks instead
    /// is structural and has one answer: the agent was handed a failure, so anything it
    /// went on to say about the user's matters rests on nothing. That is true regardless
    /// of how the sentence is phrased, and true even when the agent phrased it honestly
    /// — in which case the frame merely says the same thing more reliably.
    /// </para>
    /// </summary>
    public static Verdict? FirstFailedLookup(IReadOnlyList<TranslatedToolCall> calls)
    {
        if (calls.Count == 0)
        {
            return null;
        }

        string? failedTool = null;

        foreach (var call in calls)
        {
            if (!ReadTools.Contains(call.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (DidFail(call))
            {
                failedTool = call.Name;
                break;
            }
        }

        if (failedTool is null)
        {
            return null;
        }

        return new Verdict(failedTool, WithholdAnswer: !calls.Any(Succeeded));
    }

    /// <summary>
    /// A call failed if Langflow said so, or if the tool's own envelope says
    /// <c>ok: false</c>. An <c>executed</c> call whose result never arrived is NOT
    /// counted: the same reasoning as <c>FabricatedActionGuard.IsVerifiable</c> — an
    /// outcome we never saw cannot be called a failure without flagging turns that did
    /// real work.
    /// </summary>
    private static bool DidFail(TranslatedToolCall call)
    {
        if (string.Equals(call.Status, Failed, StringComparison.Ordinal))
        {
            return true;
        }

        return call.Result is { } result && IsNotOk(result);
    }

    /// <summary>
    /// A call counts as real work only when it ran AND its envelope did not report a
    /// failure. A gated call (<c>pending_confirmation</c>) is deliberately not real work
    /// yet — nothing has been written, so it cannot vouch for prose about what exists.
    /// </summary>
    private static bool Succeeded(TranslatedToolCall call) =>
        string.Equals(call.Status, Executed, StringComparison.Ordinal)
        && call.Result is { } result
        && !IsNotOk(result);

    /// <summary>
    /// <c>{"ok": false}</c> on the tool's own envelope. Absent <c>ok</c> is not a
    /// failure: <c>holdForClarification</c> and a bare <c>{"ok": true}</c> both pass, and
    /// so does any future tool that answers in a shape this does not model.
    /// </summary>
    private static bool IsNotOk(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("ok", out var ok)
        && ok.ValueKind == JsonValueKind.False;
}
