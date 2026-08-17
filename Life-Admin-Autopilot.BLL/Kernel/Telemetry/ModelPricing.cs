using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Kernel.Telemetry;

/// <summary>USD per million tokens, the unit every vendor quotes in.</summary>
public sealed record ModelPrice(decimal InputPerMillionUsd, decimal OutputPerMillionUsd);

/// <summary>
/// What a call cost, and whether we actually knew.
/// </summary>
/// <param name="Usd">The figure. Zero when <paramref name="Priced"/> is false.</param>
/// <param name="Priced">
/// False when no price matched the model. <b>The console must show this</b> — an
/// unpriced call silently contributing $0 to a total is exactly how a cost
/// dashboard starts under-reporting without anyone noticing.
/// </param>
public readonly record struct CostEstimate(decimal Usd, bool Priced)
{
    public static readonly CostEstimate Unknown = new(0m, false);
}

/// <summary>
/// The price table, resolved once at startup.
///
/// <para>
/// <b>Matching is longest-prefix, not exact.</b> Vendors append dated and
/// preview suffixes to model ids — <c>gemini-2.5-flash-preview-05-20</c> — and a
/// table keyed on exact ids would silently stop pricing the day a suffix changed.
/// A key of <c>gemini-2.5-flash</c> therefore matches every variant of it, and a
/// more specific key wins when one is configured.
/// </para>
///
/// <para>
/// <b>Defaults are a floor, not an authority.</b> They are the published list
/// prices at the time of writing and they will drift. Override them in
/// configuration — <c>Ai:Pricing:Models:&lt;prefix&gt;:Input</c> and
/// <c>:Output</c> — and reconcile against the real billing export before treating
/// any total here as money rather than as a signal.
/// </para>
/// </summary>
public sealed class ModelPricing
{
    private const decimal PerMillion = 1_000_000m;

    /// <summary>
    /// Published list prices, USD per million tokens, as of August 2026.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ModelPrice> Defaults =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-2.5-pro"] = new(1.25m, 10.00m),
            ["gemini-2.5-flash"] = new(0.30m, 2.50m),
            ["gemini-2.5-flash-lite"] = new(0.10m, 0.40m),
            ["gemini-2.0-flash"] = new(0.10m, 0.40m),
            ["gemini-1.5-flash"] = new(0.075m, 0.30m),
            ["gemini-1.5-pro"] = new(1.25m, 5.00m),
        };

    private readonly IReadOnlyList<KeyValuePair<string, ModelPrice>> _prices;

    public ModelPricing(IReadOnlyDictionary<string, ModelPrice> prices, string? defaultChatModel)
    {
        // Longest key first, so the first match is also the most specific one.
        _prices = prices
            .OrderByDescending(p => p.Key.Length)
            .ToList();

        DefaultChatModel = string.IsNullOrWhiteSpace(defaultChatModel) ? null : defaultChatModel.Trim();
    }

    /// <summary>
    /// Which model to price a chat turn against.
    ///
    /// <para>
    /// <b>Langflow does not report the model its Agent node called.</b> The usage
    /// block carries token counts and nothing else, so the model has to be told to
    /// us. Set <c>Ai:Pricing:DefaultChatModel</c> to whatever the flow is wired to;
    /// leave it unset and chat turns record real token counts with
    /// <c>Priced = false</c>, which is honest rather than convenient.
    /// </para>
    /// </summary>
    public string? DefaultChatModel { get; }

    public static ModelPricing FromConfiguration(IConfiguration configuration)
    {
        var prices = new Dictionary<string, ModelPrice>(Defaults, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configuration.GetSection("Ai:Pricing:Models").GetChildren())
        {
            var input = ReadDecimal(entry["Input"]);
            var output = ReadDecimal(entry["Output"]);

            // A half-configured entry is a typo, not an intention. Overriding one side
            // and silently keeping the default for the other produces a plausible,
            // wrong number — better to ignore the row and stay on a known default.
            if (input is null || output is null)
            {
                continue;
            }

            prices[entry.Key] = new ModelPrice(input.Value, output.Value);
        }

        return new ModelPricing(
            prices,
            configuration["Ai:Pricing:DefaultChatModel"] ?? configuration["AI_PRICING_DEFAULT_CHAT_MODEL"]);
    }

    public ModelPrice? For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var id = model.Trim();

        foreach (var (prefix, price) in _prices)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return price;
            }
        }

        return null;
    }

    /// <summary>
    /// Price one call. Negative counts are clamped rather than rejected — a provider
    /// reporting nonsense should skew a chart, not throw inside a finished turn.
    /// </summary>
    public CostEstimate Estimate(string? model, int inputTokens, int outputTokens)
    {
        var price = For(model);
        if (price is null)
        {
            return CostEstimate.Unknown;
        }

        var input = Math.Max(0, inputTokens);
        var output = Math.Max(0, outputTokens);

        var usd = ((input * price.InputPerMillionUsd) + (output * price.OutputPerMillionUsd)) / PerMillion;

        return new CostEstimate(usd, true);
    }

    private static decimal? ReadDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
}
