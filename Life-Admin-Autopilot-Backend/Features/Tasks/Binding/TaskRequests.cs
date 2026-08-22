using System.Text.Json;

namespace Life_Admin_Autopilot_Backend.Features.Tasks.Binding;

/// <summary>
/// Request bodies for the Matters slice.
///
/// <para>
/// Every member is a <see cref="JsonElement"/> so that "absent" and "explicitly
/// null" stay distinguishable — see <see cref="BodyFields"/>. The property NAMES
/// are what the kernel's strict binder compares the incoming keys against, so
/// they must match the Node schema key-for-key: an extra property here would
/// silently accept a key zod rejects.
/// </para>
/// </summary>
public sealed class CreateTaskBody
{
    public JsonElement Title { get; set; }

    public JsonElement Domain { get; set; }

    public JsonElement Kind { get; set; }

    public JsonElement Priority { get; set; }

    public JsonElement Tags { get; set; }

    public JsonElement DueAt { get; set; }

    public JsonElement Notes { get; set; }

    public JsonElement Estimate { get; set; }

    /// <summary>
    /// What the matter costs. Minor units and an ISO 4217 code — the same shape
    /// the client was handed, so a figure never round-trips through a decimal.
    /// </summary>
    public JsonElement Amount { get; set; }

    public JsonElement SourceVoiceNoteId { get; set; }

    /// <summary>
    /// "Check this matter for gaps and ask about them" — sent by the planning
    /// agent's <c>createTask</c> tool, and by nothing else.
    ///
    /// <para>
    /// <b>Why it is opt-in rather than always-on.</b> The same route serves the
    /// app's own Add-a-matter sheet, where a user who left the date empty made that
    /// choice in a form that showed them the field. Asking them about it afterwards
    /// would be the app arguing with a decision it just watched them take. Through
    /// the agent there was no form and no field — the gap is something nobody
    /// mentioned, which is a different thing entirely.
    /// </para>
    /// </summary>
    public JsonElement AskAboutGaps { get; set; }

    /// <summary>
    /// True when the CLOCK TIME on <c>dueAt</c> was chosen by the agent rather than
    /// said by the user. Only meaningful alongside <see cref="AskAboutGaps"/>.
    ///
    /// <para>
    /// Unknowable from the saved row — a 09:00 the user asked for and a 09:00 the
    /// model reached for are the same instant — so the caller reports it, exactly as
    /// the voice extractor reports <c>timeAssumed</c> on its own drafts.
    /// </para>
    /// </summary>
    public JsonElement TimeAssumed { get; set; }

    /// <summary>
    /// The caller's IANA zone, used ONLY to compose the option chips on a gap
    /// question ("Tomorrow — 09:00"). It does not affect <c>dueAt</c>, which is an
    /// absolute instant by the time it reaches here.
    /// </summary>
    public JsonElement Timezone { get; set; }
}

/// <summary>
/// PATCH is where the absent/null distinction earns its keep: an omitted key
/// LEAVES the field alone, an explicit <c>null</c> CLEARS it (<c>$unset</c>).
/// </summary>
public sealed class UpdateTaskBody
{
    public JsonElement Title { get; set; }

    public JsonElement Domain { get; set; }

    public JsonElement Status { get; set; }

    public JsonElement Priority { get; set; }

    public JsonElement Tags { get; set; }

    public JsonElement DueAt { get; set; }

    public JsonElement Notes { get; set; }

    /// <summary>
    /// Null clears the estimate back to "unknown" — which is a real answer, and a
    /// truer one than a number the user does not stand behind.
    /// </summary>
    public JsonElement Estimate { get; set; }

    /// <summary>
    /// Null clears the amount. That matters more here than for most fields: a
    /// matter wrongly carrying a figure is counted in the user's spending, so
    /// "this was never about money" has to be sayable.
    /// </summary>
    public JsonElement Amount { get; set; }

    public JsonElement SnoozedUntil { get; set; }
}

public sealed class AddSubtaskBody
{
    public JsonElement Text { get; set; }
}

public sealed class UpdateSubtaskBody
{
    public JsonElement Text { get; set; }

    public JsonElement Done { get; set; }
}
