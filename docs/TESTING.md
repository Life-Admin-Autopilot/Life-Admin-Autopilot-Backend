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

## What does NOT work yet — the AI surface

`/ai/ask` reaches Langflow and comes back with an error frame. The split matters:

**The .NET adapter is proven correct.** It emits the frontend's exact SSE contract, verified live:

```
data: {"type":"sources","sources":[]}
data: {"type":"error","code":"langflow_error", …}   ← error frame inside a 200
data: {"type":"done","usage":{}}
data: {"type":"quota","tier":"free","used":1, …}    ← always last
```

Frame format, ordering, the failures-after-flush-are-frames rule, and quota accounting all behave.

**The flow does not run.** Langflow 1.11.2 reports `Delete Task` has a required `MessageTextInput` receiving `None`, and that all 15 components are outdated — they were authored against the older schema in the supplied baseline. Tracked as task #37, with the fix path.

The flow also needs a **Mistral API key** as a Langflow global variable. The one in the supplied baseline is the leaked key from task #35 — rotate it first.

In the app this shows up as: chat and voice return an error, document scans land `failed`, search/summarize/categorize don't work, and the clarification deck stays empty. Everything else is unaffected.

## Re-running parity

```bash
# both servers need their in-memory rate limiters cleared, so restart them first
cd ../Steward/server && MONGODB_URI="mongodb://127.0.0.1:27018/kitto_parity_node" \
  GEMINI_API_KEY="" PORT=4100 NODE_ENV=development npx tsx src/index.ts &

node tools/parity/run.mjs --reference http://localhost:4100 --candidate http://localhost:5080 --no-colour
```

Expect `PASS 84 / SKIPPED 3`, exit 0. The 3 skips are the strict-auth-limiter scenario, excluded by default because it burns 5 slots against a 5-per-hour budget.

Use `:4100` (dev mode) not `:4200` (`NODE_ENV=test`) for anything worker-dependent — document scans, voice notes, reminders firing. In test mode the workers don't run, so the reference sits at `pending` while the candidate reaches `failed` and the row diverges for an environment reason.

## Gotchas that will cost you an hour

- **Rate limits are in-memory and per process.** Two harness runs inside 15 minutes exhaust the 20-per-15-min auth budget on *both* sides. Restart the server to clear it.
- **Bind `[::]`, dial `localhost`.** `127.0.0.1` is IPv4-only; Node records `::ffff:127.0.0.1` and anything echoing `req.ip` diverges.
- **`dotnet run` alone is wrong** — `launchSettings.json` silently pins ports 5115/7276 and `Development`. Always `--no-launch-profile`.
- **Don't `pkill -f Life-Admin-Autopilot`** — it matches every candidate at once. Use `lsof -ti tcp:<port> | xargs kill`.
