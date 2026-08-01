# Speech-to-text (ASR)

Spoken commands are transcribed by **`nvidia/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b`**,
hosted on **DeepInfra**, called over its native inference API.

> **Provider decision.** SRS §8.4 and `architecture_v3.png` still record the ASR/TTS choice
> as *Azure Speech*. The team has since chosen Nemotron on DeepInfra instead — multilingual
> (the app ships English and Arabic) and ~$0.0002/audio-minute. **Those two documents need
> updating to match**, otherwise the SRS and the code disagree. TTS (FR-2.6, a Should-Have)
> is a separate story and is not covered here.

## How it fits together

```
mobile app ──(multipart WAV)──► POST /api/speech/transcribe ──► ISpeechToTextService ──► DeepInfra
                                                                        ▲
                                    /api/planning/propose ──────────────┘
                                    (voice input calls it in-process, no second HTTP hop)
```

`ISpeechToTextService` (BLL) is the seam the rest of the backend transcribes through. The
Planning Agent's `propose` path should inject it directly rather than calling the HTTP
endpoint — the endpoint exists so the client can transcribe on its own and so the flow is
testable by hand.

`ITranscriptionService` (DAL) is the provider transport and is the only thing allowed to
call DeepInfra. Swapping providers means one new implementation of that interface; nothing
above it changes.

## Audio format — read this before recording on the client

The model card requires **mono WAV**. MP3 is accepted by the endpoint and is allowed in
config, but AAC/M4A — **what Capacitor's voice recorder produces by default** — is not.
The backend rejects it up front with `ASR_UNSUPPORTED_FORMAT` rather than letting the
provider fail confusingly.

So the voice recording story must either record WAV directly or transcode before upload.
16 kHz mono 16-bit is the sweet spot for size and accuracy:

```bash
ffmpeg -i recording.m4a -ar 16000 -ac 1 -c:a pcm_s16le command.wav
```

## Configuration

`appsettings.json`:

```json
"Speech": {
  "InferenceBaseUrl": "https://api.deepinfra.com/v1/inference",
  "ModelId": "nvidia/Nemotron-3.5-ASR-Streaming-Multilingual-0.6b",
  "Language": "auto",
  "TimeoutSeconds": 15,
  "MaxRetryAttempts": 2,
  "MaxAudioBytes": 10485760
}
```

`Language: "auto"` lets the model detect English vs Arabic; a request can override it with
a `language` form field (`en`, `ar`).

The token never goes in `appsettings.json` — same convention as `SBG_API_KEY` and the FCM
key:

```powershell
dotnet user-secrets set "DEEPINFRA_TOKEN" "<your DeepInfra token>" --project Life-Admin-Autopilot-Backend
```

Get the token from https://deepinfra.com/dash/api_keys. With no token set the API still
starts and transcription returns `ASR_NOT_CONFIGURED` (HTTP 503) instead of throwing.

## API

`POST /api/speech/transcribe` — `multipart/form-data`, bearer token required.

| Field | Required | Notes |
| --- | --- | --- |
| `audio` | yes | The recording. Mono WAV (or MP3). |
| `language` | no | `en`, `ar`, … Overrides `Speech:Language` for this call. |

```bash
curl -X POST https://localhost:7276/api/speech/transcribe \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -F "audio=@command.wav;type=audio/wav"
```

Success — `200`:

```json
{
  "succeeded": true,
  "transcript": "Renew my passport next Friday",
  "detectedLanguage": "en",
  "audioDurationSeconds": 3.2,
  "latencyMs": 412
}
```

Failure — same envelope with `succeeded: false`, an `errorCode` and a message that is safe
to show the user.

## Error handling

No ASR failure reaches the caller as an exception (NFR-8). Every failure is logged with the
provider's own wording, and the user-facing message is a rewritten, safe one — provider
messages can contain raw HTTP bodies. **The transcript itself is never logged**; only its
length, timing and detected language are.

| Code | Cause | HTTP |
| --- | --- | --- |
| `ASR_NO_AUDIO` | Nothing uploaded | 400 |
| `ASR_AUDIO_TOO_LARGE` | Over `MaxAudioBytes` | 400 |
| `ASR_UNSUPPORTED_FORMAT` | Not WAV/MP3 (e.g. Capacitor's default AAC) | 400 |
| `ASR_EMPTY_TRANSCRIPT` | Call succeeded, no speech heard (silence, dead mic) | 400 |
| `ASR_INVALID_AUDIO` | Provider could not read the file | 400 |
| `ASR_TIMEOUT` | No response within `TimeoutSeconds` | 504 |
| `ASR_RATE_LIMITED` | Provider throttled us | 429 |
| `ASR_NOT_AUTHORIZED` | Bad token, or the account is out of credit | 502 |
| `ASR_UNAVAILABLE` | Provider 5xx | 502 |
| `ASR_NETWORK_ERROR` | Provider unreachable | 502 |
| `ASR_NOT_CONFIGURED` | No `DEEPINFRA_TOKEN` in this environment | 503 |

Only transient faults (5xx, timeouts, network) are retried — twice, 250 ms apart. A user is
waiting, so recovery is deliberately quick-or-not-at-all; the audio is buffered before the
first attempt so a retry can actually re-send it. Bad audio and auth failures are never
retried because they cannot succeed.

A caller cancelling (the user backing out of the screen) propagates as a cancellation and is
**not** logged as a provider timeout.

## Manual test

**AC 1 — a clear sentence in a quiet room transcribes with no major word errors:**

1. Set `DEEPINFRA_TOKEN`, start the API, get an access token from `/api/auth/login`.
2. Record one clear sentence in a quiet room, e.g. *"Renew my passport next Friday."*
   Convert to mono WAV with the `ffmpeg` command above.
3. `curl` it as shown, or use Swagger's file picker on `/api/speech/transcribe`.
4. **Expected:** `200`, `transcript` matches what you said, `latencyMs` comfortably inside
   NFR-1's 5-second budget for the whole voice-to-task chain.

**AC 2 — a failed or timed-out call is handled, not a crash:**

5. Stop the network (or set `InferenceBaseUrl` to an unroutable host) and repeat.
   **Expected:** `502` with `ASR_NETWORK_ERROR` and a warning in the log — no 500, no stack
   trace.
6. Set `"TimeoutSeconds": 1` and send a longer recording. **Expected:** `504` with
   `ASR_TIMEOUT`.
7. Upload an `.m4a` straight from a phone. **Expected:** `400` with
   `ASR_UNSUPPORTED_FORMAT` and a message naming mono WAV.
8. Upload a few seconds of silence. **Expected:** `400` with `ASR_EMPTY_TRANSCRIPT`.
