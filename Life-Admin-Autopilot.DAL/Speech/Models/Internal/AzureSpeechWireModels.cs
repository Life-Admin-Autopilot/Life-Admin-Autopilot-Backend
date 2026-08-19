using System.Text.Json;
using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Speech.Models.Internal
{
    // Azure fast transcription takes multipart/form-data with two parts: the audio as a
    // file part, and this - a form FIELD whose value is a JSON string. Not an
    // application/json-typed part, not a file part. Attaching it the other two ways is
    // rejected, which is why it is serialised by hand into a StringContent rather than
    // being handed to JsonContent.Create.
    internal class AzureTranscribeDefinition
    {
        [JsonPropertyName("locales")]
        public string[] Locales { get; set; } = [];
    }

    // {"durationMilliseconds":2000,
    //  "combinedPhrases":[{"text":"Renew my passport next Friday."}],
    //  "phrases":[{"offsetMilliseconds":40,"durationMilliseconds":320,"text":"Renew",
    //              "locale":"en-US","confidence":0.78983736}]}
    internal class AzureTranscribeResult
    {
        [JsonPropertyName("durationMilliseconds")]
        public long? DurationMilliseconds { get; set; }

        // One entry per channel. We never send `channels`, so mono input gives exactly one
        // entry holding the whole transcript - taking [0] would DROP HALF THE AUDIO if
        // channel splitting were ever switched on.
        [JsonPropertyName("combinedPhrases")]
        public List<AzureCombinedPhrase>? CombinedPhrases { get; set; }

        [JsonPropertyName("phrases")]
        public List<AzurePhrase>? Phrases { get; set; }
    }

    internal class AzureCombinedPhrase
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    internal class AzurePhrase
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        // A GENUINE detection, unlike the Hugging Face route's echo of what was asked for.
        [JsonPropertyName("locale")]
        public string? Locale { get; set; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }

    /// <summary>
    /// Azure's failure body, in BOTH the shapes it actually uses.
    ///
    /// <para>
    /// The REST reference documents a wrapped envelope:
    /// <c>{"error":{"code":…,"message":…,"innerError":{…}}}</c>. Fast transcription
    /// does not send that. Captured verbatim from the live service on a silent
    /// recording:
    /// </para>
    /// <code>
    /// {"code":"UnprocessableEntity","message":"No language was identified.",
    ///  "innerError":{"code":"NoLanguageIdentified","message":"No language was identified."}}
    /// </code>
    /// <para>
    /// Flat, with no <c>error</c> wrapper at all. Reading only the documented shape left
    /// <c>innerError.code</c> invisible, so <c>NoLanguageIdentified</c> never mapped to
    /// <c>ASR_EMPTY_TRANSCRIPT</c> and silence surfaced as a hard <c>ASR_INVALID_AUDIO</c> —
    /// which the voice-note worker cannot settle, so it would burn all four attempts and
    /// mark the note failed. Both shapes are read, wrapper first.
    /// </para>
    /// </summary>
    internal class AzureErrorResponse
    {
        [JsonPropertyName("error")]
        public AzureError? Error { get; set; }

        // The flat form: the same three fields, at the root.
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("innerError")]
        public AzureInnerError? InnerError { get; set; }

        private AzureError? Effective =>
            Error ?? (Code is null && Message is null && InnerError is null
                ? null
                : new AzureError { Code = Code, Message = Message, InnerError = InnerError });

        public string? Describe() => Effective?.Describe();

        /// <summary>
        /// The most specific code Azure gave us. The inner one is the useful half:
        /// the outer is usually just the status name ("UnprocessableEntity").
        /// </summary>
        public string? DetailedCode() => Effective is { } error ? error.InnerError?.Code ?? error.Code : null;
    }

    internal class AzureError
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("innerError")]
        public AzureInnerError? InnerError { get; set; }

        public string? Describe()
        {
            var code = InnerError?.Code ?? Code;
            var message = InnerError?.Message ?? Message;

            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
        }
    }

    internal class AzureInnerError
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        // Azure nests innerError arbitrarily deep; we only read two levels, but the
        // property has to exist or the deserializer would not be able to skip past it
        // cleanly on a body that carries one.
        [JsonPropertyName("innerError")]
        public JsonElement? InnerError { get; set; }
    }
}
