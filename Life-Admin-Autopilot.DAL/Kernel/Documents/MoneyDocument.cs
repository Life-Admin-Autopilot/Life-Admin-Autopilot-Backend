namespace Life_Admin_Autopilot.DAL.Kernel.Documents;

/// <summary>
/// The money vocabulary, and the one place a figure becomes storable.
///
/// <para>
/// <b>Minor units, never a decimal.</b> A binary <c>double</c> cannot hold 142.37,
/// and a financial summary is a column of numbers that must add up to what the
/// user can check by hand against the paper. Everything here is a whole count of
/// the currency's smallest unit.
/// </para>
/// </summary>
public static class MoneyVocabulary
{
    /// <summary>
    /// <c>user</c> is authoritative forever — no AI pass may overwrite it. Same
    /// rule and same reason as <c>TaskEstimate.source</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> Sources = new[] { "ai", "user" };

    /// <summary>
    /// Which way the money moved. Defaulting to <c>out</c> is right for the
    /// documents that carry amounts — bills, invoices, receipts — but a refund
    /// or a rebate letter is <c>in</c>, and silently adding one to a spending
    /// total would overstate what the user spent.
    /// </summary>
    public static readonly IReadOnlyList<string> Directions = new[] { "out", "in" };

    /// <summary>
    /// ISO 4217 minor-unit exponents that are NOT 2. Getting this wrong is a
    /// 100x error on a real figure: ¥1,000 stored as if it had cents reads back
    /// as ¥100,000. The long tail all uses 2, so only the exceptions are listed.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> Exponents = new Dictionary<string, int>
    {
        // Zero-decimal currencies.
        ["BIF"] = 0, ["CLP"] = 0, ["DJF"] = 0, ["GNF"] = 0, ["ISK"] = 0,
        ["JPY"] = 0, ["KMF"] = 0, ["KRW"] = 0, ["PYG"] = 0, ["RWF"] = 0,
        ["UGX"] = 0, ["UYI"] = 0, ["VND"] = 0, ["VUV"] = 0, ["XAF"] = 0,
        ["XOF"] = 0, ["XPF"] = 0,

        // Three-decimal currencies.
        ["BHD"] = 3, ["IQD"] = 3, ["JOD"] = 3, ["KWD"] = 3,
        ["LYD"] = 3, ["OMR"] = 3, ["TND"] = 3,
    };

    /// <summary>
    /// One trillion major units — comfortably above any personal document and far
    /// enough below <c>long.MaxValue</c> that scaling by 1000 (the widest minor
    /// unit) still cannot overflow. A figure past it is a parse artefact: a
    /// thousands separator read as a decimal point, or an account number that
    /// landed in the amount slot.
    /// </summary>
    public const decimal MaxMajorUnits = 1_000_000_000_000m;

    public static int ExponentFor(string currency) =>
        Exponents.TryGetValue(currency, out var exponent) ? exponent : 2;

    /// <summary>
    /// A currency code, or null. Deliberately strict: exactly three ASCII
    /// letters, uppercased.
    ///
    /// <para>
    /// Symbols are NOT accepted, and that is the point. <c>$</c> is USD, CAD,
    /// AUD, NZD, SGD, HKD and a dozen more; <c>¥</c> is JPY or CNY. Mapping a
    /// symbol means picking one and being wrong for every user who meant
    /// another, so the extractor asks the model for the ISO code and an
    /// unresolvable currency drops the amount instead.
    /// </para>
    /// </summary>
    public static string? NormalizeCurrency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim().ToUpperInvariant();
        if (trimmed.Length != 3) return null;

        return trimmed.All(c => c is >= 'A' and <= 'Z') ? trimmed : null;
    }

    /// <summary>
    /// The single gate every figure passes through, from any source.
    ///
    /// <para>
    /// Returns null — no money at all — rather than a half-answer, because every
    /// rejection here is a value that would be WRONG on screen: an amount with
    /// no currency is not a quantity of anything, a negative is a direction
    /// dressed as a magnitude, and a figure too large to be a personal document
    /// is a misread decimal separator. A missing row on the summary is
    /// recoverable; a confident wrong number is the failure the trust contract
    /// exists to prevent.
    /// </para>
    /// </summary>
    /// <summary>
    /// The same gate for a figure that ALREADY counts minor units.
    ///
    /// <para>
    /// This is the client's entry point: a matter's amount arrives from the app in
    /// the exact shape the app was handed one, so a figure never makes a lossy
    /// round trip through a decimal on its way back to the server. The client can
    /// do this safely because <c>Intl.NumberFormat</c> gives it the same ISO
    /// exponent table <see cref="ExponentFor"/> uses.
    /// </para>
    ///
    /// <para>
    /// The ceiling is the one difference worth noting: it is applied to the MINOR
    /// count directly, so it is the major-unit cap times the widest minor unit
    /// (three places). Same intent — reject a figure no personal document states.
    /// </para>
    /// </summary>
    public static MoneyDocument? FromMinor(long? amountMinor, string? currency, string source, string? direction = null)
    {
        if (amountMinor is not { } minor) return null;

        var code = NormalizeCurrency(currency);
        if (code is null) return null;

        var magnitude = Math.Abs(minor);
        if (magnitude > (long)MaxMajorUnits * 1000) return null;

        return new MoneyDocument
        {
            AmountMinor = magnitude,
            Currency = code,
            Source = Sources.Contains(source) ? source : "ai",
            Direction = direction is not null && Directions.Contains(direction) ? direction : "out",
        };
    }

    /// <param name="amount">The figure as printed, in MAJOR units (142.37).</param>
    public static MoneyDocument? Normalize(decimal? amount, string? currency, string source, string? direction = null)
    {
        if (amount is not { } value) return null;

        var code = NormalizeCurrency(currency);
        if (code is null) return null;

        // A sign is carried by Direction, never by the magnitude — otherwise the
        // same refund sums differently depending on which field was trusted.
        var magnitude = Math.Abs(value);

        // Bounded BEFORE scaling, not after: multiplying first overflows `decimal`
        // outright on a large enough input and throws, which would turn a garbage
        // figure into a failed scan instead of an ignored field.
        if (magnitude > MaxMajorUnits) return null;

        var scale = (decimal)Math.Pow(10, ExponentFor(code));
        var minor = Math.Round(magnitude * scale, MidpointRounding.AwayFromZero);

        return new MoneyDocument
        {
            AmountMinor = (long)minor,
            Currency = code,
            Source = Sources.Contains(source) ? source : "ai",
            Direction = direction is not null && Directions.Contains(direction) ? direction : "out",
        };
    }
}

/// <summary>
/// One monetary figure attached to a document or a matter.
///
/// <para>
/// Embedded (no <c>_id</c>), like <c>TaskEstimateDocument</c>. Absent on
/// everything created before this feature existed and on every document the
/// reader could not find a figure in, so <b>every surface must render without
/// it</b> — an absent amount is the normal case, not an error.
/// </para>
/// </summary>
public sealed class MoneyDocument
{
    /// <summary>
    /// Whole count of the currency's smallest unit — cents for USD, fils for
    /// KWD, whole yen for JPY. Always non-negative; see <see cref="Direction"/>.
    /// </summary>
    public long AmountMinor { get; set; }

    /// <summary>ISO 4217, uppercase. Never a symbol.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary><c>ai</c> or <c>user</c>. Drives the CitationChip on every surface that renders this.</summary>
    public string Source { get; set; } = "ai";

    /// <summary><c>out</c> (money leaves) or <c>in</c> (a refund or rebate).</summary>
    public string Direction { get; set; } = "out";
}
