# Speech-to-text (ASR)

Spoken commands are transcribed by **`nvidia/nemotron-3.5-asr-streaming-0.6b`**, reached
through **Hugging Face's Inference Providers router** on the **fal-ai** route.

> **Provider decision.** SRS §8.4 and `architecture_v3.png` record this as *Azure Speech*.
> **Both need updating.** Azure Speech was never wired up. Two alternatives were built and
> discarded first; the reasoning is kept below because it is the justification for the
> current choice, and because it will otherwise be re-litigated.

## Why this provider

| Candidate | Outcome |
| --- | --- |
| **Nemotron 3.5 ASR via HF/fal** | ✅ What we use. Real ASR, transcribes verbatim, keeps Egyptian dialect. |
| Voxtral on the ITI gateway | ❌ Free and on the approved list, but it is an audio-capable **LLM**, not an ASR. It paraphrased Egyptian Arabic into MSA and, with no language hint, silently **translated it into English**. One test turned "renew the passport" into "go *fetch* the passport" — a wrong task, created silently, with nothing for FR-3.4's confidence gate to catch. |
| `amazon.nova-2-sonic-v1:0` | ❌ Gateway refuses it: `REGION_NOT_ALLOWED`. |
| DeepInfra (direct) | ❌ Same model, but requires a funded account. |

The failure modes differ in kind, which is the point: when Nemotron is wrong it is wrong
*phonetically* (`الإصباح` for `الأسبوع`) — visibly garbled, and the user catches it at the
confirmation step. Voxtral was fluently, confidently wrong.

## ⚠️ Current blocker: credits

The Hugging Face free tier is exhausted. Calls now return:

```
402 — "You have depleted your monthly included credits."
```

The backend handles this cleanly (`ASR_QUOTA_EXCEEDED` → HTTP 503, logged, no crash), and
`/me/capabilities` now reports `transcription: false` so the app stops offering a microphone
it knows it cannot use.

**There is now a second way out: the Azure fallback below.** Either restoring HF credits
*or* configuring an Azure Speech resource makes voice work again — neither is a code change,
and only one of them has to happen.

## Language handling — the part that matters for Arabic

**Auto-detection is weak for Arabic.** With `language` unset, an Egyptian Arabic
recording came back as `Fakarnia gradسبور الإصباح جايز.` — a mix of Latin transliteration
and broken Arabic. With `ar-AR` pinned, the same audio transcribed properly.

**But pinning the LOCALE is worse.** The value the app sends is its active UI locale,
and that describes the interface, not the sentence just spoken. Kitto is bilingual, so
a user reading English chrome and dictating Arabic is ordinary — and pinning that
Arabic to `en-US` makes the provider return an **empty transcript**, which reaches the
user as *"we could not hear anything"* for audio they can hear perfectly. Measured over
four consecutive real recordings: pinned to the UI locale **0/4** usable, auto-detect
**4/4**.

Both facts are true, so `SpeechToTextService` **detects first and pins only to repair**:

1. Always call the provider with `auto`.
2. If that returns *no speech*, retry pinned to the caller's locale — a very short or
   heavily accented clip is where the hint earns its keep.
3. If it returns text with **Latin letters and no Arabic at all** for an `ar` caller,
   retry pinned — that is the transliteration failure above, which is worse than an
   error because it looks like a transcript. If the repair finds nothing, the detected
   transcript is kept rather than discarded.

The common case is therefore **one** inference call, not two, which matters because ASR
is the metered resource here. The `ar` caller who genuinely spoke English costs one
wasted call and still gets the right answer.

So the client should still send the user's locale — as a hint. FR-1.3 already stores
`LocalePreference` on the user, so the value exists.

Nemotron accepts exactly **40** locale strings (plus the `auto` sentinel) and rejects
anything else with a `422`, so
[`LanguageNormalizer`](../Life-Admin-Autopilot.DAL/Speech/LanguageNormalizer.cs) maps client
locales onto that set before the call.

**The rules are shared; the vocabulary is per-provider.** The normalizer holds only the five
mapping rules and takes the table as an argument; each transport owns its own list
(`NemotronTranscriptionService.SupportedLocales`, `AzureFastTranscriptionService.SupportedLocales`).
That split exists because the two providers disagree about the one locale that matters most
here:

| Client sends | → Nemotron | → Azure | Why |
| --- | --- | --- | --- |
| `ar-EG`, `ar`, `ar_EG` | `ar-AR` | `ar-EG` | **Nemotron has no `ar-EG`**, so an Egyptian user's locale must collapse onto `ar-AR` or Arabic stops working entirely. Azure ships a real `ar-EG`, and collapsing it there would throw away the Egyptian acoustic model for nothing. |
| `en`, `en-AU` | `en-US` | `en-US` | Nearest supported region for the language. |
| `xx-XX`, empty | `auto` | `auto` → `["en-US","ar-EG"]` | Unknown locales still transcribe, just without the hint. |

Table **order is a behavioural contract**, not formatting: the "nearest region" rule takes
the FIRST entry for a language, so `en` → `en-US` is a consequence of declaration order. Do
not alphabetise a provider table.

`auto` is a **neutral protocol sentinel**, not any provider's locale — the BLL sends it as
its detect-first signal, so every transport has to accept it and translate it. Nemotron takes
the literal string; Azure has no such value and turns it into a candidate list for language
identification instead.

## Audio format

WAV or MP3. **Stereo 48 kHz passes through fine** — verified against the live provider,
producing an identical transcript to a mono 16 kHz conversion of the same recording. The
backend therefore does **no** audio processing: no resample, no transcode, no re-container,
at any layer including the Azure transport.

⚠️ **That is not a licence to stop converting on the client.** The recorder already emits
16 kHz mono 16-bit PCM (`lib/ai/wavEncoder.ts`), and that is the only reason the 5 MB ceiling
is survivable — a 48 kHz stereo capture is roughly **six times** larger and would fail almost
every recording of usable length. "The provider tolerates it" and "we can afford to send it"
are different claims.

AAC/M4A — Capacitor's recorder default — is **not** accepted, and is rejected up front with
`ASR_UNSUPPORTED_FORMAT`. The recording story must record WAV, or transcode:

```bash
ffmpeg -i recording.m4a -ar 16000 -ac 1 -c:a pcm_s16le command.wav
```

Audio travels as a base64 data URI inside JSON, which inflates it by a third. Uploads are
capped at 5 MB — a voice command should be seconds, not minutes.

## How it fits together

```
mobile app ──(multipart WAV)──► POST /api/speech/transcribe ──► ISpeechToTextService ──► HF router ──► fal ──► Nemotron
                                                                        ▲
                                    /api/planning/propose ──────────────┘
                                    (voice input calls it in-process, no second HTTP hop)
```

`ISpeechToTextService` (BLL) is the seam the rest of the backend transcribes through — the
Planning Agent's `propose` path should inject it directly. `ITranscriptionService` (DAL) is
the transport and the only thing that talks to the provider. Four transports have now been
swapped behind that interface without anything above it changing — and it now holds *two at
once*, see the failover section below.

## The request shape

Neither multipart nor a raw-bytes body works on this route — both are rejected. The audio
must be a **data URI inside JSON**:

```json
{ "audio_url": "data:audio/wav;base64,<...>", "language": "ar-AR" }
```

The response is fal's own shape, **not** the `{"text": ...}` that Hugging Face's task
documentation describes — the router passes the provider's body straight back:

```json
{ "output": "Renew my passport next Friday.", "partial": false }
```

A test asserts this structure so a refactor cannot quietly break it.

## Configuration

```json
"Speech": {
  "TranscriptionUrl": "https://router.huggingface.co/fal-ai/nvidia/nemotron-asr-multilingual/asr",
  "ModelId": "nvidia/nemotron-3.5-asr-streaming-0.6b",
  "DefaultLanguage": "auto",
  "TimeoutSeconds": 30,
  "MaxRetryAttempts": 2,
  "MaxAudioBytes": 5242880
}
```

The token comes from `HF_TOKEN` — never `appsettings.json`. It must be a **fine-grained**
token with *"Make calls to Inference Providers"* permission; a plain read token will not work.

```powershell
dotnet user-secrets set "HF_TOKEN" "hf_..." --project Life-Admin-Autopilot-Backend
```

⚠️ User secrets only load in the **Development** environment. Deployments must supply
`HF_TOKEN` as a real environment variable.

## API

`POST /api/speech/transcribe` — `multipart/form-data`, bearer token required.

| Field | Required | Notes |
| --- | --- | --- |
| `audio` | yes | WAV or MP3. Stereo and any sample rate are fine. |
| `language` | no — **but send it** | The user's locale (`ar-EG`, `en-US`), used as a **repair hint**, not as a claim about the audio. Every request is detected first; see the language section above. |

```bash
curl -X POST https://localhost:7276/api/speech/transcribe \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -F "audio=@command.wav;type=audio/wav" -F "language=ar-EG"
```

```json
{ "succeeded": true, "transcript": "Renew my passport next Friday.",
  "detectedLanguage": "ar-AR", "latencyMs": 1226 }
```

`detectedLanguage` echoes the locale actually **used**, not one the provider detected — it
is `null` when the call ran as `auto`.

## Error handling

No ASR failure reaches the caller as an exception (NFR-8). Failures are logged with the
provider's own wording; the user-facing message is a rewritten, safe one. **The transcript
is never logged** — only its length, timing and language.

| Code | Cause | HTTP |
| --- | --- | --- |
| `ASR_NO_AUDIO` | Nothing uploaded | 400 |
| `ASR_AUDIO_TOO_LARGE` | Over `MaxAudioBytes` | 400 |
| `ASR_UNSUPPORTED_FORMAT` | Not WAV/MP3 (e.g. Capacitor's default AAC) | 400 |
| `ASR_EMPTY_TRANSCRIPT` | Call succeeded, no speech heard | 400 |
| `ASR_INVALID_AUDIO` | Provider rejected the request or the audio | 400 |
| `ASR_TIMEOUT` | No response within `TimeoutSeconds` | 504 |
| `ASR_RATE_LIMITED` | Throttled | 429 |
| `ASR_QUOTA_EXCEEDED` | **Included credits used up** — waiting does not help | 503 |
| `ASR_NOT_AUTHORIZED` | Bad or under-permissioned token | 502 |
| `ASR_UNAVAILABLE` | Provider 5xx | 502 |
| `ASR_NETWORK_ERROR` | Provider unreachable | 502 |
| `ASR_NOT_CONFIGURED` | No `HF_TOKEN` in this environment | 503 |

Only transient faults are retried — twice, 250 ms apart. Quota, auth and bad-audio failures
are never retried because they cannot succeed. A caller cancelling propagates as a
cancellation and is **not** logged as a provider timeout. A `partial: true` response is
returned but logged as a warning: half a command would build a wrong task silently.

## Verification status

Verified end-to-end through `NemotronTranscriptionService` against the live provider:

| Audio | Locale | Result |
| --- | --- | --- |
| English, TTS-generated | auto | ✅ `Renew my passport next Friday.` — word-perfect |
| Egyptian Arabic, `Recording.wav` | `ar-EG`→`ar-AR` | ⚠️ `عندي امتحان ماث الأسبوع الجاي عزب أحضر له.` — dialect preserved, ~1 word wrong in 8 |
| Egyptian Arabic, `myrecording.wav` | `ar-EG`→`ar-AR` | ❌ `فقن جديد الباسبور لإسباق جاي.` — badly garbled |

Latency 1.2–1.7 s, leaving ~3.3 s of NFR-1's 5-second voice-to-task budget.

**Arabic quality is recording-dependent and not yet signed off.** Two samples is not enough
to conclude, and the two disagree sharply. The likely difference is recording quality, which
AC 1 scopes to "a quiet environment" — but that needs confirming with more samples before
Arabic voice input can be called done. English is solid.

**Still to do:**

1. Restore HF credits — nothing works without them.
2. Record 5–10 Egyptian Arabic commands in a quiet room, close to the mic, and measure how
   many transcribe usably.
3. If the failure rate stays high, Azure is already wired as a fallback (below) and has a
   real `ar-EG`. Two claims in the original version of this line were wrong and are worth
   recording: there is **no usable free tier** — fast transcription is Standard (S0) only —
   and it was **not** just the transport class, because `LanguageNormalizer.Auto` had already
   leaked into the BLL as a protocol sentinel.

## Failover: Azure Speech as a second provider

`ITranscriptionService` is answered by
[`FailoverTranscriptionService`](../Life-Admin-Autopilot.DAL/Speech/FailoverTranscriptionService.cs),
which tries one provider and falls through to the other. Nothing above the DAL seam knows it
exists; the BLL's detect-first/pin-second repair and `AsrAvailability` are unchanged.

**Which Azure API.** Fast transcription (`/speechtotext/transcriptions:transcribe`), not the
classic short-audio endpoint — that one caps *directly transmitted* audio at 60 seconds,
which chunked transfer does not raise, and our 5 MB ceiling admits ~2m45s of 16 kHz mono.
Splitting that into three recognition sessions would restart punctuation and sentence
continuity at every boundary. Fast transcription is one synchronous request.

**Configuration.** `AZURE_SPEECH_KEY` and `AZURE_SPEECH_ENDPOINT`, both flat root keys
alongside `HF_TOKEN` — never `appsettings.json`, which is overwritten unconditionally at
startup. Both halves are required; a key with no endpoint reports `ASR_NOT_CONFIGURED`
rather than spending a round trip on a guaranteed 401. `Speech:PrimaryProvider`
(`nemotron` | `azure`) decides which is tried first.

**When it does NOT fail over**, and why each case is deliberate:

| Primary's outcome | Second provider? | Why |
| --- | --- | --- |
| `ASR_EMPTY_TRANSCRIPT` | **never** | Silence is a legitimate answer *and* the policy layer's own retry trigger. Failing over would pre-empt the bilingual repair and break the voice-note worker, which settles a note `ready` on exactly this code. |
| Caller cancelled | **never** | The user walked away. Not worth a second provider's quota. |
| `NO_AUDIO`, `TOO_LARGE`, `UNSUPPORTED_FORMAT`, `INVALID_AUDIO` | no | Client errors; the other provider rejects them identically. |
| Timeout, network, 5xx, rate limit | yes | Transient. |
| Quota, credentials, not configured | yes | Exactly what the fallback exists for. |

**Two things that are easy to get wrong and are pinned by tests.** The wrapper snapshots the
audio to a `byte[]` and hands each provider a fresh stream — the policy layer rewinds only
before *its* calls, so a fallback given the same stream would transcribe zero bytes and
report silence, which looks exactly like a working fallback that heard nothing. And a shared
`Speech:TotalBudgetSeconds` deadline is enforced with a linked token, which each provider's
own cancellation guard would otherwise rethrow as an escaping exception, breaking the "never
throws" contract and skipping `AsrAvailability` entirely.

**`ProviderHealth` vs `AsrAvailability`.** They answer different questions and both are
needed. `AsrAvailability` is the product signal — *should the app offer a microphone at all* —
and is unchanged. `ProviderHealth` sidelines one provider for an hour after a permanent
failure, because once a fallback exists a dry primary becomes invisible: the wrapper falls
through, the fallback succeeds, and the breaker correctly clears. Without it, that dry
primary is re-tried on every single request forever and no operator ever learns.

**Not verified.** No Azure resource exists for this project yet, so the canned response
bodies in `AzureFastTranscriptionServiceTests` are shaped from Microsoft's reference rather
than captured live. Unverified until a real key exists: that 16 kHz mono PCM WAV is accepted
(argued from the *documented absence* of a sample-rate constraint on this API — the 16 kHz
rule people remember belongs to the short-audio endpoint); the exact response shape on
silence; what an exhausted quota returns (429 and 403 map to opposite codes here, so the 403
body is currently sniffed for a quota phrase); and whether `ar-EG` transcribes Egyptian
dialect verbatim or normalises toward MSA — which is the entire reason to prefer Azure, and
which Microsoft documents nowhere.
