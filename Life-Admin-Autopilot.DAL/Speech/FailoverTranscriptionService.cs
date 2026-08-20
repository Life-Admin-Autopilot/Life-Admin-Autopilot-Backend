using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Speech
{
    /// <summary>
    /// The only <see cref="ITranscriptionService"/> the container hands out. Tries one
    /// provider, and on the failures a second provider could actually survive, tries the
    /// other.
    ///
    /// <para>
    /// <b>Nothing above this seam knows it exists.</b> The BLL's detect-first/pin-second
    /// language repair, the validation gate and the availability breaker are unchanged, and
    /// <c>SpeechToTextServiceTests</c> compiling untouched is the acceptance criterion that
    /// the seam held.
    /// </para>
    ///
    /// <para>
    /// <b>Failing over is not free and is not always right.</b> Two providers, each with its
    /// own retry policy, sitting under a policy layer that may call twice and a worker that
    /// may call four times, multiply into a number of requests nobody intended. So this
    /// class is mostly about NOT calling the second provider: on a client error, on
    /// silence, on cancellation, and on a provider already known to be dry.
    /// </para>
    /// </summary>
    public class FailoverTranscriptionService : ITranscriptionService
    {
        public const string Nemotron = "nemotron";
        public const string Azure = "azure";

        private readonly IReadOnlyList<TranscriptionProvider> _providers;
        private readonly ProviderHealth _health;
        private readonly SpeechOptions _options;
        private readonly ILogger<FailoverTranscriptionService> _logger;

        /// <param name="providers">
        /// In the order they should be tried. Already ordered by the composition root, so
        /// this class never has to know which concrete transport is which - which is also
        /// what lets it be tested against stubs rather than against two HTTP services.
        /// </param>
        public FailoverTranscriptionService(
            IReadOnlyList<TranscriptionProvider> providers,
            ProviderHealth health,
            IOptions<SpeechOptions> options,
            ILogger<FailoverTranscriptionService> logger)
        {
            _providers = providers;
            _health = health;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Primary first, then the rest. Which is primary is configuration, because a
        /// Hugging Face account with no credits left turns "fallback" into "the provider,
        /// behind a wasted call on every request".
        /// </summary>
        public static IReadOnlyList<TranscriptionProvider> InPreferenceOrder(
            string? primaryProvider,
            params TranscriptionProvider[] providers)
        {
            var primary = providers.FirstOrDefault(provider =>
                string.Equals(provider.Name, primaryProvider, StringComparison.OrdinalIgnoreCase));

            return primary is null
                ? providers
                : new[] { primary }
                    .Concat(providers.Where(other => !ReferenceEquals(other, primary)))
                    .ToArray();
        }

        public async Task<Result<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            // ONE snapshot, reused. The policy layer rewinds only before ITS calls, so by
            // the time the first provider is done the stream sits at the end - and a
            // fallback handed that stream would faithfully transcribe zero bytes and report
            // silence. Not GetBuffer(): the stored-audio path passes a non-publiclyVisible
            // MemoryStream and that throws.
            byte[] audio;
            using (var snapshot = new MemoryStream())
            {
                await request.Audio.CopyToAsync(snapshot, cancellationToken);
                audio = snapshot.ToArray();
            }

            if (audio.Length == 0)
            {
                return Result<TranscriptionResult>.Failure(
                    new Error(SpeechErrorCodes.NoAudio, "The uploaded audio is empty."));
            }

            // A shared wall-clock ceiling across every provider and every retry underneath
            // them. Tuning retry counts alone cannot bound this - two providers, three
            // attempts each, thirty seconds apiece is three minutes for one recording.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(_options.TotalBudgetSeconds));

            // One entry per provider, in order, whether it was called or skipped. A skipped
            // provider counts as having failed the way it failed last time - which is the
            // literal truth, and what keeps a deployment whose every provider is dry
            // reporting the reason rather than a shrug.
            var failures = new List<Error>();

            foreach (var (name, provider) in _providers)
            {
                // A provider whose quota ran dry an hour ago will do it again. Skipping it
                // is the difference between one wasted call and one per request.
                if (!_health.IsUsable(name))
                {
                    var reason = _health.ReasonFor(name) ?? SpeechErrorCodes.Unavailable;

                    _logger.LogDebug(
                        "Skipping transcription provider {Provider}: sidelined by {ErrorCode}",
                        name,
                        reason);

                    failures.Add(new Error(
                        reason,
                        $"The {name} transcription provider is sidelined after a {reason} failure."));
                    continue;
                }

                var calledAFallback = failures.Count > 0;

                var result = await CallAsync(name, provider, request, audio, budget.Token, cancellationToken);

                if (result.IsSuccess)
                {
                    if (calledAFallback)
                    {
                        _logger.LogInformation(
                            "Transcription recovered on fallback provider {Provider}.",
                            name);
                    }

                    return result;
                }

                failures.Add(result.Error!);

                if (!ShouldFailOver(result.Error!.Code))
                {
                    return result;
                }

                if (budget.IsCancellationRequested)
                {
                    break;
                }
            }

            return Compose(failures);
        }

        /// <summary>
        /// Runs one provider and records what it did, converting a budget expiry into a
        /// handled timeout.
        ///
        /// <para>
        /// <b>The linked-token trap.</b> Each provider guards its own cancellation with
        /// <c>when (cancellationToken.IsCancellationRequested)</c> and rethrows - correct
        /// behaviour, since a caller walking away is not a provider fault. But the token it
        /// sees is the LINKED one, so when the shared budget expires that guard fires, the
        /// exception escapes, the documented "never throws" contract breaks and
        /// <c>AsrAvailability</c> is never told what happened. Branching on the ORIGINAL
        /// caller token is what tells the two apart.
        /// </para>
        /// </summary>
        private async Task<Result<TranscriptionResult>> CallAsync(
            string name,
            ITranscriptionService provider,
            TranscriptionRequest request,
            byte[] audio,
            CancellationToken budgetToken,
            CancellationToken callerToken)
        {
            Result<TranscriptionResult> result;
            try
            {
                result = await provider.TranscribeAsync(
                    new TranscriptionRequest
                    {
                        // A fresh reader over the shared bytes, per provider.
                        Audio = new MemoryStream(audio, writable: false),
                        FileName = request.FileName,
                        ContentType = request.ContentType,
                        // Forwarded UNTOUCHED, "auto" included. Each transport owns the
                        // translation into its own vocabulary; hoisting normalisation up
                        // here would give both providers the poorer of the two locale
                        // tables.
                        Language = request.Language
                    },
                    budgetToken);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                // The user walked away. Not a provider fault, and not worth another
                // provider's quota.
                throw;
            }
            catch (OperationCanceledException)
            {
                result = Result<TranscriptionResult>.Failure(new Error(
                    SpeechErrorCodes.Timeout,
                    $"Transcription did not finish within the {_options.TotalBudgetSeconds}s budget."));
            }

            _health.Observe(name, result.IsSuccess, result.Error?.Code);

            if (result.IsFailure && ProviderHealth.IsPermanent(result.Error!.Code))
            {
                // The ONLY signal an operator gets that this provider is dry while the
                // other one quietly carries the load - the availability breaker will be
                // cleared by the successful fallback, exactly as it should be.
                _logger.LogWarning(
                    "Transcription provider {Provider} failed permanently ({ErrorCode}) and is sidelined for {CoolOffMinutes} minutes.",
                    name,
                    result.Error!.Code,
                    ProviderHealth.CoolOff.TotalMinutes);
            }

            return result;
        }

        /// <summary>
        /// Whether a second provider is worth trying after this failure.
        /// </summary>
        private static bool ShouldFailOver(string errorCode) => errorCode switch
        {
            // NEVER. Silence is a legitimate outcome AND the policy layer's own retry
            // trigger: it answers ASR_EMPTY_TRANSCRIPT by re-sending pinned to the user's
            // locale, which is the bilingual repair this product depends on. Failing over
            // here would double every silent recording's cost, hide that repair, and break
            // the voice-note worker, which settles a note as `ready` on exactly this code.
            SpeechErrorCodes.EmptyTranscript => false,

            // The caller's fault. The second provider will reject it identically, and the
            // API layer is going to answer 4xx either way.
            SpeechErrorCodes.NoAudio
                or SpeechErrorCodes.AudioTooLarge
                or SpeechErrorCodes.UnsupportedFormat
                or SpeechErrorCodes.InvalidAudio => false,

            // An operator pulled the kill switch. It applies to the feature, not to one
            // provider.
            SpeechErrorCodes.FeatureDisabled => false,

            // Everything else is either transient (timeout, network, 5xx, throttling) or
            // exactly what the fallback exists for (quota, credentials, no configuration).
            _ => true
        };

        /// <summary>
        /// The code returned when no provider could serve. What comes out of here is what
        /// <c>AsrAvailability</c> observes, so it decides whether the microphone disappears
        /// for an hour.
        /// </summary>
        private static Result<TranscriptionResult> Compose(IReadOnlyList<Error> failures)
        {
            if (failures.Count == 0)
            {
                // Unreachable while at least one provider is registered, but a wrapper that
                // returns nothing at all would be a null reference two layers up.
                return Result<TranscriptionResult>.Failure(new Error(
                    SpeechErrorCodes.Unavailable,
                    "No transcription provider is currently usable."));
            }

            var first = failures[0];
            var last = failures[^1];

            if (failures.Count == 1)
            {
                return Result<TranscriptionResult>.Failure(first);
            }

            var firstIsPermanent = ProviderHealth.IsPermanent(first.Code);
            var lastIsPermanent = ProviderHealth.IsPermanent(last.Code);

            // Both dead ends: report the PRIMARY's code, so the breaker closes for the hour
            // (correct - nothing can serve and neither will self-heal) and the reason an
            // operator reads names the cause rather than a downstream symptom.
            if (firstIsPermanent && lastIsPermanent)
            {
                return Result<TranscriptionResult>.Failure(first);
            }

            // Otherwise at least one failure was transient, which means we do NOT know that
            // voice is down. Report the transient code - preferring the most recent, as the
            // freshest read on the world - so the breaker stays open and a provider that
            // recovers in thirty seconds is not locked out for an hour.
            return Result<TranscriptionResult>.Failure(lastIsPermanent ? first : last);
        }
    }

    /// <summary>
    /// One transcription transport, and the name it is known by in logs and in
    /// <see cref="ProviderHealth"/>.
    /// </summary>
    public sealed record TranscriptionProvider(string Name, ITranscriptionService Service);
}
