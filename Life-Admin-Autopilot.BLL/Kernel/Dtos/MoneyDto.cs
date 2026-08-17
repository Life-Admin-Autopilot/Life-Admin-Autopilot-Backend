using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.BLL.Kernel.Dtos;

/// <summary>
/// One monetary figure on the wire.
///
/// <para>
/// <b>Minor units and a currency code, never a formatted string.</b> The server
/// does not know the reader's locale — the same figure is "١٬٢٣٤٫٥٦ ج.م" in
/// Arabic and "EGP 1,234.56" in English — so formatting happens on the client
/// through <c>lib/i18n/numberFormat.ts</c>, which already has the locale tag.
/// </para>
///
/// <para>
/// <c>currency</c> also carries the SCALE: the client derives the divisor from
/// <c>Intl.NumberFormat</c>'s own ISO data rather than assuming 100, so JPY
/// (no minor unit) and KWD (three places) render correctly without a second
/// table to keep in sync with the server's.
/// </para>
/// </summary>
public sealed class MoneyDto
{
    [JsonPropertyName("amountMinor")]
    public long AmountMinor { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    /// <summary>
    /// <c>ai</c> or <c>user</c>. The client MUST render a CitationChip on any
    /// figure whose source is <c>ai</c> — an unattributed extracted amount is
    /// the exact trust failure the provenance rule exists to prevent.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "ai";

    /// <summary><c>out</c> (a cost) or <c>in</c> (a refund or rebate).</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; init; } = "out";
}
