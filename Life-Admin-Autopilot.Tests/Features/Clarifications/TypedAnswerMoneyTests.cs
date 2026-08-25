using Life_Admin_Autopilot.BLL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.Tests.Features.Clarifications;

/// <summary>
/// The currency a typed figure gets when the answer named none.
///
/// <para>
/// <b>The incident.</b> The card asks "How much is «Pay the electricity bill»?" and
/// the user types <c>850</c> — which is what anyone types. The model returns
/// <c>amount: "850"</c> and no <c>currency</c>, because it is only told to infer
/// EGP from the WORDS "pounds"/"جنيه". <see cref="MoneyVocabulary.Normalize"/> then
/// drops the whole figure, the resolve still answers 200, the question closes, and
/// the matter is left with no money on it and nothing in the money tab. Reproduced
/// against the deployed API on 2026-08-25: <c>850</c> → <c>amount: null</c>, while
/// <c>850 EGP</c> and <c>٨٥٠ جنيه</c> both stored 85000 EGP correctly.
/// </para>
///
/// <para>
/// <b>Why the default lives here and not in MoneyVocabulary.</b> That class drops a
/// currency it cannot resolve on purpose, and
/// <c>an_amount_whose_currency_cannot_be_resolved_is_dropped_entirely</c> pins it:
/// "$" is USD, CAD, AUD and a dozen more, so picking one is being wrong for
/// everyone who meant another. That argument is about an <b>ambiguous</b> code, not
/// an <b>absent</b> one. Every other caller — document scans, planning, the client's
/// own PATCH — reads its currency off a source that ought to state one, and a
/// silent default there would turn a misread foreign invoice into a confident wrong
/// figure. This lane is the only one where silence is the expected input, so it is
/// the only one that fills it.
/// </para>
/// </summary>
public sealed class TypedAnswerMoneyTests
{
    /// <summary>The exact value from the incident.</summary>
    [Fact]
    public void a_bare_figure_is_egyptian_pounds()
    {
        var money = CustomAnswerInterpreter.ToMoney("850", null);

        Assert.NotNull(money);
        Assert.Equal(85000, money!.AmountMinor);
        Assert.Equal("EGP", money.Currency);
    }

    /// <summary>Blank is the same silence as absent — the model can send either.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void a_blank_currency_is_the_same_silence(string currency)
    {
        Assert.Equal("EGP", CustomAnswerInterpreter.ToMoney("850", currency)?.Currency);
    }

    /// <summary>
    /// A stated currency is the user's, and it wins. The default may only fill a
    /// gap — the moment it starts overriding, "850 dollars" quietly becomes pounds.
    /// </summary>
    [Theory]
    [InlineData("USD", 85000L)]
    [InlineData("JPY", 850L)]
    [InlineData("KWD", 850000L)]
    public void a_stated_currency_is_never_overridden(string currency, long minor)
    {
        var money = CustomAnswerInterpreter.ToMoney("850", currency);

        Assert.Equal(currency, money?.Currency);
        Assert.Equal(minor, money?.AmountMinor);
    }

    /// <summary>
    /// The ambiguity rule survives untouched: a symbol that names several currencies
    /// is still dropped rather than resolved to the default. Silence means EGP;
    /// "$" means the model does not know, and neither do we.
    /// </summary>
    [Theory]
    [InlineData("$")]
    [InlineData("dollars")]
    [InlineData("US")]
    public void an_ambiguous_currency_is_still_dropped(string currency)
    {
        Assert.Null(CustomAnswerInterpreter.ToMoney("850", currency));
    }

    /// <summary>
    /// An answer with no figure in it stays no figure. The default is a currency for
    /// an amount that exists, never a reason to invent one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not sure yet")]
    [InlineData("whatever the meter says")]
    public void prose_with_no_figure_is_still_not_money(string? answer)
    {
        Assert.Null(CustomAnswerInterpreter.ToMoney(answer, null));
    }

    /// <summary>
    /// The formats an Egyptian keyboard and a model's echo actually produce — all
    /// of which must survive the default being applied around them.
    /// </summary>
    [Theory]
    [InlineData("1,250", 125000L)]
    [InlineData("١٢٥٠", 125000L)]
    [InlineData("850.50", 85050L)]
    public void the_figure_is_still_read_the_way_people_write_it(string typed, long minor)
    {
        var money = CustomAnswerInterpreter.ToMoney(typed, null);

        Assert.Equal(minor, money?.AmountMinor);
        Assert.Equal("EGP", money?.Currency);
    }
}
