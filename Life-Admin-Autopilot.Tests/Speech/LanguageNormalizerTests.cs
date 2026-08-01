using Life_Admin_Autopilot.DAL.Speech;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class LanguageNormalizerTests
    {
        // The provider rejects anything outside its own locale list with a 422, so this
        // mapping is what stands between a client locale and a failed transcription.
        [Theory]
        [InlineData("ar-AR", "ar-AR")]
        [InlineData("en-US", "en-US")]
        [InlineData("ar-ar", "ar-AR")]
        [InlineData("EN-GB", "en-GB")]
        public void KeepsALocaleTheProviderAlreadyAccepts(string requested, string expected) =>
            Assert.Equal(expected, LanguageNormalizer.Normalize(requested));

        // The whole reason this exists: the model offers ar-AR and nothing else, so an
        // Egyptian user's locale has to resolve to it or Arabic stops working entirely.
        [Theory]
        [InlineData("ar-EG", "ar-AR")]
        [InlineData("ar", "ar-AR")]
        [InlineData("ar_EG", "ar-AR")]
        [InlineData("en", "en-US")]
        [InlineData("en-AU", "en-US")]
        [InlineData("pt", "pt-BR")]
        public void MapsAnUnlistedRegionOntoTheProvidersLocaleForThatLanguage(string requested, string expected) =>
            Assert.Equal(expected, LanguageNormalizer.Normalize(requested));

        // An unknown language should still produce a transcript, just without the hint.
        [Theory]
        [InlineData("xx-XX")]
        [InlineData("klingon")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void FallsBackToAutoRatherThanFailing(string? requested) =>
            Assert.Equal("auto", LanguageNormalizer.Normalize(requested));

        [Fact]
        public void PassesAutoThrough() =>
            Assert.Equal("auto", LanguageNormalizer.Normalize("auto"));

        [Fact]
        public void UsesTheConfiguredFallbackWhenNothingWasRequested() =>
            Assert.Equal("en-US", LanguageNormalizer.Normalize(null, fallback: "en-US"));
    }
}
