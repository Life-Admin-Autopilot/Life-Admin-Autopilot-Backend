# Testing the frontend against the .NET backend

One command brings up the whole stack. Everything except the AI surface works today.

```bash
cd /Users/mina/Documents/Mina/Life-Admin-Autopilot-Backend
./tools/dev/stack.sh up        # mongo + langflow + .NET
cd ../Steward && npm run dev   # → http://localhost:3000
```

`./tools/dev/stack.sh status` re-prints what's listening. `down` stops what it started and leaves your own server on `:4000` alone.

| | port | |
|---|---|---|
| mongo | 27018 | isolated — **never** the shared Atlas cluster |
| langflow | 7860 | UI at <http://127.0.0.1:7860> |
| .NET | **5080** | what the frontend talks to |
| node reference | 4100 | only needed to re-run parity |

**Why 5080 and not 5000.** macOS AirPlay Receiver (ControlCenter) owns 5000 and answers **403** — which looks exactly like an auth bug. Either use 5080 or turn AirPlay Receiver off in System Settings → General → AirDrop & Handoff.

The frontend is pointed at it by `Steward/.env.local`, which is gitignored:

```
NEXT_PUBLIC_API_URL=http://localhost:5080
```

Next inlines `NEXT_PUBLIC_*` at **build** time, so restart `npm run dev` after changing it. **To go back to Node, delete `.env.local` and restart.**

## What works right now

Verified end to end through a browser-shaped request with `Origin: http://localhost:3000`, including the CORS preflight:

```
OPTIONS /auth/signup            204  + ACAO: http://localhost:3000
POST   /auth/signup             201
GET    /auth/me                 200   ← the boot gate; the app can't start without it
PATCH  /me                      200   ← onboarding
POST   /me/tasks                201
GET    /me/tasks?status=open    200
GET    /me/tasks/counts         200
GET    /me/digest               200   ← dashboard headline
GET    /me/notifications        200
GET    /me/reminders/upcoming   200   ← the whole push story on iOS
GET    /me/clarifications       200
GET    /me/document-scans       200
GET    /ai/conversation         200
GET    /ai/quota                200
```

So: sign up, onboard, create and filter matters, complete/snooze/delete with undo, subtasks, bulk actions, the dashboard, the documents tab, profile, calendar feeds, export, account deletion. **84 of 84 runnable operations byte-match the Node reference.**

## The AI surface — chat works end to end

Verified against live Langflow 1.11.2, with the resulting rows checked in Mongo rather than trusted from the reply:

```
POST /ai/ask  {"question":"remind me to call the dentist tomorrow at 9am",
               "timezone":"Africa/Cairo"}

sources → token×40 → tool_call(createTask) → tool_result → done → quota
  dueAt: 2026-08-11T09:00:00+03:00      ← the user's 9am, not UTC's
```

Confirmation-gated bulk delete, first-ever for a user:

```
ask     : 1 tool_call, 0 tool_result, stored pending_confirmation
confirm : HTTP 200, deletedCount 2, stored executed, the open task survives
```

So chat, tool calls, task creation with correct local time, and the confirm/decline
deck all work. Four defects were found and fixed only because a live instance was
available — see `git log` for `LangflowEventTranslator` and `LangflowInputBinding`.

**`LANGFLOW_INPUT_NODE` is required.** The Planning Agent has no ChatInput node, so
`input_value` alone reaches nothing: Langflow accepts the run, streams a healthy
`sources → token* → done`, and the agent answers an empty envelope with **no error
anywhere**. `stack.sh` sets it; if you boot the server yourself, set it too.

The flow needs a **Mistral API key** as a Langflow global variable. The free tier
rate-limits hard — a 429 arrives as an `error` frame inside a healthy 200, and on a
post-confirm continuation the action has *already* succeeded, so a trailing error
frame must not be read as "it didn't happen".

Still not working, both non-blocking for the chat surface:

**The document agent has never completed a real extraction.** It builds, runs and
reaches its gateway, then stops on a genuine `401 AUTH_INVALID` — `SBG_API_KEY`
exists nowhere on this machine. Everything either side of that one call was proved
by running `DocumentScanInput → CandidateHardener` in-process against a canned model
response: the hardener repaired out-of-vocabulary values and dropped an invented
`sourcePage`. The model call itself is unproven. **The Langflow global
`DOCUMENT_AGENT_API_KEY` currently holds the literal `not-a-real-key-probe-only`**,
set deliberately to force the request through to a real 401 rather than fail early
— it lives in the Langflow instance, not in this repo, so nothing here reveals it.
Replace it before expecting that flow to do anything.

**`clarification` mode is unreachable through `/ai/ask`.** The flow supports all four
modes and each was verified directly against Langflow, but the adapter only ever
sends `mode=chat`, and `pendingTasks`/`answers` are not wired. That needs a route
that knows the user is answering questions; the measured payload shapes are recorded
in `LangflowInputBinding`'s docstring so the next person need not rediscover them.

## Re-running parity

```bash
# both servers need their in-memory rate limiters cleared, so restart them first
cd ../Steward/server && MONGODB_URI="mongodb://127.0.0.1:27018/kitto_parity_node" \
  GEMINI_API_KEY="" PORT=4100 NODE_ENV=development npx tsx src/index.ts &

node tools/parity/run.mjs --reference http://localhost:4100 --candidate http://localhost:5080 --no-colour
```

Expect `PASS 84 / SKIPPED 3`, exit 0. The 3 skips are the strict-auth-limiter scenario, excluded by default because it burns 5 slots against a 5-per-hour budget.

**Run the candidate with Langflow NOT configured.** Parity is defined against *Node
without `GEMINI_API_KEY`*, so the candidate has to be in the same not-configured
state. `stack.sh up` deliberately wires Langflow in, which is right for using the
app and wrong for measuring parity: with it set, `/ai/voice/transcribe` answers
`empty_body` where the reference answers `ai_not_configured`, and three rows fail
for a reason that is not a defect. Boot the candidate without the `LANGFLOW_*`
variables when you want a parity number.

Use `:4100` (dev mode) not `:4200` (`NODE_ENV=test`) for anything worker-dependent — document scans, voice notes, reminders firing. In test mode the workers don't run, so the reference sits at `pending` while the candidate reaches `failed` and the row diverges for an environment reason.

## Gotchas that will cost you an hour

- **Rate limits are in-memory and per process.** Two harness runs inside 15 minutes exhaust the 20-per-15-min auth budget on *both* sides. Restart the server to clear it.
- **Bind `[::]`, dial `localhost`.** `127.0.0.1` is IPv4-only; Node records `::ffff:127.0.0.1` and anything echoing `req.ip` diverges.
- **`dotnet run` alone is wrong** — `launchSettings.json` silently pins ports 5115/7276 and `Development`. Always `--no-launch-profile`.
- **Don't `pkill -f Life-Admin-Autopilot`** — it matches every candidate at once. Use `lsof -ti tcp:<port> | xargs kill`.
- **An orphaned server from another worktree will corrupt your results, silently.**
  Every backend runs the same `BackgroundService` workers against whatever Mongo it
  was pointed at, so a leftover process *competes for scan and reminder jobs* and
  writes outcomes computed from **its** working directory. This cost an hour: two
  document-scan rows failed with `Could not find a part of the path
  '…/backend-slices/m-langflow/uploads/…'` while the file sat correctly in this
  repo, and the same upload driven by hand passed. Killing the port is not enough —
  the offender need not be listening on one, and it can be reparented to init. List
  the actual clients:

  ```bash
  lsof -nP -iTCP:27018 | awk 'NR>1 && $10!="(LISTEN)"{print $2}' | sort -u | while read p; do echo "$p $(lsof -a -p $p -d cwd | tail -1 | awk '{print $NF}')"; done
  ```

  Anything whose cwd is not this repo or `Steward/server` should not be running.
