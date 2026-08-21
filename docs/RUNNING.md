# Running Kitto

Getting the whole thing up on a machine that has never seen it.

The project is two repositories plus three containers. Nothing here is
containerised except the stateful dependencies — the backend and the frontend
are run directly so a debugger attaches and a rebuild is a keystroke.

```
Life-Admin-Autopilot-Backend    .NET 10   :4000
Life-Admin-Autopilot-Mobile     Next.js   :3000
mongo                           docker    :27017    application data
mongo-test                      docker    :27018    the test suite only
langflow                        docker    :7860     the chat agent
```

---

## 1. Install

| | |
|---|---|
| .NET SDK 10 | https://dotnet.microsoft.com/download |
| Node 20+ | https://nodejs.org |
| Docker Desktop | https://docker.com/products/docker-desktop |

On Windows use **Git Bash** for the `.sh` scripts. They are plain POSIX and also
run on macOS and Linux.

---

## 2. Clone both repositories side by side

```bash
git clone <backend-url> Life-Admin-Autopilot-Backend
git clone <mobile-url>  Life-Admin-Autopilot-Mobile
```

---

## 3. Backend configuration

```bash
cd Life-Admin-Autopilot-Backend
cp .env.example .env
```

Now open `.env`. **It ships empty of secrets and it has to** — keys never live in
a repository. Whoever set the project up has the values; they have to hand them
over out of band.

Only one is strictly required:

```bash
# Signs access tokens. Any long random string, the same across restarts.
openssl rand -hex 32
```

Put that in `JWT_ACCESS_SECRET` and sign-in works. Everything else is a feature
switch, and the app degrades honestly without each one rather than failing
mysteriously:

| Left blank | What happens |
|---|---|
| `EMBEDDINGS_API_KEY` | Planning, search and document scanning answer 503 |
| `HF_TOKEN` | Voice capture answers 503 |
| `GOOGLE_CLIENT_ID` / `SECRET` | Google integration reports `available:false` |
| `INTEGRATION_ENCRYPTION_KEY` | Same — it is required *with* Google, not instead |
| `FCM_SERVICE_ACCOUNT_FILE` | Reminders still fire in-app, but never reach a phone |
| `AZURE_STORAGE_CONNECTION_STRING` | Uploads go to `./uploads` on local disk instead of Azure Blob — fully working, and the right choice for a clone |

`.env.example` says where to obtain each one.

Check what you have without starting anything:

```bash
./tools/dev/up.sh --check
```

---

## 4. Start it

```bash
./tools/dev/up.sh
```

That starts the three containers, waits for Mongo to answer a real query — not
just accept a socket, which is the gap the app's index creation trips over — and
then runs the backend on `:4000`.

Already have the containers up?

```bash
./tools/dev/up.sh --no-docker
```

> **On the machine the project was first built on**, the three containers were
> created by hand before this file existed. Compose does not recognise them as
> its own and would start a second set against **fresh, empty volumes** — the
> existing data is not deleted, but nothing would be attached to it and the app
> would look wiped. Keep using `--no-docker` there, or migrate deliberately with
> `mongodump` / `mongorestore`. On any other machine there is nothing to collide
> with and `up.sh` is the whole story.

---

## 5. The chat agent

A fresh Langflow container is **empty**. That does not look like an error: it
accepts every request, streams a healthy-looking turn, and answers nothing.

```bash
./tools/dev/langflow-import.sh
```

This imports `langflow/planning-agent.v4.json` keeping its id — the one
`LANGFLOW_FLOW_ID` names — and sets its `GEMINI_API_KEY` credential from your
`.env`. Run it once per machine.

---

## 6. Frontend

```bash
cd ../Life-Admin-Autopilot-Mobile
npm install
cp .env.example .env.local        # set NEXT_PUBLIC_API_URL=http://localhost:4000
npm run dev
```

Open http://localhost:3000.

---

## 7. Tests

```bash
cd ../Life-Admin-Autopilot-Backend
dotnet test
```

Expect **14 failures**. They are a known parity gap, not a broken checkout:
Windows' timezone database rejects a few IANA spellings Node accepts, and some
expected strings carry CRLF. Anything beyond those 14 is worth looking at.

The suite needs `mongo-test` on **27018**. Several suites skip themselves when it
is unreachable — a missing container reads as passing tests rather than absent
ones, so check it is up before trusting a green run.

---

## Per-person setup, not per-project

Two things are tied to a Google account rather than to the code, so each
teammate needs their own turn even after everything above works.

**Google sign-in on the integration.** The OAuth consent screen is in *Testing*
mode, which admits only listed accounts. Whoever owns the Google Cloud project
adds each teammate under **Audience → Test users**, or their consent is refused
with `access_denied`. The redirect URI is `http://localhost:4000/...`, which is
each person's own localhost, so that part needs no change.

**Push notifications** need a native Android build — `npx cap add android`,
`google-services.json` into `android/app/`, then Android Studio. The backend half
works without it; nothing appears on a phone until someone does that build.

---

## When it does not work

**Every endpoint answers 503 and tests take minutes.** Docker is not running.
The app cannot create its Mongo indexes and health checks fail. Start Docker
Desktop and try again.

**Chat is silent or slow.** Either the flow was never imported (section 5), or
the model is rate limited. The Gemini free tier allows 20 requests per day per
model and resets at 00:00 UTC; Langflow retries with backoff, so exhaustion
looks like a long pause followed by an empty answer rather than an error.

**Voice returns 503.** The Hugging Face token is out of credit. Credit is per
**account** per month, not per token — minting a second token on the same
account changes nothing.

**Google connect fails with `access_denied`.** You are not on the test-user list.

**A rebuild "lost" the keys.** They were environment variables on a command line
rather than in `.env`. That is what `.env` exists to prevent.
