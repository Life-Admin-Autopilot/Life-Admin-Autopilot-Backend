using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <b>Did the turn claim work that never happened?</b>
///
/// <para>
/// The failure this exists for, measured live on a fresh account: one turn replied
/// <i>"Added it."</i> with zero <c>tool_call</c> frames and zero rows written, and an
/// earlier one wrote a complete clarification into the envelope under an invented
/// <c>taskId</c> ("hold_math_lec_2026-08-12") having called nothing. It is the worst
/// failure class in this product, because it is the only one where the user is told a
/// thing is filed and <b>no surface will ever contradict them</b> — the matter list
/// simply does not have it, and nothing explains why.
/// </para>
///
/// <para>
/// <b>Why a prompt line is not the fix.</b> The flow already forbids it. But the model
/// that produces this is the degraded one — rate-limited, truncated, or having a bad
/// day — and a degraded model is exactly the one that will not police itself. Every
/// defect in this adapter has had the same shape: an assumption about what the agent
/// emits, checked nowhere.
/// </para>
///
/// <para>
/// <b>It is a correction, not a prevention.</b> The tokens have already streamed by the
/// time the envelope is complete. What the caller can still do is refuse to persist the
/// claim and tell the user, which is the difference between a visible error and a
/// silent lie.
/// </para>
/// </summary>
public static class FabricatedActionGuard
{
    /// <summary>
    /// The code on the <c>error</c> frame. Distinct from <c>langflow_error</c>: the
    /// agent worked, the run succeeded, and what failed is the truthfulness of the
    /// answer.
    /// </summary>
    public const string ErrorCode = "unverified_action";

    /// <summary>
    /// What the user reads. Deliberately concrete about the consequence — "check your
    /// matters" — because the whole point is that they must not walk away believing
    /// something was filed.
    /// </summary>
    public const string ErrorMessage =
        "The assistant described an action it never actually performed. That part of its answer " +
        "has been discarded — check your matters and ask again.";

    /// <summary>
    /// The first claim nothing accounts for, or null when the turn is honest — or when
    /// it cannot be judged.
    ///
    /// <para>
    /// <b>Three ways it deliberately stays silent</b>, because a false accusation is
    /// worse than a missed one:
    /// </para>
    ///
    /// <list type="number">
    ///   <item>
    ///     The envelope did not parse, or was not an envelope at all
    ///     (<see cref="PlanningEnvelopeClaims.Parsed"/> false). A plain-prose flow and a
    ///     truncated answer both land here.
    ///   </item>
    ///   <item>
    ///     The envelope claims nothing structurally. An ordinary chat turn — "Hi, how
    ///     can I help?" — has empty arrays and no tools, and MUST pass.
    ///   </item>
    ///   <item>
    ///     A tool ran whose outcome never arrived. The translator marks a non-gated
    ///     call <c>executed</c> the moment it is announced and fills the result in when
    ///     Langflow delivers it; if a result never came, the ids that call returned are
    ///     unknown to us and a claim resting on one cannot be disproved. Judging it
    ///     anyway would flag a turn that did real work.
    ///   </item>
    /// </list>
    /// </summary>
    public static EnvelopeClaim? FirstUnaccounted(
        PlanningEnvelopeClaims envelope,
        IReadOnlyList<TranslatedToolCall> calls)
    {
        if (!envelope.Parsed || envelope.Claims.Count == 0)
        {
            return null;
        }

        if (!IsVerifiable(calls))
        {
            return null;
        }

        var returnedIds = ReturnedIds(calls);
        var hasGatedCall = calls.Any(c => c.Status == PendingConfirmation);

        foreach (var claim in envelope.Claims)
        {
            if (claim.Section == PlanningEnvelopeClaims.PendingConfirmationsSection)
            {
                // Satisfied by the card actually existing — a gated call the client
                // rendered a confirm button from. Claimed without one, the user is
                // asked to confirm something nothing can confirm.
                if (!hasGatedCall)
                {
                    return claim;
                }

                continue;
            }

            // No id at all: the contract says such a row must not be in the array,
            // so its presence is the fabrication.
            if (claim.Id is null || !returnedIds.Contains(claim.Id))
            {
                return claim;
            }
        }

        return null;
    }

    private const string PendingConfirmation = "pending_confirmation";

    private const string Executed = "executed";

    /// <summary>
    /// Every executed call told us what it returned. A gated call is expected to be
    /// unresolved this turn — its result belongs to the confirm route — so it does not
    /// count against verifiability. A failed call contributes no ids and is not
    /// required to carry a result either.
    /// </summary>
    private static bool IsVerifiable(IReadOnlyList<TranslatedToolCall> calls) =>
        calls.All(c => c.Status != Executed || c.Result is not null);

    /// <summary>
    /// Every string anywhere inside the results of the calls that actually ran.
    ///
    /// <para>
    /// A whole-value walk rather than a lookup at a known path, because tool results
    /// are <c>Schema.Types.Mixed</c> and their shapes differ per tool — <c>createTask</c>
    /// answers <c>{ok, task:{id,…}}</c>, <c>holdForClarification</c> answers
    /// <c>{clarification:{taskId,…}}</c>, and a new tool will answer something else. The
    /// question being asked is only "did this id come back from a tool", and any string
    /// the tool returned is a legitimate answer to it.
    /// </para>
    ///
    /// <para>
    /// <b>Arguments are excluded on purpose.</b> An id the model invented and passed
    /// INTO a tool would otherwise account for itself.
    /// </para>
    /// </summary>
    private static HashSet<string> ReturnedIds(IReadOnlyList<TranslatedToolCall> calls)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var call in calls)
        {
            if (call.Status == Executed && call.Result is { } result)
            {
                CollectStrings(result, ids);
            }
        }

        return ids;
    }

    private static void CollectStrings(JsonElement element, HashSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    into.Add(value);
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStrings(property.Value, into);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, into);
                }

                break;
        }
    }
}
