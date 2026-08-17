using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The gate every figure passes through. Each test here is a wrong number that
/// would otherwise reach the screen looking exactly as confident as a right one.
/// </summary>
public sealed class MoneyVocabularyTests
{
    [Fact]
    public void a_two_decimal_currency_stores_cents()
    {
        var money = MoneyVocabulary.Normalize(142.37m, "USD", "ai");

        Assert.NotNull(money);
        Assert.Equal(14237, money.AmountMinor);
        Assert.Equal("USD", money.Currency);
    }

    [Fact]
    public void a_zero_decimal_currency_is_not_multiplied_by_a_hundred()
    {
        // ¥1,000 has no minor unit. Storing it as if it did reads back as ¥100,000
        // — a 100x error on a real figure in a currency millions of people use.
        var money = MoneyVocabulary.Normalize(1000m, "JPY", "ai");

        Assert.NotNull(money);
        Assert.Equal(1000, money.AmountMinor);
    }

    [Fact]
    public void a_three_decimal_currency_stores_thousandths()
    {
        // KWD is fils — 1/1000. The same error in the other direction.
        var money = MoneyVocabulary.Normalize(1.234m, "KWD", "ai");

        Assert.NotNull(money);
        Assert.Equal(1234, money.AmountMinor);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("USD$")]
    [InlineData("dollars")]
    [InlineData("US")]
    [InlineData("")]
    [InlineData(null)]
    public void an_amount_whose_currency_cannot_be_resolved_is_dropped_entirely(string? currency)
    {
        // "$" is USD, CAD, AUD, SGD and a dozen more. Picking one is being wrong
        // for everyone who meant another, and a figure in the wrong currency is
        // worse than no figure — the user cannot even tell it is wrong.
        Assert.Null(MoneyVocabulary.Normalize(142.37m, currency, "ai"));
    }

    [Fact]
    public void a_lowercase_code_is_accepted_and_normalized()
    {
        // The model is asked for ISO 4217 but is not bound by it; casing is a
        // formatting slip, not an ambiguity, so it is corrected rather than dropped.
        var money = MoneyVocabulary.Normalize(10m, "egp", "ai");

        Assert.NotNull(money);
        Assert.Equal("EGP", money.Currency);
    }

    [Fact]
    public void a_negative_figure_becomes_a_magnitude_and_never_a_negative_total()
    {
        // Direction carries the sign. If the magnitude could also be negative, the
        // same refund would sum differently depending on which field was trusted.
        var money = MoneyVocabulary.Normalize(-50m, "USD", "ai", "in");

        Assert.NotNull(money);
        Assert.Equal(5000, money.AmountMinor);
        Assert.Equal("in", money.Direction);
    }

    [Fact]
    public void an_absent_figure_is_not_money()
    {
        Assert.Null(MoneyVocabulary.Normalize(null, "USD", "ai"));
    }

    [Fact]
    public void an_absurd_figure_is_rejected_rather_than_stored()
    {
        // A misread thousands separator or an account number caught in the amount
        // slot. No household document states this.
        Assert.Null(MoneyVocabulary.Normalize(decimal.MaxValue, "USD", "ai"));
    }

    [Fact]
    public void an_unknown_source_or_direction_falls_back_to_the_safe_default()
    {
        var money = MoneyVocabulary.Normalize(10m, "USD", "guessed", "sideways");

        Assert.NotNull(money);
        // 'ai' is the cautious answer: it is the one that earns a CitationChip.
        Assert.Equal("ai", money.Source);
        Assert.Equal("out", money.Direction);
    }

    [Fact]
    public void a_user_entered_figure_keeps_its_source()
    {
        // Load-bearing: 'user' is authoritative forever and no AI pass overwrites it.
        var money = MoneyVocabulary.Normalize(10m, "USD", "user");

        Assert.NotNull(money);
        Assert.Equal("user", money.Source);
    }

    [Fact]
    public void rounding_goes_away_from_zero_at_the_half_cent()
    {
        var money = MoneyVocabulary.Normalize(1.005m, "USD", "ai");

        Assert.NotNull(money);
        Assert.Equal(101, money.AmountMinor);
    }
}
