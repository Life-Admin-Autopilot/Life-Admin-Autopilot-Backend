using Life_Admin_Autopilot.DAL.Speech.Models;

namespace Life_Admin_Autopilot.BLL.Services;

/// <summary>
/// Whether speech-to-text can actually do anything right now.
///
/// <para>
/// <b>The problem this exists for.</b> The ASR provider's included credits ran out.
/// Every call now answers <c>402</c>, the backend maps it to
/// <c>ASR_QUOTA_EXCEEDED</c> and handles it cleanly — and the app went on offering a
/// microphone, taking a recording, uploading it, telling the user it was safely kept,
/// and then failing it seconds later with a message that named nothing they could
/// act on. The server was behaving correctly and the product was still lying: it
/// asked for something it knew it could not use.
/// </para>
///
/// <para>
/// <b>Why a breaker rather than a probe.</b> <c>/me/capabilities</c> is called on
/// every app open and cannot spend an inference call answering — that would burn the
/// very quota it is asking about, and on an account with none left it would be a
/// wasted round trip on every launch. So nothing is probed: the transcription path
/// reports what it observed, and this holds that observation for a while. The signal
/// is a by-product of work that was happening anyway.
/// </para>
///
/// <para>
/// <b>Only the three permanent failures close it.</b> Quota, credentials and missing
/// configuration are conditions that waiting does not fix — the next hundred
/// recordings fail exactly as the last one did. A timeout, a rate limit or a provider
/// 5xx are NOT reported here on purpose: those clear on their own, often within
/// seconds, and disabling the microphone over one slow response would take the
/// feature away from someone who could have used it.
/// </para>
///
/// <para>
/// <b>It reopens on its own, and it reopens on success.</b> The window exists so that
/// topping the account up does not require a deploy or a restart — the app is back
/// inside an hour at worst. Any successful transcription clears it immediately, which
/// is what makes a wrong close cheap: the first call that works reopens the door.
/// </para>
///
/// <para>
/// Deliberately per-process and in memory. This is a hint that shapes an affordance,
/// not an authorisation decision — every route that matters still enforces for
/// itself, and two API instances briefly disagreeing about whether the microphone is
/// offered costs nothing worth a shared store.
/// </para>
/// </summary>
public sealed class AsrAvailability
{
    /// <summary>
    /// How long an observed permanent failure keeps voice switched off.
    ///
    /// <para>
    /// An hour is chosen against the human loop, not the technical one: someone who
    /// notices voice is off, buys credits and comes back should not have to wonder
    /// whether the app has noticed. Any successful call reopens immediately, so this
    /// is only the ceiling on how long a stale close can last.
    /// </para>
    /// </summary>
    public static readonly TimeSpan CoolOff = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private DateTime? _unavailableSince;
    private string? _reason;

    /// <summary>The clock, injectable so the cool-off can be tested without waiting an hour.</summary>
    private readonly Func<DateTime> _now;

    public AsrAvailability()
        : this(() => DateTime.UtcNow)
    {
    }

    public AsrAvailability(Func<DateTime> now) => _now = now;

    /// <summary>
    /// True when it is worth offering the microphone.
    ///
    /// <para>
    /// Optimistic by default: a process that has never transcribed anything reports
    /// available, because "we have not tried yet" is not evidence of failure and the
    /// alternative would hide the feature from every user for the first call of every
    /// deployment.
    /// </para>
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                if (_unavailableSince is not { } since)
                {
                    return true;
                }

                if (_now() - since < CoolOff)
                {
                    return false;
                }

                // The window has lapsed. Forget, rather than merely reporting
                // available: the next failure should start a fresh window instead of
                // measuring from an hour-old observation.
                _unavailableSince = null;
                _reason = null;
                return true;
            }
        }
    }

    /// <summary>
    /// The code that closed it — <c>ASR_QUOTA_EXCEEDED</c> and friends. For logs and
    /// operator tooling only; it is never sent to a client, which owns its own copy
    /// and its own language.
    /// </summary>
    public string? Reason
    {
        get
        {
            lock (_gate)
            {
                return _unavailableSince is null ? null : _reason;
            }
        }
    }

    /// <summary>
    /// Record what a transcription attempt turned out to be. Safe to call on every
    /// attempt, successful or not.
    /// </summary>
    public void Observe(bool succeeded, string? errorCode)
    {
        lock (_gate)
        {
            if (succeeded)
            {
                _unavailableSince = null;
                _reason = null;
                return;
            }

            if (!IsPermanent(errorCode))
            {
                return;
            }

            // The FIRST observation of a run is the one that counts. Re-stamping on
            // every subsequent failure would slide the window forward with each
            // attempt, so a busy account could hold voice closed indefinitely on a
            // condition that was fixed forty minutes ago.
            _unavailableSince ??= _now();
            _reason ??= errorCode;
        }
    }

    /// <summary>
    /// The failures that waiting cannot fix. Everything else — timeouts, throttling,
    /// provider outages — is transient and deliberately absent.
    /// </summary>
    private static bool IsPermanent(string? errorCode) =>
        errorCode is SpeechErrorCodes.QuotaExceeded
            or SpeechErrorCodes.NotAuthorized
            or SpeechErrorCodes.NotConfigured;
}
