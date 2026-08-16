# Planning Agent v4 — contract

`planning-agent.v4.json` is the single convergence point for the product's AI. Chat, voice
transcripts and document candidates all enter through this one flow. It asks clarifying
questions, calls tools, and returns one JSON envelope.

Supersedes `planning-agent.v3.baseline.json`, which spoke a vocabulary the product does not
use, could not handle chat, and called routes that do not exist.

> **Status: live behind an import.** A Langflow instance now runs on `:7860`, but it
> executes its **own copy** of this flow from its internal database — editing this file
> changes nothing until the flow is re-imported. See
> [Deploying this JSON into the running Langflow](#9-deploying-this-json-into-the-running-langflow-on-7860).
> Structural claims are listed under [What is verified](#what-is-verified); model-behaviour
> claims are limited to the runs described inline.

---

## 1. Vocabulary

The authority is `queries/tasks.ts` in the Steward frontend and
`docs/contract/paths.tasks.yaml` in this repo.

| Field      | Values                                              |
|------------|-----------------------------------------------------|
| `domain`   | `health` `home` `car` `finance` `family` `pets`      |
| `priority` | `low` `normal` `high` `urgent`                       |
| `status`   | `open` `done` `snoozed`                              |
| `kind`     | `reminder` `list`                                    |

**There is no `draft` status and no `published` status.** v3's `Financial / Work-University
/ Health / Vehicle / Home / Personal / General` categories and its `normal / important /
urgent` priorities are gone; nothing in the product ever accepted them.

`kind` is load-bearing:

- `reminder` fires at its due moment and **must** carry a `dueAt`. The server rejects a
  dateless reminder — on the reference server that surfaces as a `500`, which is part of
  the frozen contract, so the flow refuses the combination client-side before it is sent.
- `list` is passive. It never fires and needs no date.

### The draft → kind + clarification mapping

This is the single most important translation from v3, and it is how
`holdForClarification` behaves in the Node reference implementation
(`server/src/modules/ai/toolRunner.ts`):

| v3 concept | v4 reality |
|---|---|
| confident task, has a date | `createTask`, `kind: 'reminder'`, with `dueAt` |
| confident task, no date needed | `createTask`, `kind: 'list'`, no `dueAt` |
| "draft" — unresolved uncertainty | **the task is still created**, as `kind: 'list'`, **plus a clarification carrying the question** |

The task is never withheld. What is withheld is the *reminder*: a `costOfWrong: 'high'`
item lands as a passive list entry so a guessed date can never fire at the user, while a
`costOfWrong: 'low'` item with a date is free to nudge, because being wrong just means
rescheduling. If the user never answers the question, they still have the task.

### Clarification shape

```jsonc
{
  "taskId":      "the task created for this question",
  "question":    "≤ 300 chars",
  "kind":        "date | detail | choice",
  "costOfWrong": "low | high",          // defaults to high
  "options":     [ { "label": "…", "dueAt": "…", "title": "…", "notes": "…" } ],  // 0–4, most likely first
  "sourceText":  "the user's verbatim words",
  "draft":       { "title", "domain", "priority", "tags", "notes", "dueAt" }
}
```

Only `label` is required on an option. The first option's `dueAt` becomes the applied guess
when no explicit guess was given. For `kind: 'detail'`, `options` is empty — the user types
the answer. `sourceText` is omitted in document mode, because there is no user utterance
behind a scanned page.

---

## 2. Modes

`mode` arrives as a tweak. Blank or unrecognised is treated as `chat`, because chat is the
only mode allowed to do nothing.

### `chat`

Conversational first. **Most chat turns create nothing.** Answering a question is a
complete, correct turn.

- "What's due next week?" → `queryTasks`, then answer. It does **not** become a task called
  "due next week". This is the failure the v3 prompt guaranteed and the main risk in the
  redesign; the prompt attacks it in three places (mode block, tool description on
  `queryTasks`, and the final self-check).
- Actions happen only when the user asks for one. Anything phrased as a question is treated
  as a question until proven otherwise; when genuinely ambiguous the agent answers and
  offers the action in a sentence.
- The agent starts with **no** list of the user's tasks and may not invent an id. Any
  mutation is preceded by `queryTasks` to resolve the real `taskId`. Several plausible
  matches → ask in prose and change nothing (a plain question, *not* a clarification row).

### `transcript`

Voice. Extraction, not conversation — closest to v3's behaviour. Every actionable item is
filed in one pass; a transcript with nothing actionable yields `tasks: []` rather than a
manufactured task. Uncertainty is expressed through `holdForClarification`, never as a
conversational question.

### `document`

The payload is the Document Agent's JSON, not prose:

```jsonc
{ "documentTitle", "documentSubtitle", "documentType", "documentSummary", "issuer",
  "candidates": [
    { "key", "title", "domain", "priority",
      "confidence": "high|medium|low",
      "dueAt": "2026-09-17T00:00:00.000Z",   // optional
      "notes",                                // optional
      "sourcePage" }                          // optional
  ] }
```

A bare candidates array is also accepted. Fields are already hardened and already in this
vocabulary, so they pass through unchanged. Each candidate's `key` is echoed on the matching
`tasks[]` entry so a re-scan reconciles instead of duplicating.

Uncertainty is deliberately narrower here than in chat: review already happened upstream, so
a candidate is held **only** when `confidence` is `low` **and** a wrong date is expensive.
The Document Agent writes its doubt into `notes` (ambiguous printed date, inferred date,
missing citation) and that wording drives the question.

> Verified against `langflow/document-agent.json` as it stood at the time of writing. That
> file is owned by another agent; if its output shape moves, the document mode block and
> this section move with it.

### `clarification`

The user has answered open questions. `pendingTasks` and `answers` are read; the payload
block is ignored. Each answer refers to a task that **already exists** — it is corrected via
`updateTask`, never re-created. An answer may also resolve partly, add new uncertainty, or
contain entirely new tasks ("September, and remind me to renew my passport next month"); all
of it is handled in one turn.

**The agent cannot promote `kind`.** `PATCH /me/tasks/{id}` does not accept `kind` and its
schema is `.strict()`, so sending it is a `400`. The server performs the `list → reminder`
promotion itself when a confirmed date lands via `POST /me/clarifications/{id}/resolve`. The
prompt says so explicitly so the model does not try.

---

## 3. Output envelope

Every mode returns the same object. All four keys are always present.

```jsonc
{
  "mode": "chat | transcript | document | clarification",
  "reply": "prose for the user, in the user's language; may be \"\"",
  "tasks": [
    { "id", "action": "created|updated|completed|deleted|snoozed|unchanged",
      "title", "domain", "kind", "priority", "dueAt", "key"? }
  ],
  "clarifications": [ /* the shape in §1, copied verbatim from the tool result */ ],
  "pendingConfirmations": [
    { "tool": "deleteAllTasks", "args": { "domain", "status" },
      "affectedCount": 42, "message": "…" }
  ]
}
```

| Mode | `reply` | `tasks` | `clarifications` | `pendingConfirmations` |
|---|---|---|---|---|
| `chat` | usually the whole answer | only on explicit action | rare | only on a wipe request |
| `transcript` | one short lead-in | the filed items | on held items | no |
| `document` | one short summary | filed candidates, with `key` | low-confidence + high-stakes only | no |
| `clarification` | confirmation of the correction | the updated tasks | only genuinely-remaining questions | no |

Rules the prompt enforces: only tool-confirmed ids appear in `tasks`; a failed tool is
reported in `reply` and omitted from `tasks`; clarifications are echoed verbatim from the
tool result rather than re-worded; the access token never appears in `reply`.

---

## 4. Tools

Eleven, matching the shipped product's chat surface. Each is one Langflow custom component
in `tool_mode`, and each is a thin HTTP wrapper over a real route — all correctness
(validation, timezone normalisation, the open-question cap, idempotency) stays server-side.

| Tool | Route | Confirm? |
|---|---|---|
| `createTask` | `POST /me/tasks` — **real creation, runs immediately** | no |
| `updateTask` | `PATCH /me/tasks/{id}` (+ `POST /me/tasks/{id}/conflicts` preflight on a date move) | only a clashing time — resolved in-turn, see below |
| `completeTask` | `PATCH /me/tasks/{id}` `{status:'done'}` | no |
| `deleteTask` | `DELETE /me/tasks/{id}` | no |
| `deleteAllTasks` | `POST /me/tasks/bulk/preview` (**dry run only**) | **YES** |
| `snoozeTask` | `PATCH /me/tasks/{id}` `{status:'snoozed', snoozedUntil}` | no |
| `queryTasks` | `GET /me/tasks` + filters | no (read-only) |
| `addSubtask` | `POST /me/tasks/{id}/subtasks` | no |
| `toggleSubtask` | `PATCH /me/tasks/{id}/subtasks/{subId}` | no |
| `removeSubtask` | `DELETE /me/tasks/{id}/subtasks/{subId}` | no |
| `holdForClarification` | `POST /me/clarifications` — creates the task AND the question | no |

> **History note — the draft pivot, reverted.** Commit `526f0c5` briefly repointed
> `createTask` at `POST /me/tasks/draft` (writes nothing, returns
> `status: "awaiting_confirmation"`, `task: null`). That contradicted the flow's whole
> receipt design — the prompt's lead-in says the item is already filed *because it is* —
> left two of the three description layers claiming "runs immediately", and made
> `task: null` results trip the backend's fabricated-action guard into silent turns.
> `createTask` is back on real `POST /me/tasks` creation, all three description layers
> (node `description`, `tools_metadata`, embedded class docstring) agree again, and
> trust is preserved the way v4 designed it: receipts in chat, `holdForClarification`
> for real uncertainty, `costOfWrong: 'high'` keeping a guessed reminder from firing.

The flow's tool names are **exactly** the contract names — the component methods are named
`createTask`, `deleteAllTasks` and so on rather than snake_case, so the name Langflow puts
on the wire is already the name the backend gates on. `LangflowToolNames.ToContractName`
short-circuits on `AiToolCatalog.IsKnownTool` and never reaches its alias table; no alias
entries need adding for v4. (v3 needed them because its one tool was called `save_task`.)

### `deleteAllTasks` is the only tool requiring confirmation

Calling it **deletes nothing**. It runs `POST /me/tasks/bulk/preview` — a documented dry run
that writes nothing — and returns the count that *would* be removed. The agent puts that in
`pendingConfirmations` and stops. The client asks the user; the client performs the real
`POST /me/tasks/bulk` `{filter, action:'delete'}`.

The bulk endpoint is hard-capped at 500 rows and returns `400 bulk_too_large` above it. The
preview reports `warnings.truncated`, and the component attaches a note when it trips, so
the confirmation card can say so rather than failing at execution time.

### `updateTask` refuses a clashing time — and resolves it inside the turn

Moving a task's `dueAt` preflights `POST /me/tasks/{id}/conflicts` (unless
`confirm_conflicts='true'`). A clash saves **nothing** and returns `ok: false`,
`status: 'awaiting_confirmation'`, with the clashing matters in `conflicts`. This is a
guard against silent double-booking, **not** a user-confirmation flow — prompt §7 makes
the model resolve it in the same turn:

- the **user** named the time explicitly → immediate re-call with
  `confirm_conflicts='true'`, and the reply mentions the clash;
- the **model** picked the time → re-call with a nearby non-clashing time (no flag),
  stating what it chose; if that clashes too, leave the task unchanged and say so.

Either way the turn ends with a complete reply. An `updateTask` refusal never lands in
`pendingConfirmations`, and `confirm_conflicts` is never set on a first attempt. A
preflight that cannot run does not block the edit — the server stays the authority.

### Notable per-tool behaviour

- **`update_task`** — `tags` is a full replace, not a delta. Because every tool argument is
  a string, the literal `"null"` is how the agent clears `dueAt`, `notes` or `snoozedUntil`
  (the component converts it to a JSON `null`, which is the `$unset` signal the route wants);
  `"null"` on `tags` sends `[]`. `kind` is deliberately **not** an argument.
  `confirm_conflicts` is a string flag (`'true'`) that skips the conflicts preflight — valid
  only on a re-call after a `conflict_detected` refusal, per the section above.
- **`toggle_subtask`** — the API has no flip verb, so when `done` is omitted the component
  reads the task first and inverts. Supplying `done` skips the read.
- **`create_task`** — refuses `kind: 'reminder'` without a `dueAt` locally, with an
  actionable message, rather than letting the server 500.
- **`query_tasks`** — read-only and cheap; the prompt tells the agent to use it freely, since
  it is both how questions get answered and how real ids are found.

---

## 5. Configuration — secrets and hosts

Nothing secret is committed. Two Langflow **global variables** must exist in the Langflow
instance before the flow will run:

| Global variable | Bound to | Purpose |
|---|---|---|
| `GEMINI_API_KEY` | `GeminiModel-v4.api_key` | model credential |
| `STEWARD_API_BASE_URL` | `base_url` on all 11 tool components | backend origin, e.g. `https://api.example.com` |

Both are bound with `load_from_db: true`, which is Langflow's reference-a-global-variable
mechanism: the field stores the *name*, never the value.

### The model — `gemini-3-flash-preview` at temperature 0.1

`GeminiModel-v4` is `ext:google:GoogleGenerativeAIComponent@official`, built from the live
component template rather than hand-written, with `tool_model_enabled: true`.

**It replaced `mistral-medium-latest`, and the reason is measured, not aesthetic.** Mistral
at the same temperature, against this same 24,989-character prompt, failed §5's most
explicit rule — *A DAY WITH NO HOUR: ASK WHO SET THE HOUR*, which names `"math lec
tomorrow"` **literally** as an example. It invented 09:00 and filed the lecture with no
clarification at all. The flow on disk and the flow registered in Langflow were verified
byte-identical first, so this was the model ignoring a rule it had been handed, not drift.
On the same sentence Gemini calls `holdForClarification` 4 runs out of 4, with 2–4 options
each carrying a resolved `dueAt`.

Do not read `tool_model_enabled` as the thing that makes tool calling work — the component
only consults it in `update_build_config`, to filter the UI dropdown, and never in
`build_model`. Worse, that filter is unreliable: `get_models` removes from the list it is
iterating and probes `self.model_name` instead of each candidate, so opening the canvas
narrows the dropdown to an arbitrary four models. The selected value survives, and tool
calling is proved by the behavioural runs, not by that list.

`base_url` is **not** agent-controllable (`tool_mode: false`). Letting the model choose a
host would be an SSRF hole. An empty or non-`http(s)` base URL fails loudly with a
`misconfigured` tool result and issues no request.

`verify=False` — the TLS-verification bypass v3 needed for `https://localhost:7276` — is
gone.

### Secrets that were removed from v3 and must be rotated

> **Both of these are live credentials committed in `planning-agent.v3.baseline.json` and
> are in git history. Rotating them is not optional.**

1. **Mistral API key** `<REDACTED-KEY-ROTATE-SEE-TASK-35>` —
   `MistralModel-XpO15.template.api_key.value`.
2. **A user JWT** for `fady@gmail.com` (subject `73010c68-3732-4ebb-f68b-08def1aee2ac`,
   `exp` 1786123365) — `Prompt Template-9PpOE.template.accessToken.value`.

`docs/RESUME.md:201` already lists the Mistral key as self-reported leaked. The JWT appears
to be a second, separate leak.

---

## 6. Tweaks the backend must send

**Eight tweaks**, targeting the `PlanningInput-v4` node. Six are unchanged from v3; two
grounding blocks were added since (`dateReference` and `myTasks` — see
`LangflowInputBinding.cs`, which owns the field names via `Ai:Langflow:Fields:*`).

| Tweak | Required | Value |
|---|---|---|
| `currentDate` | always | **ISO-8601 with the user's UTC offset**, e.g. `2026-08-10T14:23:00+03:00` |
| `dateReference` | always | the rendered 14-day weekday → date table plus literal phrase anchors ("this weekend", "end of this month") — the authority the prompt reads dates off |
| `accessToken` | always | raw bearer token, no `Bearer ` prefix |
| `mode` | always | `chat` \| `transcript` \| `document` \| `clarification` |
| `myTasks` | always | the rendered MY TASKS block — the user's open/snoozed matters with their real `[task:...]` ids, capped at 20; `(no open tasks)` when empty |
| `transcript` | chat, transcript, document | the payload — prose, or the Document Agent's JSON |
| `pendingTasks` | clarification | JSON array of tasks with open questions, each with its real id |
| `answers` | clarification | JSON array of `{taskId, question, answer}` |

### Two changes the backend must make

**1. `currentDate` must carry a timezone offset.** v3 sent `6-8-2026` — ambiguous
(day-month vs month-day) and offset-free. The API rejects any datetime without an explicit
offset, so with a bare date the agent has no offset to emit and every write fails
validation. This is a change to the *value format* of an existing tweak, not a new tweak.

**2. `transcript` is the payload slot for three different modes.** The name is now wrong for
two of them. Renaming it, or splitting it into `userMessage` / `transcript` /
`documentCandidates`, would be clearer — but each of those is a new tweak and therefore a
.NET change, so v4 reuses the existing slot and documents it instead. Flagging, not assuming.

### Optional hardening (a .NET change — not assumed)

Each tool component's `access_token` is `tool_mode: true`, so today the **agent** passes the
token through, exactly as v3 did. That works with zero backend change, but it puts a live
JWT in the model's context on every turn and lets a confused model corrupt it.

The better arrangement is for the backend to tweak `access_token` on each of the eleven tool
nodes directly (same value, same tweak name, additional node ids in the tweak map). The
fields already exist and take precedence when the model omits the argument, so the two
degrade into each other and can be switched without touching the flow. Node ids are
`CreateTaskTool-v4`, `UpdateTaskTool-v4`, `CompleteTaskTool-v4`, `DeleteTaskTool-v4`,
`DeleteAllTasksTool-v4`, `SnoozeTaskTool-v4`, `QueryTasksTool-v4`, `AddSubtaskTool-v4`,
`ToggleSubtaskTool-v4`, `RemoveSubtaskTool-v4`, `HoldForClarificationTool-v4`.

Conversation history is a run parameter (`session_id`), not a tweak; the Agent component's
`n_messages` controls how much is replayed.

---

## 7. The clarification write gap — CLOSED

**Was:** the backend had no endpoint that created a clarification. `/me/clarifications`
exposed only `GET`, `POST /{id}/resolve`, `POST /{id}/defer`, `POST /{id}/drop`, because in
the Node reference the row is written by an in-process module call
(`createClarification.ts`), never over HTTP — so a flow calling the public API could not
reproduce it. `hold_for_clarification` created the task via `POST /me/tasks` and handed the
question back in its result for somebody downstream to persist. Nobody did. **Held questions
produced a task and no question row**, measured: *"Remind me that I have math lec tomorrow"*
→ the tool fired, the reply asked *"What time is your math lecture tomorrow?"*, and
`db.clarifications` gained nothing. No card, no way to answer.

**Now:** the backend exposes an authenticated **`POST /me/clarifications`**, and the tool
makes exactly one call to it. The route creates the task AND the question and links them by
`taskId`, porting `runHoldForClarification` from
`server/src/modules/ai/toolRunner.ts`. Response: `201 {clarification, task, queueFull}`.

This is the **recommended** option from the two originally listed, not the minimum one: the
model is out of the persistence path, so the `clarification` the tool returns is a
**receipt** rather than a data channel, and a model that mangles the echo can no longer lose
a question. It is a route rather than a service-authenticated `/internal/…` one because the
tool already carries the caller's own bearer token, so the row is written **as that user**
with no ambient authority — a service credential would have been a second, broader trust
path for no benefit.

The route also owns the rule the whole feature turns on: a `cost_of_wrong: 'high'` hold
lands as `kind:'list'`, so a **guessed** date can never fire. That check used to live in this
component's Python, where a prompt-tuning pass could quietly change it; it is now server-side
and covered by tests.

The envelope's `clarifications[]` array is unchanged and still worth filling — it is what the
turn reports having done — but nothing depends on it any more.

Recorded as a deliberate non-match with Node in `docs/DIVERGENCES.md` §6, together with the
one guarantee that is weaker here: `sourceText` is supplied by the caller over HTTP, where
Node passes it in as a non-tool argument the model cannot touch.

---

## 8. What is verified

Verified by executing the flow's own code, in `scratchpad/`:

- The JSON parses; 15 nodes, 14 edges; no duplicate node or edge ids.
- Every edge's source and target resolve; every `targetHandle.fieldName` exists on the
  target node's template; every `sourceHandle.name` is a real output; the Langflow
  `œ`-encoded handle strings round-trip to their `data.*Handle` objects.
- All 15 embedded Python blocks compile.
- Neither leaked credential appears anywhere in the file, and no JWT-shaped literal does.
- No dead v3 host or route (`localhost:7276`, `/api/Planning/`, `/api/UserTasks`) survives;
  no literal URL appears in any authored tool component; `verify=False` is gone.
- The prompt template's `{…}` variables are exactly the eight tweaks — nothing else in the
  template body uses braces, which would otherwise be parsed as a stray variable.
- Each tool node declares exactly one tool; its declared args equal its `tool_mode` inputs;
  every declared arg is actually read by the code; every template field is constructed in
  the code; `base_url` is non-agent-controllable and bound to the global variable.
- All eleven tools are present and all eleven are wired to the agent's `tools` input, along
  with `model` and `input_value`.
- **42 behavioural checks against a mocked HTTP layer**, executing the real component code:
  correct method/path/body for every tool; bearer header; `status` never sent on create;
  dateless-reminder refusal; v3 domain rejection; `"null"` clearing semantics; boolean and
  limit coercion; the subtask read-then-invert flip; `deleteAllTasks` issuing *only* the
  preview and never a delete; the 500-row cap surfacing; `holdForClarification` creating the
  task, choosing `kind` from `costOfWrong`, taking the guess from the first option, linking
  `taskId`, preserving `sourceText` verbatim, and surviving malformed options JSON; backend
  error codes propagating; and an empty base URL failing without issuing a request.

**Not verified — Langflow is not running here and cannot be:**

- That Langflow imports this JSON without complaint. The node/template structure mirrors the
  v3 baseline field-for-field, but "structurally identical to a file that imported" is not
  the same as "imported".
- Any model behaviour whatsoever. Whether the agent actually answers "what's due next week?"
  instead of filing it, whether it holds the right items, whether it emits the envelope
  without a code fence, whether it copies clarifications verbatim — all of it is authored
  intent, not observed fact. The chat/question distinction in particular is the highest-risk
  claim in this document and the first thing to test.
- That `load_from_db: true` resolves these particular global variable names in your Langflow
  instance. The mechanism is standard; the variable names must be created by hand.
- Whether `tools_metadata` regenerates cleanly on first open in the Langflow UI. It is
  hand-authored here to match what v3 exported after a UI round-trip.
- End-to-end behaviour against the real .NET backend. Route shapes come from
  `docs/contract/paths.tasks.yaml` and the endpoint map, not from live calls.

### Suggested first tests, in order

1. Import the flow. Create both global variables.
2. `mode=chat`, `transcript="what's due next week?"` → expect a `queryTasks` call and
   `tasks: []`. If this files a task, the redesign's core claim has failed.
3. `mode=chat`, `transcript="remind me to pay rent on the 15th"` → one `createTask`,
   `kind: 'reminder'`, offset-bearing `dueAt`.
4. `mode=chat`, `transcript="delete all my tasks"` → `pendingConfirmations` populated,
   `tasks: []`, and **nothing deleted**. Verify against the database, not the reply.
5. `mode=transcript` with two items, one of them a high-stakes fuzzy date → one
   `createTask`, one `holdForClarification`, and a task row for both.
6. `mode=document` with a real Document Agent payload → candidate `key`s echoed.

---

## 9. Deploying this JSON into the running Langflow on :7860

The running instance **does not read this file from disk.** Flows live in Langflow's own
database inside its Docker volume; editing `planning-agent.v4.json` changes nothing until
the flow is re-imported. Two things must survive the swap:

- **The flow id.** The .NET backend addresses the flow by id — `LANGFLOW_FLOW_ID` in
  `.env`, default `6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14`, called at
  `POST /api/v1/run/{flowId}?stream=true`. A drag-and-drop import in the Langflow UI mints
  a **new** id, and the backend keeps running the old flow. Use `/api/v1/flows/upload/`
  (what the script below does), which keeps the file's own id.
- **The global variables.** `GEMINI_API_KEY` and `STEWARD_API_BASE_URL` live in Langflow's
  DB, not in the flow file. From inside the container the backend origin is
  `host.docker.internal`, never `localhost`.

Replace-in-place flow (the import script skips a flow that already exists, so delete
first):

```bash
cd Life-Admin-Autopilot-Backend
BASE="${LANGFLOW_BASE_URL:-http://127.0.0.1:7860}"
FLOW_ID="${LANGFLOW_FLOW_ID:-6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14}"

# 1. auto-login token (LANGFLOW_AUTO_LOGIN=true in docker-compose.yml)
TOKEN=$(curl -s "$BASE/api/v1/auto_login" | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')

# 2. remove the stale copy — this is what makes the re-import happen
curl -s -X DELETE -H "Authorization: Bearer $TOKEN" "$BASE/api/v1/flows/$FLOW_ID"

# 3. re-import the edited file AND re-seed both global variables
./tools/dev/langflow-import.sh
```

No .NET restart is needed — the backend resolves the flow per run. Smoke-test with the
[suggested first tests](#suggested-first-tests-in-order) above; the cheapest tripwire is
`mode=chat`, "what's due next week?", which must call `queryTasks` and file nothing.
