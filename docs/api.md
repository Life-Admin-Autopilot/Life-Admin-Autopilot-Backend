# API reference

Every endpoint that exists in the backend today, what it does in plain English, and what
it gives back. Generated from the running app's OpenAPI document, not from memory.

Interactive version while the API is running: **https://localhost:7276/swagger**

---

## Before anything: how auth works

Most endpoints need a **bearer token** — a short-lived JWT proving who you are.

1. `POST /auth/signup` (or `/auth/signin`) → you get an `accessToken` and a `refreshToken`
2. Send the access token on every later request: `Authorization: Bearer <accessToken>`
3. When one expires, call `POST /auth/refresh` with your refresh token to get a fresh
   pair — the user doesn't log in again.
4. Refresh tokens are single-use: refreshing revokes the old one.

In Swagger, click **Authorize** and paste the access token. Endpoints that need it show a
padlock.

The user's identity always comes from the token, never from the request body. You cannot
act on another user's data by passing their id.

**The server will not start without a real signing secret.** `Jwt:Key` used to ship as the
literal placeholder `REPLACE_WITH_A_STRONG_SECRET_STORED_IN_USER_SECRETS`, so a deployment
that forgot its environment override signed tokens with a string published in this
repository. The placeholder is gone, and `UseKernel()` now refuses to boot if the resolved
secret is absent, still a placeholder, or shorter than the 32 bytes HS256 needs. Set one of
`Kernel:Jwt:AccessSecret`, `JWT_ACCESS_SECRET` or `Jwt:Key`.

---

## Auth — `/auth`

Signup, signin, refresh, signout, sessions, email verification, password reset and magic
links all live under `Features/Auth`, ported from the Node reference and rate-limited by
the kernel. They are frozen in `docs/contract/*.yaml`, which is the authority on their
exact shapes — this page does not restate them.

The old `/api/auth/{register,login,refresh,logout}` controller **has been removed**. It
duplicated those routes on an unrated-limited path, and authenticated with
`UserManager.CheckPasswordAsync`, which neither increments `AccessFailedCount` nor honours
lockout — an unbounded credential-stuffing target. Nothing referenced it: not the contract,
not the tests, not the frontend.

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

## Tasks and documents

Real, token-scoped APIs under `Features/Tasks` and `Features/DocumentScans`, frozen in
`docs/contract/*.yaml`.

The `UserTasksTest` and `DocumentsTest` controllers that used to be documented here **have
been removed** — see the note below.

---

## Health — `/health`

`GET /health` → `200 Healthy`. No auth. For uptime checks and deployment probes. Not in the
OpenAPI document.

---

## Closed: the NFR-5 gap

`UserTasksTestController` and `DocumentsTestController` were **unauthenticated and took a
`userId` as a parameter**. Anyone who could reach the API could read, edit or delete any
user's tasks and documents — a textbook IDOR, and a direct contradiction of **NFR-5**
(*"A user shall only be able to access their own tasks, reminders, and documents, enforced
at the API layer via UserId"*).

They were scaffolding from before the Node parity port. Every route they exposed now exists
properly under `Features/`, scoped to the caller's token, so **both controllers and their
DTOs have been deleted** rather than patched. The only dependency ever recorded on them —
an old Langflow tool calling `GET /api/UserTasksTest/user/{id}`, described in `docs/ai-flow.md`
— lives on `origin/feature/langflow-agent-integration` and never reached this branch; the
replacement flows in `langflow/` do not call the API directly at all.

`NotificationsTestController` is a different animal and stays: it is `[Authorize]`d and
takes the user id from the token, never from the caller.

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
