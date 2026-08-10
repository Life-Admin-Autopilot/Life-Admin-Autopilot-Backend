using System.Text;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>
/// JavaScript's whitespace rules, which are not .NET's.
///
/// <para>
/// The duplicate detector bins titles on
/// <c>trim().toLowerCase().replace(/\s+/g, ' ')</c>, so whichever characters count
/// as whitespace decides whether two matters are "the same". ECMAScript's
/// <c>WhiteSpace ∪ LineTerminator</c> set and .NET's <c>char.IsWhiteSpace</c>
/// disagree in exactly two places: JS counts <c>U+FEFF</c> (a BOM pasted into a
/// title, which is how this shows up in practice) and .NET does not, while .NET
/// counts <c>U+0085</c> (NEL) and JS does not.
/// </para>
///
/// <para>
/// Two characters is a small disagreement that produces a large one: a title
/// differing only by a stray BOM bins together on Node and separately here, so the
/// same backlog reports a duplicate on one server and not the other.
/// </para>
/// </summary>
internal static class JsText
{
    private const char ByteOrderMark = '\uFEFF';
    private const char NextLine = '\u0085';

    /// <summary>ECMAScript <c>WhiteSpace</c> or <c>LineTerminator</c>.</summary>
    internal static bool IsJsWhiteSpace(char c) => c switch
    {
        ByteOrderMark => true,
        NextLine => false,
        _ => char.IsWhiteSpace(c),
    };

    /// <summary><c>String.prototype.trim()</c>.</summary>
    internal static string Trim(string value)
    {
        var start = 0;
        var end = value.Length;

        while (start < end && IsJsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsJsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary><c>replace(/\s+/g, ' ')</c> — every run of whitespace becomes one space.</summary>
    internal static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var inRun = false;

        foreach (var c in value)
        {
            if (IsJsWhiteSpace(c))
            {
                if (!inRun)
                {
                    builder.Append(' ');
                    inRun = true;
                }

                continue;
            }

            builder.Append(c);
            inRun = false;
        }

        return builder.ToString();
    }
}
