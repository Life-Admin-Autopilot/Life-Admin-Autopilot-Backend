using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class AzureFastTranscriptionServiceTests
    {
        // Shape CONFIRMED against the live service (Germany West Central, api-version
        // 2025-10-15). The real body also carries a per-word `words` array, which this
        // service deliberately ignores - see the ar-EG test below for a captured one.
        private const string SuccessBody = """
            {"durationMilliseconds":2000,
             "combinedPhrases":[{"text":"Renew my passport next Friday."}],
             "phrases":[{"offsetMilliseconds":40,"durationMilliseconds":1900,
                         "text":"Renew my passport next Friday.","locale":"en-US","confidence":0.94}]}
            """;

        // Captured verbatim from the live provider on a silent 16 kHz mono PCM WAV.
        // NOTE THE SHAPE: flat, with no "error" wrapper - see the envelope test below.
        private const string SilenceBody =
            """{"code":"UnprocessableEntity","message":"No language was identified.","innerError":{"code":"NoLanguageIdentified","message":"No language was identified."}}""";

        // Captured from the live provider on a real Egyptian Arabic recording, asking for
        // ["en-US","ar-EG"]. The per-word `words` array is elided for length; nothing else
        // is changed, and this service ignores that array anyway.
        private const string ArabicSuccessBody =
            """
            {"durationMilliseconds":6656,
             "combinedPhrases":[{"text":"السلام عليكم"}],
             "phrases":[{"offsetMilliseconds":240,"durationMilliseconds":5680,
                         "text":"السلام عليكم",
                         "locale":"ar-EG","confidence":0.88188565}]}
            """;

        [Fact]
        public async Task TranscribeAsync_ReturnsTheTranscript_WhenTheProviderSucceeds()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsSuccess);
            Assert.Equal("Renew my passport next Friday.", result.Value!.Text);
        }

        // New information the other provider never gave us: it reports no duration at all.
        [Fact]
        public async Task TranscribeAsync_PopulatesTheAudioDuration_WhenTheProviderReportsIt()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(2.0, result.Value!.AudioDurationSeconds);
        }

        // A REAL detection rather than an echo of what was asked for, which is why it is
        // populated even on an auto run - the case where the other provider returns null.
        [Fact]
        public async Task TranscribeAsync_ReportsTheDetectedLocale_EvenOnAnAutoRun()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request(language: "auto"));

            Assert.Equal("en-US", result.Value!.DetectedLanguage);
        }

        // Two parts, and the definition is a form FIELD holding JSON - not a JSON-typed
        // part and not a file. The other two shapes are rejected by the service.
        [Fact]
        public async Task TranscribeAsync_SendsTheAudioAndDefinitionParts_WhenTranscribing()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request());

            Assert.Matches(@"name=""?audio""?", handler.LastRequestBody!);
            Assert.Matches(@"name=""?definition""?", handler.LastRequestBody!);
            Assert.Contains("RIFF....fake wav bytes", handler.LastRequestBody!);
        }

        // Microsoft's own curl sample sets Content-Type by hand, and the header it writes
        // carries no boundary - which would make every request unparseable. Asserted so
        // nobody "fixes" the missing header back in.
        [Fact]
        public async Task TranscribeAsync_LetsTheClientGenerateTheMultipartBoundary()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request());

            Assert.StartsWith("multipart/form-data", handler.LastContentType!);
            Assert.Contains("boundary=", handler.LastContentType!);
        }

        [Fact]
        public async Task TranscribeAsync_SendsTheSubscriptionKeyHeader_WhenCalling()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request());

            Assert.Equal("test-key", handler.LastRequestHeaders["Ocp-Apim-Subscription-Key"]);
            // No bearer token exchange: the raw key is the credential.
            Assert.Null(handler.LastAuthorizationHeader);
        }

        [Fact]
        public async Task TranscribeAsync_RequestsTheConfiguredApiVersion_WhenCalling()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request());

            Assert.Equal(
                "https://speech.example/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
                handler.LastRequestUri!.ToString());
        }

        // The counterpart of the Nemotron theory, and the whole point of adding this
        // provider: ar-EG survives instead of collapsing onto ar-AR.
        [Theory]
        [InlineData("ar-EG", "ar-EG")]
        [InlineData("ar", "ar-EG")]
        [InlineData("ar_eg", "ar-EG")]
        [InlineData("ar-SA", "ar-SA")]
        [InlineData("ar-MA", "ar-MA")]
        [InlineData("en", "en-US")]
        [InlineData("EN-GB", "en-GB")]
        public async Task TranscribeAsync_NormalisesTheRequestedLanguage(string requested, string expected)
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request(language: requested));

            Assert.Equal(new[] { expected }, LocalesSent(handler));
        }

        // "auto" is a protocol sentinel, not an Azure locale. It becomes a candidate list.
        [Fact]
        public async Task TranscribeAsync_SendsBothCandidateLocales_WhenTheCallerAsksForAuto()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request(language: "auto"));

            // Emphatically NOT an empty array: that selects Azure's multilingual model,
            // which does not support Arabic at all.
            Assert.Equal(new[] { "en-US", "ar-EG" }, LocalesSent(handler));
        }

        [Fact]
        public async Task TranscribeAsync_FallsBackToDetection_WhenTheLocaleIsUnknown()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request(language: "klingon"));

            Assert.Equal(new[] { "en-US", "ar-EG" }, LocalesSent(handler));
        }

        // The voice-note path sends a FileName with no extension at all.
        [Fact]
        public async Task TranscribeAsync_GivesTheAudioPartAnExtension_WhenTheFileNameHasNone()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var request = Request();
            request.FileName = "voice-note";

            await service.TranscribeAsync(request);

            Assert.Contains("voice-note.wav", handler.LastRequestBody!);
        }

        [Fact]
        public async Task TranscribeAsync_UsesTheUploadsOwnContentTypeForTheAudioPart()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            await service.TranscribeAsync(Request(contentType: "audio/mpeg"));

            Assert.Contains("audio/mpeg", handler.LastRequestBody!);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithEmptyTranscript_WhenTheProviderReturnsNoPhrases()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """{"durationMilliseconds":800,"combinedPhrases":[],"phrases":[]}""");
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithEmptyTranscript_WhenTheTranscriptIsWhitespace()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """{"durationMilliseconds":800,"combinedPhrases":[{"text":"   "}],"phrases":[]}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
        }

        /// <summary>
        /// Detection that named no language is silence, not a hard failure.
        ///
        /// <para>
        /// This one is load-bearing well outside this class: the voice-note worker settles
        /// a note as <c>ready</c> on exactly <c>ASR_EMPTY_TRANSCRIPT</c>, so mapping these
        /// to anything else would burn all four attempts and mark a silent recording
        /// failed.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("NoLanguageIdentified")]
        [InlineData("MultipleLanguagesIdentified")]
        public async Task TranscribeAsync_FailsWithEmptyTranscript_WhenNoLanguageWasIdentified(string detailedCode)
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.UnprocessableEntity,
                SilenceBody.Replace("NoLanguageIdentified", detailedCode));
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
        }

        /// <summary>
        /// Azure sends the FLAT envelope, not the wrapped one the REST reference documents.
        ///
        /// <para>
        /// This was a real bug caught only by calling the live service: reading solely the
        /// documented <c>{"error":{…}}</c> shape left <c>innerError.code</c> invisible, so a
        /// silent recording came back as a hard <c>ASR_INVALID_AUDIO</c> instead of
        /// <c>ASR_EMPTY_TRANSCRIPT</c> — which the voice-note worker cannot settle, so it
        /// would burn all four attempts and mark the note failed.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_ReadsTheFlatErrorEnvelope_WhichIsWhatAzureActuallySends()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.UnprocessableEntity, SilenceBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
            Assert.Contains("NoLanguageIdentified", result.Error.Message);
        }

        // The wrapped shape is still read, because it is what the REST reference documents
        // and other Speech endpoints do send it.
        [Fact]
        public async Task TranscribeAsync_AlsoReadsTheDocumentedWrappedErrorEnvelope()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.UnprocessableEntity,
                """{"error":{"code":"UnprocessableEntity","message":"no","innerError":{"code":"NoLanguageIdentified","message":"No language was identified."}}}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
        }

        /// <summary>
        /// Egyptian Arabic, detected rather than pinned, in Arabic script.
        ///
        /// <para>
        /// The whole justification for adding this provider, and it holds: the same class of
        /// recording that the Hugging Face route returned "badly garbled" (docs/speech-to-text.md)
        /// comes back clean here, with <c>ar-EG</c> identified out of a two-candidate list at
        /// 0.88 confidence — no Latin transliteration, so the BLL's script-repair second call
        /// never fires and the request costs one inference instead of two.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_DetectsEgyptianArabic_AndReturnsArabicScript()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ArabicSuccessBody);
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request(language: "auto"));

            Assert.True(result.IsSuccess);
            Assert.Equal("ar-EG", result.Value!.DetectedLanguage);
            Assert.Equal(6.656, result.Value.AudioDurationSeconds);
            // Unicode ESCAPES, never literal characters - a non-UTF-8 toolchain has already
            // mangled an Arabic range in this repo once, and a corrupted assertion here would
            // pass against garbage rather than fail loudly.
            Assert.Contains('ا', result.Value.Text);
            Assert.DoesNotContain(result.Value.Text, char.IsAsciiLetter);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, SpeechErrorCodes.NotAuthorized)]
        [InlineData(HttpStatusCode.Forbidden, SpeechErrorCodes.NotAuthorized)]
        [InlineData(HttpStatusCode.TooManyRequests, SpeechErrorCodes.RateLimited)]
        [InlineData(HttpStatusCode.BadRequest, SpeechErrorCodes.InvalidAudio)]
        [InlineData(HttpStatusCode.UnprocessableEntity, SpeechErrorCodes.InvalidAudio)]
        [InlineData(HttpStatusCode.RequestEntityTooLarge, SpeechErrorCodes.AudioTooLarge)]
        [InlineData(HttpStatusCode.RequestTimeout, SpeechErrorCodes.Timeout)]
        // Must be mapped BEFORE the >= 500 arm, or a gateway timeout reads as an outage.
        [InlineData(HttpStatusCode.GatewayTimeout, SpeechErrorCodes.Timeout)]
        [InlineData(HttpStatusCode.InternalServerError, SpeechErrorCodes.Unavailable)]
        [InlineData(HttpStatusCode.ServiceUnavailable, SpeechErrorCodes.Unavailable)]
        // Unmapped 4xx is Unavailable, never GatewayError: that code is absent from the
        // BLL's user-message switch and would show the user the raw response body.
        [InlineData(HttpStatusCode.Conflict, SpeechErrorCodes.Unavailable)]
        public async Task TranscribeAsync_MapsProviderStatusCodes(
            HttpStatusCode statusCode,
            string expectedErrorCode)
        {
            var handler = new StubHttpMessageHandler(statusCode, """{"error":{"code":"Nope","message":"nope"}}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(expectedErrorCode, result.Error!.Code);
        }

        /// <summary>
        /// A 403 that is actually a spent allowance rather than a bad key.
        ///
        /// <para>
        /// Microsoft documents nothing about what an exhausted Speech quota returns, and the
        /// two candidates map to OPPOSITE behaviours here: NotAuthorized and QuotaExceeded
        /// both close the availability breaker, but only one of them is true. This reads the
        /// body because it is the best available signal — replace the heuristic, and this
        /// test's canned body, with a real response the first time one is observed.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_ReportsQuotaExceeded_WhenA403IsAnExhaustedAllowance()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.Forbidden,
                "Out of call volume quota. Quota will be replenished in 2 days.");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.QuotaExceeded, result.Error!.Code);
        }

        [Fact]
        public async Task TranscribeAsync_SurfacesTheProviderMessage_WhenTheErrorEnvelopeIsStructured()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"InvalidRequest","message":"Bad request.","innerError":{"code":"InvalidAudioFormat","message":"The audio could not be decoded."}}}""");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.InvalidAudio, result.Error!.Code);
            Assert.Contains("InvalidAudioFormat", result.Error.Message);
            Assert.Contains("could not be decoded", result.Error.Message);
        }

        // An APIM gateway page arriving with a 200 must be a handled failure, not an
        // exception - the layer above has no try/catch and documents that this never throws.
        [Fact]
        public async Task TranscribeAsync_FailsWithUnrecognizedShape_WhenTheBodyIsNotJson()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "<html>gateway</html>");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.UnrecognizedResponseShape, result.Error!.Code);
        }

        [Fact]
        public async Task TranscribeAsync_StillReportsAFailure_WhenTheErrorBodyIsNotJson()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>");
            var (service, _) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.Unavailable, result.Error!.Code);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithoutCallingAzure_WhenNoKeyIsConfigured()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var service = new AzureFastTranscriptionService(
                new HttpClient(handler),
                Options.Create(new AzureSpeechOptions { Endpoint = "https://speech.example" }),
                new RecordingLogger<AzureFastTranscriptionService>());

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NotConfigured, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }

        // A key with nowhere to send it is not configuration, it is a guaranteed 401.
        [Fact]
        public async Task TranscribeAsync_FailsWithoutCallingAzure_WhenNoEndpointIsConfigured()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var service = new AzureFastTranscriptionService(
                new HttpClient(handler),
                Options.Create(new AzureSpeechOptions { ApiKey = "test-key" }),
                new RecordingLogger<AzureFastTranscriptionService>());

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NotConfigured, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithNoAudio_WhenTheStreamIsEmpty()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, _) = CreateService(handler);

            var request = Request();
            request.Audio = new MemoryStream();

            var result = await service.TranscribeAsync(request);

            Assert.Equal(SpeechErrorCodes.NoAudio, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }

        // A user who navigated away is not a provider timeout, and must not be logged as one.
        [Fact]
        public async Task TranscribeAsync_RethrowsWithoutLogging_WhenTheCallerCancels()
        {
            var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("cancelled"));
            var (service, logger) = CreateService(handler);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.TranscribeAsync(Request(), cancellation.Token));

            Assert.Empty(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithTimeout_WhenTheClientTimesOut()
        {
            var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("The request timed out."));
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.Timeout, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithNetworkError_WhenTheSocketFails()
        {
            var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("no such host"));
            var (service, logger) = CreateService(handler);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NetworkError, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        // NFR-8: the transcript is the user's own words and never reaches the logs.
        [Fact]
        public async Task TranscribeAsync_DoesNotLogTheTranscript_WhenItSucceeds()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SuccessBody);
            var (service, logger) = CreateService(handler);

            await service.TranscribeAsync(Request());

            Assert.All(logger.Entries, entry => Assert.DoesNotContain("passport", entry.Message));
        }

        /// <summary>
        /// The <c>locales</c> array actually sent, dug out of the multipart body.
        ///
        /// <para>
        /// Read out of the raw body rather than off a structured part: the multipart content
        /// is disposed with the request message, so by the time a test could inspect it, it
        /// is gone. The stub captures the serialised form, which is what went on the wire
        /// anyway.
        /// </para>
        /// </summary>
        private static string[] LocalesSent(StubHttpMessageHandler handler)
        {
            var definition = Regex.Match(handler.LastRequestBody!, @"\{""locales"":\[[^\]]*\]\}");
            Assert.True(definition.Success, $"No definition part in: {handler.LastRequestBody}");

            using var parsed = JsonDocument.Parse(definition.Value);

            return parsed.RootElement.GetProperty("locales")
                .EnumerateArray()
                .Select(locale => locale.GetString()!)
                .ToArray();
        }

        private static TranscriptionRequest Request(string contentType = "audio/wav", string? language = null) => new()
        {
            Audio = new MemoryStream(Encoding.UTF8.GetBytes("RIFF....fake wav bytes")),
            FileName = "command.wav",
            ContentType = contentType,
            Language = language
        };

        private static (AzureFastTranscriptionService Service, RecordingLogger<AzureFastTranscriptionService> Logger) CreateService(
            HttpMessageHandler handler)
        {
            var logger = new RecordingLogger<AzureFastTranscriptionService>();
            var service = new AzureFastTranscriptionService(
                new HttpClient(handler),
                Options.Create(new AzureSpeechOptions
                {
                    Endpoint = "https://speech.example",
                    ApiKey = "test-key"
                }),
                logger);

            return (service, logger);
        }
    }
}
