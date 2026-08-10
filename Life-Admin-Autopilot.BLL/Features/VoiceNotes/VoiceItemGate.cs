using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>A <see cref="DraftVoiceItem"/> that has been assigned a stable note-scoped key.</summary>
public sealed record KeyedVoiceItem(string Key, DraftVoiceItem Item);

/// <summary>The three lanes one extraction pass splits into.</summary>
public sealed record VoiceGateResult(
    IReadOnlyList<KeyedVoiceItem> AutoSave,
    IReadOnlyList<KeyedVoiceItem> Review,
    IReadOnlyList<KeyedVoiceItem> Clarify);

/// <summary>
/// Port of <c>modules/ai/voiceCore/gate.ts</c> — the spine of the feature.
///
/// <para>
/// Conservative by product decision: auto-save ONLY when the model is fully sure
/// (<c>high</c> confidence) AND nothing flagged the item (<c>reviewReason</c> is
/// <c>clear</c>). Everything else is held. A wrong auto-saved task is the worst
/// failure mode in a personal app, so the bar is deliberately high — do not
/// "improve" this into a score threshold.
/// </para>
///
/// <para>
/// A clarifiable hold always becomes a QUESTION — never auto-saved, never a silent
/// review item — regardless of the model's confidence. The routing key is the
/// presence of a clarification block, not the confidence bucket.
/// </para>
/// </summary>
public static class VoiceItemGate
{
    /// <summary>
    /// Anything that is not a fully-clear, fully-confident item is held for the
    /// user.
    /// </summary>
    public static bool NeedsReview(DraftVoiceItem item) =>
        item.Confidence != "high" || item.ReviewReason != "clear";

    /// <summary>
    /// Assign a stable note-scoped key, then split into three lanes. The key is
    /// deterministic in <c>(noteId, position, normalised title)</c> so a worker
    /// retry or job reclaim upserts to the SAME Task/Clarification instead of
    /// creating a duplicate.
    /// </summary>
    public static VoiceGateResult GateAndKey(string noteId, IReadOnlyList<DraftVoiceItem> items)
    {
        var autoSave = new List<KeyedVoiceItem>();
        var review = new List<KeyedVoiceItem>();
        var clarify = new List<KeyedVoiceItem>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var keyed = new KeyedVoiceItem(MakeItemKey(noteId, index, item.Title), item);

            if (item.Clarification is not null)
            {
                clarify.Add(keyed);
            }
            else if (NeedsReview(item))
            {
                review.Add(keyed);
            }
            else
            {
                autoSave.Add(keyed);
            }
        }

        return new VoiceGateResult(autoSave, review, clarify);
    }

    /// <summary>
    /// <c>sha256(`${noteId}|${index}|${normalizeTitle(title)}`).hex.slice(0, 24)</c>.
    /// </summary>
    public static string MakeItemKey(string noteId, int index, string title)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{noteId}|{index}|{NormalizeTitle(title)}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..24];
    }

    /// <summary>
    /// Port of <c>normalizeTitle</c> in <c>voiceCore/dedupe.ts</c>: lowercase,
    /// NFKD, every non-letter/non-digit/non-space to a space, whitespace runs
    /// collapsed, trimmed.
    ///
    /// <para>
    /// The lowercase happens BEFORE the NFKD decomposition, matching the JS chain —
    /// reversing them changes the output for a handful of ligatures and would give
    /// the two servers different idempotency keys for the same title.
    /// </para>
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var decomposed = title.ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var spaced = NonAlphanumeric.Replace(decomposed, " ");
        return Whitespace.Replace(spaced, " ").Trim();
    }

    private static readonly Regex NonAlphanumeric =
        new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
}
