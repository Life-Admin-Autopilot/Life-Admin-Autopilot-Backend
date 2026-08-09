namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Port of <c>server/src/modules/ai/promptLanguage.ts</c>'s locale catalogue —
/// only the parts the Matters slice needs (the prompt-building half belongs to
/// the AI slice).
/// </summary>
public static class AiLocales
{
    public static readonly IReadOnlyList<string> All = new[] { "en", "ar" };

    public const string Default = "en";

    public static bool IsAiLocale(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Best supported locale for a stored BCP 47 tag. Matches the PRIMARY subtag,
    /// so <c>ar-EG</c>, <c>ar-SA</c> and <c>ar</c> all land on <c>ar</c>, and
    /// anything unknown — or absent, on an account that predates the picker —
    /// falls back to English.
    /// </summary>
    public static string Resolve(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return Default;
        }

        var primary = tag.ToLowerInvariant().Split('-', '_')[0];
        return IsAiLocale(primary) ? primary : Default;
    }
}
