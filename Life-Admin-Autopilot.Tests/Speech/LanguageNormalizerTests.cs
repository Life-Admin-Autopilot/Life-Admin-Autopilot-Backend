using Life_Admin_Autopilot.DAL.Speech;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class LanguageNormalizerTests
    {
        // The algorithm is shared; the vocabulary is not. Every case below names which
        // provider's table it is asserting against, because the two disagree about the
        // locale that matters most to this product.
        private static readonly IReadOnlyList<string> Nemotron = NemotronTranscriptionService.SupportedLocales;
        private static readonly IReadOnlyList<string> Azure = AzureFastTranscriptionService.SupportedLocales;

        // The provider rejects anything outside its own locale list with a 422, so this
        // mapping is what stands between a client locale and a failed transcription.
        [Theory]
        [InlineData("ar-AR", "ar-AR")]
        [InlineData("en-US", "en-US")]
        [InlineData("ar-ar", "ar-AR")]
        [InlineData("EN-GB", "en-GB")]
        public void KeepsALocaleTheProviderAlreadyAccepts(string requested, string expected) =>
            Assert.Equal(expected, LanguageNormalizer.Normalize(requested, Nemotron));

        // NEMOTRON-SPECIFIC, and still correct for it: that model offers ar-AR and nothing
        // else, so an Egyptian user's locale has to resolve to it or Arabic stops working
        // entirely. This is a fact about the table, not about the rules — see the Azure
        // counter-case below.
        [Theory]
        [InlineData("ar-EG", "ar-AR")]
        [InlineData("ar", "ar-AR")]
        [InlineData("ar_EG", "ar-AR")]
        [InlineData("en", "en-US")]
        [InlineData("en-AU", "en-US")]
        [InlineData("pt", "pt-BR")]
        public void MapsAnUnlistedRegionOntoTheProvidersLocaleForThatLanguage(string requested, string expected) =>
            Assert.Equal(expected, LanguageNormalizer.Normalize(requested, Nemotron));

        /// <summary>
        /// The whole point of splitting the table out of the algorithm.
        ///
        /// <para>
        /// Azure ships a real ar-EG, so collapsing it would throw away the Egyptian
        /// acoustic model for no reason — and Azure has no ar-AR to collapse onto in the
        /// first place. Same five rules, opposite outcome, because the vocabulary differs.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("ar-EG", "ar-EG")]
        [InlineData("ar", "ar-EG")]
        [InlineData("ar_EG", "ar-EG")]
        [InlineData("ar-SA", "ar-SA")]
        [InlineData("ar-AR", "ar-EG")]
        public void PreservesEgyptianArabicWhenTheProviderSupportsIt(string requested, string expected) =>
            Assert.Equal(expected, LanguageNormalizer.Normalize(requested, Azure));

        // An unknown language should still produce a transcript, just without the hint.
        [Theory]
        [InlineData("xx-XX")]
        [InlineData("klingon")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void FallsBackToAutoRatherThanFailing(string? requested)
        {
            Assert.Equal("auto", LanguageNormalizer.Normalize(requested, Nemotron));
            Assert.Equal("auto", LanguageNormalizer.Normalize(requested, Azure));
        }

        [Fact]
        public void PassesAutoThrough()
        {
            Assert.Equal("auto", LanguageNormalizer.Normalize("auto", Nemotron));
            Assert.Equal("auto", LanguageNormalizer.Normalize("auto", Azure));
        }

        [Fact]
        public void UsesTheConfiguredFallbackWhenNothingWasRequested() =>
            Assert.Equal("en-US", LanguageNormalizer.Normalize(null, Nemotron, fallback: "en-US"));

        // Rule 5 takes the FIRST entry for a language, so a table's declaration order is a
        // behavioural contract rather than formatting. Asserted here because the tables now
        // live on the provider classes, where "tidying" them is an easy mistake to make.
        [Fact]
        public void ResolvesABareLanguageCodeToTheFirstMatchingEntry()
        {
            Assert.Equal("en-US", LanguageNormalizer.Normalize("en", Nemotron));
            Assert.Equal("en-US", LanguageNormalizer.Normalize("en", Azure));
            Assert.Equal("ar-AR", LanguageNormalizer.Normalize("ar", Nemotron));
            Assert.Equal("ar-EG", LanguageNormalizer.Normalize("ar", Azure));
        }
    }
}
