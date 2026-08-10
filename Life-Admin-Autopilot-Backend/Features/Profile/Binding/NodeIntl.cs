using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot_Backend.Features.Profile.Binding;

/// <summary>
/// The two <c>Intl</c> probes <c>routes/me.ts</c> uses as zod refinements.
///
/// <para>
/// The reference validates against the runtime's own tables rather than a
/// hand-kept list, precisely so the check does not go stale the next time a
/// country moves its clock. This class does the same through .NET's ICU-backed
/// APIs. Every accept/reject below was measured against the live reference —
/// see <c>ProfileIntlTests</c>, which pins the whole probed set.
/// </para>
/// </summary>
public static class NodeIntl
{
    /// <summary>
    /// <c>new Intl.DateTimeFormat('en-US', { timeZone: zone })</c>, true when it
    /// does not throw.
    ///
    /// <para>
    /// <b>Two things the obvious implementation gets wrong.</b>
    /// </para>
    ///
    /// <para>
    /// First, <c>FindSystemTimeZoneById</c> alone is too PERMISSIVE: on .NET 6+ it
    /// also resolves Windows identifiers, so <c>"GMT Standard Time"</c> succeeds on
    /// macOS and Linux while the reference rejects it (measured). <c>HasIanaId</c>
    /// is false for exactly those, which is why the check is in two parts. The ICS
    /// slice hit the same trap from the other direction and reached the same
    /// predicate.
    /// </para>
    ///
    /// <para>
    /// Second, it is too RESTRICTIVE: ECMA-402 also accepts a bare UTC offset, and
    /// the reference takes <c>+05</c>, <c>+05:00</c> and <c>-0800</c> (all
    /// measured), none of which is a zone id. <see cref="OffsetPattern"/> covers
    /// them. Note what is NOT accepted: <c>UTC+05:00</c>, a seconds component, and
    /// an out-of-range <c>+24:00</c> or <c>+05:60</c>.
    /// </para>
    /// </summary>
    public static bool IsValidTimezone(string zone)
    {
        if (string.IsNullOrEmpty(zone))
        {
            return false;
        }

        if (OffsetPattern.IsMatch(zone))
        {
            return true;
        }

        if (NotPublishedByIcu.Contains(zone))
        {
            return false;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zone).HasIanaId;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// tzdata entries that .NET resolves from <c>/usr/share/zoneinfo</c> but ICU
    /// does not publish as time zones, so <c>Intl.DateTimeFormat</c> throws on them.
    ///
    /// <para>
    /// <c>Factory</c> is the whole list as measured: it is tzdata's deliberate
    /// "no zone configured" placeholder, present as a file but absent from CLDR.
    /// The other non-zone entries under <c>/usr/share/zoneinfo</c> —
    /// <c>posix/UTC</c>, <c>right/UTC</c>, <c>localtime</c> — are already rejected
    /// by <c>FindSystemTimeZoneById</c>, so they need no entry here.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> NotPublishedByIcu =
        new(StringComparer.OrdinalIgnoreCase) { "Factory" };

    /// <summary>
    /// ECMA-402's <c>UTCOffset</c> forms, hours 00-23 and minutes 00-59:
    /// <c>±HH</c>, <c>±HH:MM</c>, <c>±HHMM</c>. A seconds component is rejected by
    /// the reference's V8 build, so it is rejected here too.
    /// </summary>
    private static readonly Regex OffsetPattern = new(
        @"^[+\-]([01]\d|2[0-3])(:?[0-5]\d)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>Intl.getCanonicalLocales(tag).length &gt; 0</c>, false when it throws.
    ///
    /// <para>
    /// <b>This is a STRUCTURAL check, not a "do we know this language" check</b> —
    /// which is the whole reason it cannot be delegated to
    /// <c>CultureInfo</c>. ECMA-402 asks <c>IsStructurallyValidLanguageTag</c>,
    /// i.e. does the tag match UTS-35 <c>unicode_locale_id</c>. So <c>zz</c>,
    /// <c>qqq</c> and <c>und</c> are all ACCEPTED (well-formed, semantically
    /// unknown) while <c>en_GB</c>, <c>i-klingon</c> and <c>xx-XX-XX</c> are
    /// REJECTED. All six measured. <c>CultureInfo.GetCultureInfo</c> disagrees on
    /// most of them: it normalises the underscore in <c>en_GB</c> and happily mints
    /// custom cultures for names ICU has never heard of.
    /// </para>
    ///
    /// <para>
    /// The route never stores the canonical form — the schema is a
    /// <c>.refine()</c>, not a transform — so <c>"EN-gb"</c> is accepted and stored
    /// verbatim. Only the boolean is needed here.
    /// </para>
    /// </summary>
    public static bool IsValidLocale(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !LocalePattern.IsMatch(tag))
        {
            return false;
        }

        // UTS-35 forbids a repeated variant and a repeated singleton, and
        // getCanonicalLocales throws on both. The grammar alone cannot express it.
        return NoDuplicateSubtags(tag);
    }

    /// <summary>
    /// UTS-35 <c>unicode_locale_id</c>, the grammar ECMA-402 defers to.
    ///
    /// <list type="bullet">
    ///   <item>language: <c>alpha{2,3}</c> or <c>alpha{5,8}</c> — so a one-letter
    ///     primary subtag such as <c>i-klingon</c>'s is not a language at all.</item>
    ///   <item>script: <c>alpha{4}</c>; region: <c>alpha{2}</c> or <c>digit{3}</c>.</item>
    ///   <item>variant: <c>alphanum{5,8}</c> or <c>digit alphanum{3}</c> — which is
    ///     what makes <c>de-DE-1901</c> valid and <c>xx-XX-XX</c> invalid.</item>
    ///   <item>extensions: <c>u-</c>/<c>t-</c>/other singletons, then a private-use
    ///     <c>x-</c> tail.</item>
    /// </list>
    /// </summary>
    private static readonly Regex LocalePattern = new(
        """
        ^
        (?:
          [A-Za-z]{2,3}|[A-Za-z]{5,8}
        )
        (?:-[A-Za-z]{4})?
        (?:-(?:[A-Za-z]{2}|[0-9]{3}))?
        (?:-(?:[A-Za-z0-9]{5,8}|[0-9][A-Za-z0-9]{3}))*
        (?:-[A-Za-wy-zA-WY-Z0-9](?:-[A-Za-z0-9]{2,8})+)*
        (?:-[Xx](?:-[A-Za-z0-9]{1,8})+)?
        $
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private static bool NoDuplicateSubtags(string tag)
    {
        var parts = tag.Split('-');
        var singletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inExtension = false;

        // Index 0 is the language subtag; a one-character subtag can only start an
        // extension, never appear in the language-id part.
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 1)
            {
                if (!singletons.Add(part))
                {
                    return false;
                }

                inExtension = true;
                continue;
            }

            // Variants only exist before the first singleton. Everything after one
            // is extension payload, where repetition is legal.
            if (!inExtension && part.Length >= 4 && !variants.Add(part))
            {
                return false;
            }
        }

        return true;
    }
}
