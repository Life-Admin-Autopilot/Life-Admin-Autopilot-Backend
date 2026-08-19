namespace Life_Admin_Autopilot.DAL.Speech
{
    /// <summary>
    /// Maps a client locale onto the set of locale strings a provider will actually accept.
    ///
    /// <para>
    /// <b>The algorithm lives here; the vocabulary does not.</b> A provider's locale list is
    /// that provider's own, scraped from its own validation error, and the two we speak to
    /// disagree about the case that matters most: Nemotron offers <c>ar-AR</c> and nothing
    /// else, so an Egyptian user's <c>ar-EG</c> has to collapse onto it or Arabic silently
    /// stops working - while Azure has a real <c>ar-EG</c>, and collapsing it there would
    /// throw away the Egyptian acoustic model for no reason.
    /// </para>
    ///
    /// <para>
    /// So each transport passes its own table (<see cref="NemotronTranscriptionService.SupportedLocales"/>,
    /// <see cref="AzureFastTranscriptionService.SupportedLocales"/>) and the five mapping
    /// rules stay in one place. Copying this class per provider was the alternative, and it
    /// would have meant <c>ar-EG</c> behaving differently by provider for reasons nobody
    /// could reconstruct a year later.
    /// </para>
    /// </summary>
    public static class LanguageNormalizer
    {
        /// <summary>
        /// "Do not pin a language; detect it."
        ///
        /// <para>
        /// A NEUTRAL PROTOCOL SENTINEL, not a provider's locale. It has already leaked out
        /// of this layer - <c>SpeechToTextService</c> sends it as its detect-first signal
        /// and tests incoming locales against it - so every transport is obliged to accept
        /// it and translate it into whatever its own provider calls the same idea.
        /// Nemotron takes the literal string; Azure has no such locale and turns it into a
        /// candidate list instead.
        /// </para>
        /// </summary>
        public const string Auto = "auto";

        /// <summary>
        /// Falls back rather than failing: an unrecognised locale should still produce a
        /// transcript, just without the detection hint.
        /// </summary>
        /// <param name="requested">Whatever the caller sent - a locale, "auto", or nothing.</param>
        /// <param name="supported">
        /// The provider's own accepted values. ORDER IS A BEHAVIOURAL CONTRACT: rule 5
        /// takes the first entry for a language, so <c>en</c> resolving to <c>en-US</c>
        /// rather than <c>en-GB</c> is a consequence of declaration order. Do not
        /// alphabetise a provider table.
        /// </param>
        /// <param name="fallback">What an unmatched locale becomes. Usually <see cref="Auto"/>.</param>
        public static string Normalize(
            string? requested,
            IReadOnlyList<string> supported,
            string fallback = Auto)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                return fallback;
            }

            var value = requested.Trim().Replace('_', '-');

            if (value.Equals(Auto, StringComparison.OrdinalIgnoreCase))
            {
                return Auto;
            }

            var exact = supported.FirstOrDefault(
                locale => locale.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            // ar-EG, en-AU, pt-AO and friends: keep the language, take the provider's
            // closest available region rather than giving up on the language entirely.
            var languageCode = value.Split('-')[0];
            var byLanguage = supported.FirstOrDefault(
                locale => locale.StartsWith(languageCode + "-", StringComparison.OrdinalIgnoreCase));

            return byLanguage ?? fallback;
        }
    }
}
