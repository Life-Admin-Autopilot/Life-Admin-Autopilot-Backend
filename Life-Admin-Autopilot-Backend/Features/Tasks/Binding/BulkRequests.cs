using System.Text.Json;

namespace Life_Admin_Autopilot_Backend.Features.Tasks.Binding;

/// <summary>
/// The bulk body — <c>z.intersection(BulkTargetSchema, BulkActionSchema)</c>.
///
/// <para>
/// <b>LENIENT, unlike the rest of me.tasks.</b> Neither half carries
/// <c>.strict()</c>, so an unknown key is stripped and the request succeeds —
/// verified live: <c>POST /me/tasks/bulk</c> with a stray key returns 200. That is
/// also WHY the Node route reads <c>label</c> straight off <c>req.body</c> instead
/// of from the parsed result: a plain <c>z.object</c> would have stripped it.
/// </para>
/// </summary>
public sealed class BulkBody
{
    public JsonElement Ids { get; set; }

    public JsonElement Filter { get; set; }

    public JsonElement Action { get; set; }

    /// <summary>Present on <c>snooze</c> only.</summary>
    public JsonElement Until { get; set; }

    /// <summary>Present on <c>setDomain</c> only.</summary>
    public JsonElement Domain { get; set; }

    /// <summary>Present on <c>addTags</c> only.</summary>
    public JsonElement Tags { get; set; }

    /// <summary>
    /// Free text for the run history. Truncated to 240 chars by the route, and
    /// deliberately NOT part of either zod half.
    /// </summary>
    public JsonElement Label { get; set; }
}

/// <summary>
/// <c>BulkTargetSchema</c> on its own — what <c>POST /me/tasks/categorize</c>
/// validates. Also lenient.
/// </summary>
public sealed class BulkTargetBody
{
    public JsonElement Ids { get; set; }

    public JsonElement Filter { get; set; }
}

/// <summary><c>ApplyProposalSchema</c> — <c>.strict()</c>.</summary>
public sealed class ApplyProposalBody
{
    public JsonElement TaskIds { get; set; }
}

/// <summary>
/// <c>TranslateSelectionSchema</c>. Lenient — it carries no <c>.strict()</c>.
/// </summary>
public sealed class TranslateSelectionBody
{
    public JsonElement Locale { get; set; }

    public JsonElement Ids { get; set; }

    public JsonElement Preset { get; set; }
}

/// <summary><c>SearchSchema</c> — <c>.strict()</c>.</summary>
public sealed class SearchBody
{
    public JsonElement Query { get; set; }

    public JsonElement Timezone { get; set; }
}

/// <summary><c>SummarizeSchema</c> — <c>.strict()</c>, plus a <c>to &gt; from</c> refine.</summary>
public sealed class SummarizeBody
{
    public JsonElement From { get; set; }

    public JsonElement To { get; set; }

    public JsonElement Timezone { get; set; }
}

/// <summary><c>EstimateBacklogSchema</c> — <c>.strict()</c>.</summary>
public sealed class EstimateBacklogBody
{
    public JsonElement Limit { get; set; }
}
