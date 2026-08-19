using System.Text;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Speech
{
    public class FailoverTranscriptionServiceTests
    {
        private const string AudioBytes = "RIFF....fake wav bytes";

        [Fact]
        public async Task TranscribeAsync_DoesNotCallTheFallback_WhenThePrimarySucceeds()
        {
            var primary = StubTranscriptionService.Returning("Renew my passport next Friday.");
            var fallback = StubTranscriptionService.Returning("should never run");
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal("Renew my passport next Friday.", result.Value!.Text);
            Assert.Empty(fallback.Requests);
        }

        /// <summary>
        /// The single most important test in this file.
        ///
        /// <para>
        /// An empty transcript is a legitimate outcome (the user recorded silence) AND the
        /// policy layer's own retry trigger — it answers this code by re-sending pinned to
        /// the user's locale, which is the bilingual repair the product depends on. Failing
        /// over here would double the cost of every silent recording, pre-empt that repair,
        /// and break the voice-note worker, which settles a note as <c>ready</c> on exactly
        /// this code.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_DoesNotCallTheFallback_WhenThePrimaryReturnsAnEmptyTranscript()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.EmptyTranscript);
            var fallback = StubTranscriptionService.Returning("should never run");
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.EmptyTranscript, result.Error!.Code);
            Assert.Empty(fallback.Requests);
        }

        // The caller's fault. A second provider will reject it identically, and the API
        // layer answers 4xx either way.
        [Theory]
        [InlineData(SpeechErrorCodes.NoAudio)]
        [InlineData(SpeechErrorCodes.AudioTooLarge)]
        [InlineData(SpeechErrorCodes.UnsupportedFormat)]
        [InlineData(SpeechErrorCodes.InvalidAudio)]
        public async Task TranscribeAsync_DoesNotCallTheFallback_WhenThePrimaryReturnsAClientError(string errorCode)
        {
            var primary = StubTranscriptionService.Failing(errorCode);
            var fallback = StubTranscriptionService.Returning("should never run");
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(errorCode, result.Error!.Code);
            Assert.Empty(fallback.Requests);
        }

        [Theory]
        [InlineData(SpeechErrorCodes.QuotaExceeded)]
        [InlineData(SpeechErrorCodes.NotAuthorized)]
        [InlineData(SpeechErrorCodes.NotConfigured)]
        [InlineData(SpeechErrorCodes.Unavailable)]
        [InlineData(SpeechErrorCodes.Timeout)]
        [InlineData(SpeechErrorCodes.NetworkError)]
        [InlineData(SpeechErrorCodes.RateLimited)]
        [InlineData(SpeechErrorCodes.GatewayError)]
        [InlineData(SpeechErrorCodes.UnrecognizedResponseShape)]
        public async Task TranscribeAsync_CallsTheFallback_WhenThePrimaryCannotServe(string errorCode)
        {
            var primary = StubTranscriptionService.Failing(errorCode);
            var fallback = StubTranscriptionService.Returning("Renew my passport next Friday.");
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsSuccess);
            Assert.Equal("Renew my passport next Friday.", result.Value!.Text);
            Assert.Single(fallback.Requests);
        }

        /// <summary>
        /// Without this, "the fallback transcribes zero bytes" ships silently.
        ///
        /// <para>
        /// The policy layer rewinds the buffer only before ITS calls, so the second provider
        /// in one call receives a stream the first one already drained — and would report
        /// silence for perfectly good audio, which looks exactly like a working fallback
        /// that simply heard nothing.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_RewindsTheAudio_BeforeCallingTheFallback()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.QuotaExceeded);
            var fallback = StubTranscriptionService.Returning("heard it");
            var (service, _) = CreateService(primary, fallback);

            await service.TranscribeAsync(Request());

            Assert.Equal(AudioBytes, Encoding.UTF8.GetString(Assert.Single(primary.AudioRead)));
            Assert.Equal(AudioBytes, Encoding.UTF8.GetString(Assert.Single(fallback.AudioRead)));
        }

        // "auto" included. Each transport translates the sentinel into its own vocabulary,
        // and hoisting normalisation up here would give both providers the poorer of the
        // two locale tables.
        [Fact]
        public async Task TranscribeAsync_ForwardsTheRequestedLocaleUntouched()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.Unavailable);
            var fallback = StubTranscriptionService.Returning("heard it");
            var (service, _) = CreateService(primary, fallback);

            await service.TranscribeAsync(Request(language: "ar-EG"));

            Assert.Equal("ar-EG", Assert.Single(primary.Requests).Language);
            Assert.Equal("ar-EG", Assert.Single(fallback.Requests).Language);
        }

        // A user who walked away is not a provider fault, and is not worth a second
        // provider's quota.
        [Fact]
        public async Task TranscribeAsync_DoesNotCallTheFallback_WhenTheCallerCancels()
        {
            var primary = new StubTranscriptionService(_ => throw new OperationCanceledException());
            var fallback = StubTranscriptionService.Returning("should never run");
            var (service, _) = CreateService(primary, fallback);

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.TranscribeAsync(Request(), cancellation.Token));

            Assert.Empty(fallback.Requests);
        }

        // Nothing can serve and neither will self-heal, so the availability breaker SHOULD
        // close - and the reason it records should name the primary, not a downstream
        // symptom.
        [Fact]
        public async Task TranscribeAsync_ReportsThePrimarysCode_WhenBothProvidersFailPermanently()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.QuotaExceeded);
            var fallback = StubTranscriptionService.Failing(SpeechErrorCodes.NotConfigured);
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.QuotaExceeded, result.Error!.Code);
        }

        /// <summary>
        /// A transient failure anywhere means we do NOT know that voice is down — so the
        /// code that comes out must be one the availability breaker ignores. Reporting the
        /// primary's permanent code here would take the microphone away for an hour over a
        /// fallback that might recover in thirty seconds.
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_ReportsTheTransientCode_WhenOneProviderFailsTransiently()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.QuotaExceeded);
            var fallback = StubTranscriptionService.Failing(SpeechErrorCodes.Unavailable);
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.Unavailable, result.Error!.Code);
            Assert.False(ProviderHealth.IsPermanent(result.Error.Code));
        }

        [Fact]
        public async Task TranscribeAsync_ReportsTheTransientCode_WhenThePrimaryFailsTransiently()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.Timeout);
            var fallback = StubTranscriptionService.Failing(SpeechErrorCodes.NotAuthorized);
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.Timeout, result.Error!.Code);
        }

        // SpeechWiringTests asserts exactly this end to end, from an empty configuration.
        [Fact]
        public async Task TranscribeAsync_ReportsNotConfigured_WhenNeitherProviderHasCredentials()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.NotConfigured);
            var fallback = StubTranscriptionService.Failing(SpeechErrorCodes.NotConfigured);
            var (service, _) = CreateService(primary, fallback);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NotConfigured, result.Error!.Code);
        }

        /// <summary>
        /// An unconfigured provider must NOT be sidelined.
        ///
        /// <para>
        /// Sidelining exists to stop wasted calls, and a provider with no credentials
        /// refuses before it opens a socket — there is nothing to save. Hiding it would
        /// turn the second request of an unconfigured deployment from an honest
        /// <c>ASR_NOT_CONFIGURED</c> into a vague <c>ASR_UNAVAILABLE</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_StillReportsNotConfigured_OnASecondCall()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.NotConfigured);
            var fallback = StubTranscriptionService.Failing(SpeechErrorCodes.NotConfigured);
            var (service, _) = CreateService(primary, fallback);

            await service.TranscribeAsync(Request());
            var result = await service.TranscribeAsync(Request());

            Assert.Equal(SpeechErrorCodes.NotConfigured, result.Error!.Code);
        }

        /// <summary>
        /// A dry quota should cost one wasted call, not one per request for an hour.
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_SkipsAProvider_WhileItsPermanentFailureWindowIsOpen()
        {
            var now = new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc);
            var health = new ProviderHealth(() => now);

            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.QuotaExceeded);
            var fallback = StubTranscriptionService.Returning("heard it");
            var (service, _) = CreateService(primary, fallback, health);

            await service.TranscribeAsync(Request());
            Assert.Single(primary.Requests);

            // Second call within the window: the primary is not tried at all.
            await service.TranscribeAsync(Request());
            Assert.Single(primary.Requests);

            // Past the cool-off, it gets another chance - topping an account up must not
            // require a restart.
            now = now.Add(ProviderHealth.CoolOff).AddMinutes(1);
            await service.TranscribeAsync(Request());
            Assert.Equal(2, primary.Requests.Count);
        }

        // Every provider sidelined: the reason has to survive, or the microphone stays on
        // offer for a deployment where nothing can transcribe.
        [Fact]
        public async Task TranscribeAsync_ReportsTheSideliningReason_WhenEveryProviderIsSkipped()
        {
            var now = new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc);
            var health = new ProviderHealth(() => now);

            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.QuotaExceeded);
            var fallback = StubTranscriptionService.Failing(SpeechErrorCodes.NotAuthorized);
            var (service, _) = CreateService(primary, fallback, health);

            await service.TranscribeAsync(Request());
            var result = await service.TranscribeAsync(Request());

            Assert.Empty(primary.Requests.Skip(1));
            Assert.Equal(SpeechErrorCodes.QuotaExceeded, result.Error!.Code);
        }

        // A transient failure must not sideline anything: it clears on its own, often in
        // seconds, and an hour's exile over one slow response is the wrong trade.
        [Fact]
        public async Task TranscribeAsync_DoesNotSidelineAProvider_AfterATransientFailure()
        {
            var primary = StubTranscriptionService.Failing(SpeechErrorCodes.Timeout);
            var fallback = StubTranscriptionService.Returning("heard it");
            var (service, _) = CreateService(primary, fallback);

            await service.TranscribeAsync(Request());
            await service.TranscribeAsync(Request());

            Assert.Equal(2, primary.Requests.Count);
        }

        [Fact]
        public async Task TranscribeAsync_ClearsTheWindow_WhenAProviderRecovers()
        {
            var health = new ProviderHealth();
            var primary = StubTranscriptionService.Sequence(
                _ => Result<TranscriptionResult>.Failure(new Error(SpeechErrorCodes.QuotaExceeded, "dry")),
                _ => StubTranscriptionService.Heard("topped up"));
            var fallback = StubTranscriptionService.Returning("heard it");
            var (service, _) = CreateService(primary, fallback, health);

            await service.TranscribeAsync(Request());
            Assert.False(health.IsUsable(FailoverTranscriptionService.Nemotron));

            // Simulating the operator topping the account up: the window is cleared by any
            // success, which is what makes a wrong close cheap.
            health.Observe(FailoverTranscriptionService.Nemotron, succeeded: true, null);

            var result = await service.TranscribeAsync(Request());

            Assert.Equal("topped up", result.Value!.Text);
            Assert.Empty(fallback.Requests.Skip(1));
        }

        [Fact]
        public async Task TranscribeAsync_FailsWithNoAudio_WhenTheStreamIsEmpty()
        {
            var primary = StubTranscriptionService.Returning("should never run");
            var fallback = StubTranscriptionService.Returning("should never run");
            var (service, _) = CreateService(primary, fallback);

            var request = Request();
            request.Audio = new MemoryStream();

            var result = await service.TranscribeAsync(request);

            Assert.Equal(SpeechErrorCodes.NoAudio, result.Error!.Code);
            Assert.Empty(primary.Requests);
            Assert.Empty(fallback.Requests);
        }

        /// <summary>
        /// The linked-token trap, pinned.
        ///
        /// <para>
        /// Each provider rethrows on cancellation when its own token is cancelled — correct
        /// on its own terms, but the token it sees is the shared-budget one, so a budget
        /// expiry would escape as an exception, break the documented "never throws"
        /// contract and skip <c>AsrAvailability.Observe</c> entirely.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TranscribeAsync_FailsWithTimeout_WhenTheSharedBudgetExpires()
        {
            var primary = new HangingTranscriptionService();
            var fallback = StubTranscriptionService.Returning("should never run");
            var (service, _) = CreateService(primary, fallback, budgetSeconds: 1);

            var result = await service.TranscribeAsync(Request());

            Assert.True(result.IsFailure);
            Assert.Equal(SpeechErrorCodes.Timeout, result.Error!.Code);
            // The budget is spent, so there is nothing left for a second provider.
            Assert.Empty(fallback.Requests);
        }

        [Theory]
        [InlineData("nemotron", "nemotron")]
        [InlineData("azure", "azure")]
        [InlineData("AZURE", "azure")]
        // An unknown or absent name must not reorder anything - a typo in configuration
        // should not silently change which provider is billed first.
        [InlineData("typo", "nemotron")]
        [InlineData(null, "nemotron")]
        public void InPreferenceOrder_PutsTheConfiguredPrimaryFirst(string? configured, string expected)
        {
            var order = FailoverTranscriptionService.InPreferenceOrder(
                configured,
                new TranscriptionProvider("nemotron", StubTranscriptionService.Returning("a")),
                new TranscriptionProvider("azure", StubTranscriptionService.Returning("b")));

            Assert.Equal(expected, order[0].Name);
            Assert.Equal(2, order.Count);
        }

        private static TranscriptionRequest Request(string? language = null) => new()
        {
            Audio = new MemoryStream(Encoding.UTF8.GetBytes(AudioBytes)),
            FileName = "command.wav",
            ContentType = "audio/wav",
            Language = language
        };

        private static (FailoverTranscriptionService Service, RecordingLogger<FailoverTranscriptionService> Logger) CreateService(
            ITranscriptionService primary,
            ITranscriptionService fallback,
            ProviderHealth? health = null,
            int budgetSeconds = 25)
        {
            var logger = new RecordingLogger<FailoverTranscriptionService>();
            var service = new FailoverTranscriptionService(
                [
                    new TranscriptionProvider(FailoverTranscriptionService.Nemotron, primary),
                    new TranscriptionProvider(FailoverTranscriptionService.Azure, fallback)
                ],
                health ?? new ProviderHealth(),
                Options.Create(new SpeechOptions { TotalBudgetSeconds = budgetSeconds }),
                logger);

            return (service, logger);
        }

        /// <summary>
        /// A provider that never answers, and that guards cancellation exactly the way both
        /// real transports do.
        /// </summary>
        private sealed class HangingTranscriptionService : ITranscriptionService
        {
            public async Task<Result<TranscriptionResult>> TranscribeAsync(
                TranscriptionRequest request,
                CancellationToken cancellationToken = default)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return Result<TranscriptionResult>.Failure(new Error(SpeechErrorCodes.Unavailable, "unreachable"));
            }
        }
    }
}
