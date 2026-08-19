using Life_Admin_Autopilot.DAL.Speech.Models;

namespace Life_Admin_Autopilot.DAL.Speech
{
    /// <summary>
    /// Which transcription providers are worth calling right now, one by one.
    ///
    /// <para>
    /// <b>Not a replacement for AsrAvailability.</b> That one answers a product question -
    /// "should the app offer a microphone at all" - and stays exactly as it is. This one
    /// answers an operational question inside the failover wrapper: "is it worth spending
    /// fifteen seconds discovering that this particular provider is still dry?"
    /// </para>
    ///
    /// <para>
    /// <b>Why the two cannot be one.</b> The moment a fallback exists, a primary with an
    /// exhausted quota stops being visible to the availability breaker: the wrapper falls
    /// through, the fallback succeeds, and the policy layer correctly reports success -
    /// voice DOES work. Without this, that primary is re-tried on every single request
    /// forever, costing a wasted call and its full timeout each time, and no operator ever
    /// learns it is dry.
    /// </para>
    ///
    /// <para>
    /// Same discipline as AsrAvailability, for the same reasons: only PERMANENT codes open
    /// a window (a timeout or a 5xx clears on its own and must not sideline a provider),
    /// the first observation stamps the window rather than the latest one sliding it
    /// forward, any success clears it immediately, and a lapsed window is FORGOTTEN rather
    /// than merely reported healthy so the next failure measures from itself.
    /// </para>
    ///
    /// <para>
    /// Deliberately per-process and in memory. Two API instances briefly disagreeing about
    /// which provider to try first costs one call.
    /// </para>
    /// </summary>
    public sealed class ProviderHealth
    {
        /// <summary>
        /// How long a provider stays sidelined after a permanent failure. Matches
        /// <c>AsrAvailability.CoolOff</c> and for the same reason: someone who tops an
        /// account up should not have to wonder whether the process has noticed.
        /// </summary>
        public static readonly TimeSpan CoolOff = TimeSpan.FromHours(1);

        private readonly object _gate = new();
        private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _now;

        public ProviderHealth()
            : this(() => DateTime.UtcNow)
        {
        }

        /// <summary>The clock, injectable so the cool-off can be tested without waiting an hour.</summary>
        public ProviderHealth(Func<DateTime> now) => _now = now;

        /// <summary>
        /// True when this provider is worth a call. Optimistic for one never seen before:
        /// "we have not tried yet" is not evidence of failure.
        /// </summary>
        public bool IsUsable(string provider)
        {
            lock (_gate)
            {
                if (!_windows.TryGetValue(provider, out var window))
                {
                    return true;
                }

                if (_now() - window.Since < CoolOff)
                {
                    return false;
                }

                // Lapsed. Forget it, so the next failure starts a fresh window instead of
                // measuring from an hour-old observation.
                _windows.Remove(provider);
                return true;
            }
        }

        /// <summary>The code that sidelined a provider, or null while it is usable. Operators only.</summary>
        public string? ReasonFor(string provider)
        {
            lock (_gate)
            {
                return _windows.TryGetValue(provider, out var window) ? window.Reason : null;
            }
        }

        /// <summary>
        /// Record what one provider's attempt turned out to be. Called on EVERY provider
        /// call, which is how this closes a hole the policy layer has on its own: a pinned
        /// script-repair call that fails permanently after a successful detect pass is
        /// discarded up there and never observed at all.
        /// </summary>
        public void Observe(string provider, bool succeeded, string? errorCode)
        {
            lock (_gate)
            {
                if (succeeded)
                {
                    _windows.Remove(provider);
                    return;
                }

                if (!IsPermanent(errorCode))
                {
                    return;
                }

                // NotConfigured is permanent, but sidelining costs more than it saves: a
                // provider with no credentials refuses before it opens a socket, so there
                // is no wasted call to prevent - and hiding it would turn the second
                // request of an unconfigured deployment from an honest ASR_NOT_CONFIGURED
                // into a vague ASR_UNAVAILABLE.
                if (string.Equals(errorCode, SpeechErrorCodes.NotConfigured, StringComparison.Ordinal))
                {
                    return;
                }

                // First observation wins - re-stamping on each subsequent failure would
                // slide the window forward and could hold a provider out indefinitely on a
                // condition fixed forty minutes ago.
                if (!_windows.ContainsKey(provider))
                {
                    _windows[provider] = new Window(_now(), errorCode!);
                }
            }
        }

        /// <summary>
        /// The failures that waiting cannot fix. Identical to <c>AsrAvailability</c>'s list
        /// on purpose: a provider sidelined here for a reason that clears on its own would
        /// be sidelined for an hour over one slow response.
        /// </summary>
        public static bool IsPermanent(string? errorCode) =>
            errorCode is SpeechErrorCodes.QuotaExceeded
                or SpeechErrorCodes.NotAuthorized
                or SpeechErrorCodes.NotConfigured;

        private readonly record struct Window(DateTime Since, string Reason);
    }
}
