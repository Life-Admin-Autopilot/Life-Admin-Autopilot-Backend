namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// The conversation memory that lives OUTSIDE this process — the copy an external
/// agent keeps, keyed on the session id we send it (<see cref="AgentSessionId"/>).
///
/// <para>
/// Rotating that key on reset is what stops a new conversation inheriting an old
/// one's memory, and it is sufficient for correctness on its own: the retired
/// session is never addressed again. This seam exists for the part rotation does
/// NOT do — the retired messages are still sitting in the agent's store, verbatim,
/// after a user pressed a button labelled "clear". Dropping them is a deletion on
/// somebody else's system, so it is best-effort by construction and never a
/// precondition for the local reset.
/// </para>
///
/// <para>
/// The default registration is <see cref="NoAgentSessionMemory"/>. A deployment with
/// no external agent has nothing to forget, and the parity target — the reference
/// server with no AI key — must not acquire an outbound call it never made.
/// </para>
/// </summary>
public interface IAgentSessionMemory
{
    /// <summary>
    /// Ask the agent to drop everything it holds for <paramref name="sessionId"/>.
    ///
    /// <para>
    /// Implementations may throw; the caller treats a failure as "the copy outlives
    /// the reset", which is untidy rather than wrong. They must not, however, take
    /// unbounded time — the caller is a user-facing request.
    /// </para>
    /// </summary>
    Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// No external agent, nothing to forget. The registration that holds the parity
/// target: <c>POST /ai/conversation/reset</c> makes exactly the writes it always
/// made and no network call at all.
/// </summary>
public sealed class NoAgentSessionMemory : IAgentSessionMemory
{
    public Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
