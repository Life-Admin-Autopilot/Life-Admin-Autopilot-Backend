using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.DAL.Kernel.Validation;

/// <summary>
/// Field rules whose ORDER OF OPERATIONS is observable on the wire.
///
/// <para>
/// zod applies a chain left to right, so <c>.email().toLowerCase().trim()</c> and
/// <c>.trim().regex(…)</c> behave completely differently — the check runs before
/// the transform in the first case and after it in the second. A "correct"
/// reimplementation that normalises first breaks parity in ways no status-code
/// test catches. All three below are verified against the live Node server.
/// </para>
///
/// <para><b>Slices must use these rather than hand-rolling the equivalent.</b></para>
/// </summary>
public static class NodeFieldRules
{
    private static readonly Regex EmailPattern = new(
        @"^(?!\.)(?!.*\.\.)([A-Z0-9_'+\-\.]*)[A-Z0-9_+-]@([A-Z0-9][A-Z0-9\-]*\.)+[A-Z]{2,}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <c>z.string().email().toLowerCase().trim()</c>.
    ///
    /// <para>
    /// <b>The email check runs FIRST, on the untrimmed value.</b> So
    /// <c>"  a@b.com  "</c> is REJECTED (surrounding whitespace is not a valid
    /// address) while <c>"KerNeL@Probe.COM"</c> is accepted and stored as
    /// <c>"kernel@probe.com"</c>. Both verified live. Trimming before validating —
    /// the "obvious" implementation — would accept the padded form and silently
    /// diverge.
    /// </para>
    /// </summary>
    /// <returns>The normalised address, or null when the raw value is not a valid email.</returns>
    public static string? NormalizeEmail(string? raw)
    {
        if (raw is null || !EmailPattern.IsMatch(raw))
        {
            return null;
        }

        return raw.ToLowerInvariant().Trim();
    }

    /// <summary>Length of the emailed confirmation code. Node: <c>CODE_LENGTH</c>.</summary>
    public const int CodeLength = 6;

    private static readonly Regex CodePattern = new($@"^\d{{{CodeLength}}}$", RegexOptions.Compiled);

    /// <summary>
    /// <c>z.string().trim().regex(/^\d{6}$/)</c>.
    ///
    /// <para>
    /// <b>Trim runs BEFORE the regex here</b>, the opposite of the email rule. So
    /// <c>" 424242 "</c> is ACCEPTED and normalises to <c>"424242"</c>. Verified live.
    /// </para>
    /// </summary>
    /// <returns>The trimmed code, or null when it is not six digits.</returns>
    public static string? NormalizeSixDigitCode(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        return CodePattern.IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// <c>z.string().min(1).max(80).trim()</c>.
    ///
    /// <para>
    /// <b>The length checks run BEFORE the trim.</b> So <c>"   "</c> passes
    /// <c>min(1)</c> — it is three characters — and is then trimmed, meaning the
    /// account stores an EMPTY display name. That is Node's behaviour; do not
    /// "fix" it into a rejection.
    /// </para>
    /// </summary>
    /// <param name="raw">The submitted value.</param>
    /// <param name="normalized">The value to store — possibly empty.</param>
    /// <returns>False only when the UNTRIMMED length is outside [1, 80].</returns>
    public static bool TryNormalizeDisplayName(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (raw is null || raw.Length < 1 || raw.Length > 80)
        {
            return false;
        }

        normalized = raw.Trim();
        return true;
    }

    private static readonly Regex TimeOfDayPattern = new(@"^([01]\d|2[0-3]):([0-5]\d)$", RegexOptions.Compiled);

    /// <summary>
    /// <c>imports.defaultTimeOfDay</c> — 'HH:mm' on a 24-hour clock, nothing else.
    /// Strict on purpose: a malformed value silently becoming midnight fires
    /// reminders at 00:00, which users do not report — they mute notifications.
    /// </summary>
    public static bool IsValidTimeOfDay(string? value) =>
        value is not null && TimeOfDayPattern.IsMatch(value);
}
