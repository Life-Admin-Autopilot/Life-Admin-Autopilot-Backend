using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Ai;

/// <summary>
/// The key an external agent's per-session memory hangs on — <c>&lt;userId&gt;:&lt;generation&gt;</c>.
///
/// <para>
/// <b>Both halves are load-bearing, for different reasons.</b> The user id is what
/// keeps one account's memory out of another's: the agent has no notion of our
/// tenancy, so the only thing separating two users' histories inside it is that
/// their session keys differ. The generation is what makes
/// <c>POST /ai/conversation/reset</c> honest: it changes on every reset, so the next
/// turn addresses a session the agent has never seen, and the answer it gives cannot
/// come from a conversation the user believes they deleted.
/// </para>
///
/// <para>
/// <b>It must NOT change for any other reason.</b> A multi-turn exchange and the
/// continuation that follows a confirmation have to land in the same session or the
/// agent forgets its own plan halfway through — which is why the generation is the
/// conversation document's id (or the key a reset put there), and not the turn, the
/// request, or the clock.
/// </para>
///
/// <para>
/// The format is one place on purpose. It is written by the provider on every turn
/// and read by the reset path to name the session being retired; two spellings of it
/// would mean a reset that forgets a session nobody was using.
/// </para>
/// </summary>
public static class AgentSessionId
{
    /// <summary>Separates the owner from the generation. Neither half can contain it.</summary>
    public const char Separator = ':';

    /// <summary>
    /// <paramref name="generation"/> comes from
    /// <c>AiConversationDocument.SessionGeneration</c>. Both arguments are ObjectIds,
    /// so the result is 49 characters of hex and one colon — no escaping, no user
    /// input, and nothing that could collide across accounts.
    /// </summary>
    public static string For(ObjectId userId, ObjectId generation) =>
        string.Concat(userId.ToString(), Separator.ToString(), generation.ToString());
}
