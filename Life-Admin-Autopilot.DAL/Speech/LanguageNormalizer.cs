namespace Life_Admin_Autopilot.DAL.Speech
{
    // The provider accepts a fixed set of locale strings and rejects anything else with a
    // 422, so a client locale is mapped onto that set here rather than passed through. The
    // mapping matters most for Arabic: the model offers ar-AR and nothing else, so an
    // Egyptian user's ar-EG has to resolve to it or Arabic silently stops working.
    public static class LanguageNormalizer
    {
        // Exactly the values the provider's schema allows, taken from its own validation
        // error. Ordered so that the first entry for a language is the one a bare language
        // code resolves to (en -> en-US, pt -> pt-BR, no -> nn-NO).
        private static readonly string[] SupportedLocales =
        [
            "en-US", "en-GB", "es-US", "es-ES", "de-DE", "fr-FR", "fr-CA", "it-IT", "ar-AR",
            "ja-JP", "ko-KR", "pt-BR", "pt-PT", "ru-RU", "hi-IN", "zh-CN", "vi-VN", "he-IL",
            "nl-NL", "cs-CZ", "da-DK", "pl-PL", "nn-NO", "nb-NO", "sv-SE", "th-TH", "tr-TR",
            "bg-BG", "el-GR", "et-EE", "fi-FI", "hr-HR", "hu-HU", "lt-LT", "lv-LV", "ro-RO",
            "sk-SK", "uk-UA", "mt-MT", "sl-SI"
        ];

        public const string Auto = "auto";

        // Falls back to "auto" rather than failing: an unrecognised locale should still
        // produce a transcript, just without the detection hint.
        public static string Normalize(string? requested, string fallback = Auto)
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

            var exact = SupportedLocales.FirstOrDefault(
                locale => locale.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            // ar-EG, en-AU, pt-AO and friends: keep the language, take the provider's
            // closest available region rather than giving up on the language entirely.
            var languageCode = value.Split('-')[0];
            var byLanguage = SupportedLocales.FirstOrDefault(
                locale => locale.StartsWith(languageCode + "-", StringComparison.OrdinalIgnoreCase));

            return byLanguage ?? fallback;
        }
    }
}
