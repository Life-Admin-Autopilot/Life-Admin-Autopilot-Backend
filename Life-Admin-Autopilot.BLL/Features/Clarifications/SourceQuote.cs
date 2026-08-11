namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// Port of <c>server/src/modules/clarifications/sourceQuote.ts</c> — the user's own
/// words, prepared for storage on a Clarification.
///
/// <para>
/// Two very different inputs land here: a chat message (usually one sentence) and a
/// whole voice-note transcript (one recording can carry six matters, so the same
/// paragraph is quoted by every question it produced). Both are stored verbatim up to
/// a ceiling — the card clamps to a few lines anyway, and an unbounded copy of a
/// five-minute transcript on every row is a lot of Mongo for text nobody reads past
/// the third line.
/// </para>
///
/// <para>
/// It lives in the clarifications slice because that is where the reference puts it
/// and because both writers of the field are clarification writers. The voice slice
/// had its own copy; <c>VoiceClarificationStaging.ClampSourceText</c> now forwards
/// here so the ceiling and the ellipsis cannot drift apart.
/// </para>
/// </summary>
public static class SourceQuote
{
    /// <summary>
    /// <c>MAX_SOURCE_TEXT</c>. Roughly a short paragraph — well past what the card
    /// shows.
    /// </summary>
    public const int MaxSourceText = 600;

    /// <summary>
    /// Trim, cap, and drop the empty case.
    /// </summary>
    /// <returns>
    /// <c>null</c> rather than <c>""</c>, so the field is simply ABSENT on rows with
    /// nothing worth quoting and the card renders exactly as it did before the field
    /// existed.
    /// </returns>
    public static string? Clamp(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxSourceText
            ? trimmed
            : $"{trimmed[..(MaxSourceText - 1)].TrimEnd()}…";
    }
}
