using Life_Admin_Autopilot_Backend.Features.Profile.Binding;

namespace Life_Admin_Autopilot.Tests.Features.Profile;

/// <summary>
/// The two <c>Intl</c> refinements in <c>PATCH /me</c>, pinned against values
/// MEASURED on the live Node reference rather than derived from the specs.
///
/// <para>
/// Both probes are places where the plausible .NET implementation silently
/// disagrees with V8 — <c>FindSystemTimeZoneById</c> also resolves Windows ids and
/// the tzdata <c>Factory</c> placeholder, and <c>CultureInfo</c> accepts tags ICU
/// rejects. Each row below is a real request/response pair from <c>:4200</c>.
/// </para>
/// </summary>
public sealed class ProfileIntlTests
{
    [Theory]
    // Ordinary IANA names, including multi-segment ones.
    [InlineData("Africa/Cairo")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Asia/Kolkata")]
    [InlineData("Europe/Kyiv")]
    [InlineData("UTC")]
    [InlineData("Etc/UTC")]
    [InlineData("Etc/GMT+5")]
    [InlineData("Etc/GMT-14")]
    // Case-insensitive: the reference stores whatever case it was sent.
    [InlineData("utc")]
    [InlineData("africa/cairo")]
    [InlineData("AFRICA/CAIRO")]
    [InlineData("aSiA/kOlKaTa")]
    // tzdata "backward" links and abbreviations, all published by ICU.
    [InlineData("GMT")]
    [InlineData("EST")]
    [InlineData("EST5EDT")]
    [InlineData("PST8PDT")]
    [InlineData("CET")]
    [InlineData("US/Eastern")]
    [InlineData("Zulu")]
    [InlineData("Universal")]
    [InlineData("Asia/Calcutta")]
    [InlineData("Mexico/BajaSur")]
    [InlineData("Asia/Chungking")]
    // ECMA-402 UTC-offset identifiers — not zone ids at all.
    [InlineData("+05")]
    [InlineData("+05:00")]
    [InlineData("+0500")]
    [InlineData("-08:00")]
    public void accepts_every_timezone_the_reference_accepts(string zone) =>
        Assert.True(NodeIntl.IsValidTimezone(zone), zone);

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("Africa/Cairo/x")]
    [InlineData("")]
    // Whitespace is not trimmed anywhere in this chain.
    [InlineData("Africa/Cairo ")]
    [InlineData(" Africa/Cairo")]
    // Windows identifiers. FindSystemTimeZoneById resolves these on macOS and Linux,
    // so HasIanaId is the only thing standing between the port and a false accept.
    [InlineData("GMT Standard Time")]
    [InlineData("Pacific Standard Time")]
    [InlineData("W. Europe Standard Time")]
    // In /usr/share/zoneinfo but absent from CLDR, so Intl throws.
    [InlineData("Factory")]
    [InlineData("posix/UTC")]
    [InlineData("right/UTC")]
    [InlineData("Etc/Unknown")]
    [InlineData("Local")]
    [InlineData("localtime")]
    // Offset forms outside the grammar or outside the range.
    [InlineData("UTC+05:00")]
    [InlineData("+05:00:00")]
    [InlineData("+24:00")]
    [InlineData("+05:60")]
    [InlineData("05:00")]
    public void rejects_every_timezone_the_reference_rejects(string zone) =>
        Assert.False(NodeIntl.IsValidTimezone(zone), zone);

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("en-GB")]
    [InlineData("ar-EG")]
    [InlineData("EN-gb")]
    [InlineData("en-GB-u-ca-gregory")]
    // A 4-character variant that starts with a digit is legal UTS-35.
    [InlineData("de-DE-1901")]
    [InlineData("de-DE-1901-1996")]
    [InlineData("en-x-priv")]
    // Structurally well formed, semantically unknown. This is the whole reason
    // CultureInfo cannot stand in: the check is grammar, not a known-culture lookup.
    [InlineData("zz")]
    [InlineData("zz-ZZ")]
    [InlineData("qqq")]
    [InlineData("und")]
    public void accepts_every_locale_the_reference_accepts(string tag) =>
        Assert.True(NodeIntl.IsValidLocale(tag), tag);

    [Theory]
    // The POSIX separator is not BCP 47 — and .NET normalises it away if asked.
    [InlineData("en_GB")]
    // "XX" is two alpha in variant position: too short to be a variant, and the
    // region slot is already taken.
    [InlineData("xx-XX-XX")]
    // A one-letter primary subtag can only be an extension singleton.
    [InlineData("i-klingon")]
    [InlineData("not a locale!!")]
    [InlineData("123")]
    [InlineData("en-")]
    [InlineData("-en")]
    [InlineData("")]
    // Four alpha is the SCRIPT shape, not a language, so "root" has no valid
    // parse as a unicode_locale_id even though UTS-35 names it elsewhere.
    [InlineData("root")]
    public void rejects_every_locale_the_reference_rejects(string tag) =>
        Assert.False(NodeIntl.IsValidLocale(tag), tag);

    [Theory]
    // UTS-35 forbids a repeated variant and a repeated extension singleton;
    // getCanonicalLocales throws on both, and the grammar alone cannot say so.
    // All three measured against the reference.
    [InlineData("de-DE-1901-1901")]
    [InlineData("en-u-ca-gregory-u-nu-latn")]
    [InlineData("en-a-bbb-a-ccc")]
    public void rejects_duplicate_subtags(string tag) =>
        Assert.False(NodeIntl.IsValidLocale(tag), tag);
}
