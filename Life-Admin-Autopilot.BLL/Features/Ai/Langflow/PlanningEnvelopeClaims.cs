using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// One thing the envelope says the turn DID: a row in <c>tasks</c>, a row in
/// <c>clarifications</c>, or a row in <c>pendingConfirmations</c>.
/// </summary>
/// <param name="Section">Which array it came from — for the log line, not for logic.</param>
/// <param name="Id">
/// The id the claim rests on: <c>tasks[].id</c> or <c>clarifications[].taskId</c>.
/// <b>Null is itself a claim worth checking</b>, not a missing value to skip: the
/// flow's contract says an item with no tool-returned id "does not go in the array at
/// all", so a row that omits one is already asserting something that did not happen.
/// A <c>pendingConfirmations</c> row carries no id and is satisfied by a gated call.
/// </param>
public readonly record struct EnvelopeClaim(string Section, string? Id);

/// <summary>
/// The structured half of the planning agent's envelope —
/// <c>{"mode":…,"reply":…,"tasks":[…],"clarifications":[…],"pendingConfirmations":[…]}</c>
/// — read for one purpose: to find out what the turn CLAIMS it did, so the claim can
/// be checked against what actually ran.
///
/// <para>
/// <b>Why the structured half and never the prose.</b> "Added it." is a claim too, but
/// detecting it means matching words, and the reply is written in the user's language.
/// A word matcher would fire on "Hi! How can I help?" in one locale and miss a filed
/// task in another. The arrays are the deterministic signal: the flow's contract calls
/// <c>tasks</c> "a receipt of what you actually did", requires every id in it to be one
/// "the tool returned", and forbids inventing one. That is a checkable statement.
/// </para>
///
/// <para>
/// <b>Parsing is all-or-nothing and fails silent.</b> A truncated envelope — the model
/// cut off mid-answer, or a rate limit ended the stream — yields
/// <see cref="Parsed"/> false and no claims. That path already surfaces as an error of
/// its own, and guessing at a half-written array is exactly how a guard starts
/// producing false accusations.
/// </para>
/// </summary>
public readonly record struct PlanningEnvelopeClaims(bool Parsed, IReadOnlyList<EnvelopeClaim> Claims)
{
    /// <summary>Nothing was claimed, or nothing could be read. Either way, no verdict.</summary>
    public static readonly PlanningEnvelopeClaims None = new(false, Array.Empty<EnvelopeClaim>());

    public const string TasksSection = "tasks";

    public const string ClarificationsSection = "clarifications";

    public const string PendingConfirmationsSection = "pendingConfirmations";

    /// <summary>
    /// Read the arrays out of one complete envelope. <paramref name="envelope"/> is
    /// the raw model output; anything that is not a JSON object returns
    /// <see cref="None"/>, which is the honest answer for a flow that replies in
    /// plain prose.
    /// </summary>
    public static PlanningEnvelopeClaims Read(string? envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope))
        {
            return None;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelope);
        }
        catch (JsonException)
        {
            return None;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            var claims = new List<EnvelopeClaim>();

            Collect(document.RootElement, TasksSection, "id", claims);
            Collect(document.RootElement, ClarificationsSection, "taskId", claims);

            // No id to check: a pending confirmation is accounted for by the gated
            // tool call the client renders its card from, so its claim is "a card
            // exists", checked by the guard rather than by an id here.
            Collect(document.RootElement, PendingConfirmationsSection, null, claims);

            return new PlanningEnvelopeClaims(true, claims);
        }
    }

    private static void Collect(
        JsonElement root,
        string section,
        string? idKey,
        List<EnvelopeClaim> into)
    {
        if (!root.TryGetProperty(section, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (idKey is null)
            {
                into.Add(new EnvelopeClaim(section, null));
                continue;
            }

            var id = entry.ValueKind == JsonValueKind.Object
                     && entry.TryGetProperty(idKey, out var value)
                     && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            into.Add(new EnvelopeClaim(section, string.IsNullOrWhiteSpace(id) ? null : id));
        }
    }
}
