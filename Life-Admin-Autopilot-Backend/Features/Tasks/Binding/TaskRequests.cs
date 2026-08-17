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
