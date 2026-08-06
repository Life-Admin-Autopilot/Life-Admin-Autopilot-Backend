# API reference

Every endpoint that exists in the backend today, what it does in plain English, and what
it gives back. Generated from the running app's OpenAPI document, not from memory.

Interactive version while the API is running: **https://localhost:7276/swagger**

---

## Before anything: how auth works

Most endpoints need a **bearer token** — a short-lived JWT proving who you are.

1. `POST /api/auth/register` (or `/login`) → you get an `accessToken` and a `refreshToken`
2. Send the access token on every later request: `Authorization: Bearer <accessToken>`
3. Access tokens expire after **15 minutes**. When one does, call `/api/auth/refresh` with
   your refresh token to get a fresh pair — the user doesn't log in again.
4. Refresh tokens last **7 days** and are single-use: refreshing revokes the old one.

In Swagger, click **Authorize** and paste the access token. Endpoints that need it show a
padlock.

The user's identity always comes from the token, never from the request body. You cannot
act on another user's data by passing their id.

---

## Auth — `/api/auth`

| Endpoint | Auth | What it does |
| --- | --- | --- |
| `POST /register` | No | Creates an account and logs you straight in. |
| `POST /login` | No | Exchanges email + password for tokens. |
| `POST /refresh` | No* | Trades a valid refresh token for a new token pair. |
| `POST /logout` | No* | Revokes a refresh token so it can't be used again. |

\* The refresh token *is* the credential for these two, so no bearer header is needed.

**Register / login**

```json
POST /api/auth/register
{ "email": "user@example.com", "password": "Passw0rd!23" }
```
Password must be at least 8 characters. Email must be unique.

```json
200 OK
{ "accessToken": "eyJ...", "refreshToken": "base64...", "accessTokenExpiresAt": "2026-08-01T18:15:00Z" }
```

Failures return `400` (register) or `401` (login) with a list of reasons.

**Refresh / logout** take `{ "refreshToken": "..." }`. Refresh returns a new pair; logout
returns `204 No Content`, or `404` if the token was already revoked or never existed.

---

## Speech — `/api/speech`

Turns a spoken command into text so the Planning Agent can build a task from it.

| Endpoint | Auth | What it does |
| --- | --- | --- |
| `POST /transcribe` | Yes | Upload an audio recording, get back the words that were said. |

Send it as `multipart/form-data`:

| Field | Required | Notes |
| --- | --- | --- |
| `audio` | Yes | WAV or MP3. Stereo and any sample rate are fine. **Not** AAC/M4A. |
| `language` | No, **but send it** | The user's locale, e.g. `ar-EG` or `en-US`. |

Why `language` matters: left out, the model auto-detects, and auto-detection **fails badly
on Arabic** — it returns Latin transliteration. Send the user's stored `LocalePreference`.

```json
200 OK
{ "succeeded": true, "transcript": "Renew my passport next Friday.",
  "detectedLanguage": "en-US", "audioDurationSeconds": null, "latencyMs": 1398,
  "errorCode": null, "errorMessage": null }
```

On failure you get the **same shape** with `succeeded: false` and an `errorCode`. It never
throws a 500. Full error table in [speech-to-text.md](speech-to-text.md).

Common ones: `400 ASR_UNSUPPORTED_FORMAT` (sent an .m4a), `400 ASR_EMPTY_TRANSCRIPT`
(silence), `503 ASR_QUOTA_EXCEEDED` (provider credits used up), `504 ASR_TIMEOUT`.

---

## Devices — `/api/devices`

Registers which phones belong to a user, so reminders know where to go.

| Endpoint | Auth | What it does |
| --- | --- | --- |
| `POST /register` | Yes | Saves this phone's push token against the logged-in user. |
| `GET /` | Yes | Lists the caller's active devices. |
| `DELETE /` | Yes | Removes a device — call this on logout. |

```json
POST /api/devices/register
{ "token": "<FCM token from the app>", "platform": "Android", "deviceModel": "Pixel 8" }
```

`platform` is `Android` or `Ios`. **Safe to call on every app start** — re-registering an
existing token refreshes it rather than creating a duplicate, which matters because FCM
rotates tokens.

Responses show a **masked** token (`e8Xq7T...9Zk4 (len 152)`) — a full push token is a
capability to notify that device, so it's never echoed back or written to logs.

`DELETE` takes `{ "token": "..." }` and returns `204`, or `404` if that token isn't
registered to you.

---

## Notifications (test) — `/api/notificationstest`

Manual verification that push delivery works on real hardware. Authorized so it can't be
used to push arbitrary text at any device.

| Endpoint | Auth | What it does |
| --- | --- | --- |
| `POST /send-to-me` | Yes | Sends a notification to every device the caller registered. |
| `POST /send-to-token` | Yes | Sends to one specific device token. |

```json
POST /api/notificationstest/send-to-me
{ "title": "Test", "body": "Hello from the backend", "data": { "taskId": "abc" } }
```

Returns a per-device report: how many succeeded, how many failed, and why each failed.
`404` if you have no registered devices; `502` if every send failed — so a test that
reached nobody can't be mistaken for a pass.

---

## Tasks (test) — `/api/usertaskstest`

⚠️ **Temporary scaffolding, not the real API.** No authentication, and `userId` is passed
in by the caller. To be replaced by the proper Task CRUD API (story #31).

| Endpoint | What it does |
| --- | --- |
| `POST /` | Creates a task from a raw JSON body. |
| `GET /{id}` | Fetches one task. |
| `GET /user/{userId}` | Lists all tasks for a user id. |
| `PUT /{id}` | Replaces a task. |
| `DELETE /{id}` | Deletes a task **and every document attached to it**. |

---

## Documents (test) — `/api/documentstest`

⚠️ **Temporary scaffolding, same caveats as above.**

| Endpoint | What it does |
| --- | --- |
| `POST /` | Creates a document record. |
| `GET /{id}` | Fetches one document. |
| `GET /user/{userId}` | Lists a user's documents. |
| `PUT /{id}` | Replaces a document. |
| `DELETE /{id}` | Deletes a document. |

Note `blobUrl` is just a string the caller supplies — **there is no file upload yet**.
Azure Blob Storage (story #18) is not built, so nothing actually stores a file.

---

## Health — `/health`

`GET /health` → `200 Healthy`. No auth. For uptime checks and deployment probes. Not in the
OpenAPI document.

---

## ⚠️ Known security gap

The two `...Test` controllers are **unauthenticated and take a `userId` as a parameter**.
Anyone who can reach the API can read, edit or delete any user's tasks and documents.

That contradicts **NFR-5** (*"A user shall only be able to access their own tasks,
reminders, and documents, enforced at the API layer via UserId"*).

Acceptable while they're local development scaffolding. **They must not reach a deployed
environment.** The fix is stories #31 (Task CRUD API) and #35 (commit endpoint), which take
the user id from the token like `/api/devices` already does.

---

## Not built yet

Planned in SRS §7.1 but absent from the codebase today:

| Endpoint | Purpose | Story |
| --- | --- | --- |
| `POST /api/planning/propose` | The unified entry point — voice, typed text or a document becomes one or more draft tasks, conflict-checked, nothing saved | #30 |
| `POST /api/planning/commit` | Saves a confirmed proposal: task + document + reminder together | #35 |
| `GET/POST/PATCH/DELETE /api/tasks` | Real task CRUD, scoped to the caller | #31 |
| `GET /api/users/me`, `PATCH /api/users/me` | Profile and preferences (locale, theme) | — |
| `POST /api/users/me/avatar` | Profile picture upload | #18 |
| `GET/PATCH /api/reminders` | Reminder listing and rescheduling | #55 |
| `GET /api/briefing/today` | The daily briefing | #49 |
| Copilot Chat endpoint | RAG question-answering over tasks and documents | #83 |

**The most consequential gap:** `/api/planning/propose` doesn't exist, so `/api/speech/transcribe`
currently has no consumer. Transcription works, but nothing yet turns a transcript into a
task. Voice → task is not demonstrable end-to-end until #30 lands.
