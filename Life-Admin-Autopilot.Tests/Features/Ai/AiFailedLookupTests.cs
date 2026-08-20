using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// <b>The turn that answers a data question after its data lookup died.</b>
///
/// <para>
/// Reproduced live against the running flow. Asked "What do I have on Friday August 28?"
/// the agent built a correct range —
/// <c>due_after 2026-08-28T00:00:00+03:00, due_before 2026-08-28T23:59:59+03:00</c> —
/// the tool answered
/// <c>{"ok": false, "error": "misconfigured", "message": "access_token is empty…"}</c>,
/// and the reply was <i>"I don't see anything on your schedule for Friday, August 28."</i>
/// </para>
///
/// <para>
/// A dead lookup rendered as an empty calendar. Nothing in the product contradicts it,
/// so the user believes their day is clear and misses the thing — the same failure class
/// as <see cref="FabricatedActionGuard"/>, pointed the other way.
/// </para>
///
/// <para>
/// The two halves of the contract these pin: the guard FIRES on a failure the tool's own
/// envelope reports while Langflow calls the call a success, and it stays SILENT on every
/// turn where the lookup actually answered — including the legitimately empty one.
/// </para>
/// </summary>
public sealed class AiFailedLookupTests
{
    // ---- it fires -----------------------------------------------------------

    /// <summary>
    /// The exact payload measured live. Langflow reports the call as a SUCCESS — the
    /// tool returned HTTP 200 — so <c>Status</c> is <c>executed</c> and only the envelope
    /// says otherwise. Reading the status alone would miss this entirely, which is why
    /// the guard reads the payload.
    /// </summary>
    [Fact]
    public void fires_on_ok_false_even_though_langflow_called_the_call_a_success()
    {
        var verdict = FailedLookupGuard.FirstFailedLookup([
            Call("queryTasks", "executed", """{"ok":false,"error":"misconfigured","message":"access_token is empty"}"""),
        ]);

        Assert.NotNull(verdict);
        Assert.Equal("queryTasks", verdict!.Value.ToolName);
        Assert.True(verdict.Value.WithholdAnswer);
    }

    [Fact]
    public void fires_on_a_rejected_session()
    {
        var verdict = FailedLookupGuard.FirstFailedLookup([
            Call("queryTasks", "executed", """{"ok":false,"status":401,"error":"unauthorized"}"""),
        ]);

        Assert.NotNull(verdict);
    }

    [Fact]
    public void fires_when_langflow_itself_reports_the_call_failed()
    {
        var verdict = FailedLookupGuard.FirstFailedLookup([
            Call("queryTasks", "failed", result: null),
        ]);

        Assert.NotNull(verdict);
    }

    /// <summary>
    /// A failed read alongside REAL work keeps its prose — the user still needs the
    /// receipt for the matter that was actually created — but the frame fires anyway.
    /// Dropping the receipt would trade one silent failure for another.
    /// </summary>
    [Fact]
    public void keeps_the_prose_when_something_real_happened_in_the_same_turn()
    {
        var verdict = FailedLookupGuard.FirstFailedLookup([
            Call("createTask", "executed", """{"ok":true,"task":{"id":"6a7a64a0f9aa485661606001"}}"""),
            Call("queryTasks", "executed", """{"ok":false,"error":"misconfigured"}"""),
        ]);

        Assert.NotNull(verdict);
        Assert.False(verdict!.Value.WithholdAnswer);
    }

    // ---- it stays silent ----------------------------------------------------

    /// <summary>
    /// The hardest half. A lookup that RAN and found nothing is a real answer and must
    /// pass — this is the case the guard must never confuse with a failure, or every
    /// genuinely empty day becomes an error.
    /// </summary>
    [Fact]
    public void stays_silent_on_a_lookup_that_answered_zero_rows()
    {
        Assert.Null(FailedLookupGuard.FirstFailedLookup([
            Call("queryTasks", "executed", """{"ok":true,"count":0,"total":0,"tasks":[]}"""),
        ]));
    }

    [Fact]
    public void stays_silent_on_a_turn_that_called_nothing()
    {
        Assert.Null(FailedLookupGuard.FirstFailedLookup([]));
    }

    /// <summary>
    /// A failed WRITE is <see cref="FabricatedActionGuard"/>'s job: it returns no id, so
    /// any claim resting on it is already unaccounted. Firing here too would put two
    /// error frames on one failure and say the user's matters could not be read, which is
    /// not what happened.
    /// </summary>
    [Fact]
    public void stays_silent_on_a_failed_write()
    {
        Assert.Null(FailedLookupGuard.FirstFailedLookup([
            Call("createTask", "executed", """{"ok":false,"error":"validation_failed"}"""),
        ]));
    }

    /// <summary>
    /// An <c>executed</c> call whose result never arrived is not judged — the same
    /// reasoning as <c>FabricatedActionGuard.IsVerifiable</c>. An outcome we never saw
    /// cannot be called a failure without flagging turns that did real work.
    /// </summary>
    [Fact]
    public void stays_silent_when_the_result_never_arrived()
    {
        Assert.Null(FailedLookupGuard.FirstFailedLookup([
            Call("queryTasks", "executed", result: null),
        ]));
    }

    /// <summary>
    /// A tool answering in a shape the guard does not model passes. Absent <c>ok</c> is
    /// not a failure — a future tool must not start tripping this by returning something
    /// new.
    /// </summary>
    [Fact]
    public void stays_silent_on_a_result_with_no_ok_field()
    {
        Assert.Null(FailedLookupGuard.FirstFailedLookup([
            Call("queryTasks", "executed", """{"tasks":[]}"""),
        ]));
    }

    private static TranslatedToolCall Call(string name, string status, string? result)
    {
        JsonElement? parsed = result is null
            ? null
            : JsonDocument.Parse(result).RootElement.Clone();

        return new TranslatedToolCall(
            CallId: $"call-{name}-{status}",
            Name: name,
            Args: null,
            Status: status,
            Result: parsed);
    }
}
