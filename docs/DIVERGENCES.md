# DIVERGENCES — where the .NET port deliberately does NOT match Node

The parity target is byte-level response compatibility with the Node server running
without `GEMINI_API_KEY`. This file is the list of places we have decided **not** to
match, each with the reasoning and who ruled on it.

**A divergence belongs here only if it was argued and decided.** An accidental
difference is a bug; a difference nobody wrote down is indistinguishable from one.
Parity traps that must NOT be "fixed" are the opposite thing and live in
`docs/KERNEL.md` §2 — do not confuse the two lists.

---

## 1. Account deletion erases three collections Node leaves behind

**Decided:** slice K (profile), extending the ruling the coordinator made for slice G.
**Status:** implemented. `icsFeeds` and `translationUsageCounters` were already
registered by slices F and C; this entry ratifies all three under one rule.

### What Node does

`routes/me.ts` `deleteUserAndDependents()` deletes twelve collections by hand:

```
RefreshToken, Task, TaskBulkOp, VoiceNote, ScannedDocument, AiConversation,
AiUsageCounter, DocumentScanUsageCounter, Clarification, DailyDigest,
Notification, VerificationToken
```

It **omits `icsFeeds`, `integrations` and `translationUsageCounters`**. All three
collections were added to the product after that function was written, and nobody
went back to extend the list — the omission has no comment, no test and no visible
intent behind it.

### What .NET does

All three are erased, through their owning slice's registered `IUserDataEraser`:

| Collection | Eraser | Registered by |
| --- | --- | --- |
| `integrations` | `IntegrationEraser` | slice G (Google) |
| `icsfeeds` | `IcsFeedEraser` | slice F (ICS) |
| `translationusagecounters` | `TranslationUsageEraser` | slice C (tasks) |

### Why

The coordinator ruled on `integrations` first: **an encrypted Google OAuth refresh
token surviving account deletion is a defect, not a behaviour worth reproducing.**
"Delete my account" that leaves a live credential behind is the kind of parity you
should refuse.

The same reasoning applies to the other two, and applying it inconsistently would be
worse than either choice on its own:

- **`icsFeeds`** holds a third-party calendar URL the user subscribed to. It is
  personal data by any reading, and leaving it behind means the erasure promise is
  false. It also has an **observable** consequence, which is the part that settles
  it: `GET /me/ics-feeds` never loads the user row, so on Node a deleted account
  whose access token has not yet expired **still lists its calendar subscriptions**
  (measured against `:4200` — 200 `{"feeds":[...]}` after a successful `DELETE /me`).
  That is not a shape difference, it is a deleted account reading its own data back.
- **`translationUsageCounters`** is the weakest case: a per-day quota row keyed by a
  user id that will never be minted again, so nothing can read it. It is erased
  anyway, because the rule worth having is "the cascade covers every collection a
  slice owns", and a rule with a carve-out for rows we judge boring is a rule that
  stops being applied.

### What it costs

Nothing a client can observe in the direction that matters — .NET erases a superset,
so every difference is data that is *gone* rather than data that is *wrong*. No
harness row exercises the `icsFeeds` case: the ICS scenario runs offline and its
SSRF guard rejects every URL it tries, so a feed is never created to be orphaned.

### The structural point

This is why `IUserDataEraser` exists. Node's hand-maintained list is a single
function that every feature has to remember to edit; it has already failed that way
three times. In the port each slice registers its own eraser and the cascade is
whatever is registered, so a new collection cannot be forgotten and `DELETE /me`
never becomes an N-way merge conflict.

### Open: five collections have no eraser yet

`voicenotes`, `aiconversations`, `aiusagecounters`, `clarifications` and
`dailydigests` are on Node's list but have no registered eraser, because the slices
that own them are not merged. **Each is that slice's to register** — slice K must not
add them, or they will be registered twice when those slices land. Not observable
through any endpoint today; tracked here so it is not mistaken for a decision.

---

## 2. `GET /me/export` omits `__v` on every raw document — NOT a decision, an open bug

**Status:** FAILING. `profile / export` is red on exactly one path,
`$.sessions.items[0].__v` (reference `0`, candidate absent). Listed here so it is not
mistaken for the divergence above.

Mongoose stamps `__v: 0` on every document it inserts; the .NET driver does not, and
no typed document in the port declares the field. The export is honest — it returns
the raw rows as they are actually stored — so **the export is not where this should
be fixed.** Fabricating `__v: 0` in the projection would report a version the
database does not hold.

The fix belongs at the point of insert, and slice E already set the precedent by
writing `["__v"] = 0` into its raw-BSON seed
(`BLL/Features/DocumentScans/DocumentScanReviewService.cs`). The typed inserts have
not followed:

- `DAL/Features/Auth/AuthDocuments.cs` — `RefreshTokenDocument` (the row the harness
  actually catches, since signup creates one)
- `DAL/Kernel/Documents/*` — every kernel document, `TaskDocument` included

A kernel-level convention that stamps `__v: 0` on insert would fix all of them at
once and is the better shape, but it is a `Kernel/` change and belongs to the kernel
owner. Until then the export row stays red for a real, understood reason.

*(Note: `TaskDocument` has since gained an explicit `[BsonElement("__v")]`
`SchemaVersion` property, so the list above is narrower than when it was written.
Re-check before acting on it.)*

---

## 3. The Express HTML 404 body is not reproduced

**Decided:** the coordinator, arbitrating a Fix A / Fix B split; the ruling reversed
an earlier one that had gone the other way. Recorded in full at `KERNEL.md` §2.2.1
and `RESUME.md`.
**Status:** implemented — status matches, body and content-type deliberately do not.

### What Node does

Express has no catch-all JSON 404 handler. An unknown route, or a wrong method on a
known one, falls through to `finalhandler`, which serves an HTML page:

```
404  Content-Type: text/html; charset=utf-8
<!DOCTYPE html>…<pre>Cannot PUT /health</pre>…
```

### What .NET does

Returns **404 with an empty body and no content-type**. The status is matched — that
part was a real bug (ASP.NET returned 405 on a method mismatch) and was fixed. The
body is not reproduced.

### Why

**The body interpolates the request path, and the request path is attacker
controlled.** `Cannot PUT /<script>alert(1)</script>` served as `text/html` from the
same origin that serves authenticated JSON is reflected XSS on every unknown route of
the API. Node's `finalhandler` escapes; a naive port does not, and the naive port is
what a "make the harness green" change would produce.

The trade is not cosmetic-versus-correct, it is **cosmetic-versus-XSS**. Nothing
parses a 404 body — the frontend branches on status — so the status is the only
difference a client can actually observe, and that one is matched.

### What it costs

Two harness rows cannot assert body or content-type. Both are declared exceptions
carrying `statusOnly: true` in `tools/parity/scenarios/00-framework.yaml`
(`unknown-route-is-html-404`, `unknown-method-on-known-path-is-html-404`) — the only
two `statusOnly` steps in the corpus, each with an inline comment citing §2.2.1. A
standing red row would train people to ignore red rows; a declared, reviewed
exception does not.

`statusOnly` still asserts that neither side returned a JSON error envelope, so the
*other* way to get this wrong — emitting the kernel's envelope here — is still
caught, by `MethodMismatchTests.the_404_body_is_not_the_json_error_envelope`.

### How to revert

Reproduce `finalhandler`'s body in the 404 middleware, **after** the 405→404 status
rewrite (gate on "final status is 404 and nothing has been written", *not* on
`GetEndpoint() is null` — that predicate is false on a method mismatch). Non-mergeable
without all four of: HTML-escaping the path, a test asserting
`/<script>alert(1)</script>` comes back escaped, Express's CSP header on the
response, and a byte-exact capture of the real body — which nobody has yet taken.

---

## 4. The `Server` header is suppressed

**Decided:** slice kernel-hardening, alongside the twelve helmet headers (`KERNEL.md`
§2.7).
**Status:** implemented — `KestrelServerOptions.AddServerHeader = false`.

### What Node does

Sends **no server-identity header at all**. Node's HTTP server does not set `Server`,
and `app.disable('x-powered-by')` removes the `X-Powered-By: Express` that Express
would otherwise add.

### What .NET does

Also sends nothing, because Kestrel's default `Server: Kestrel` is switched off.

### Why

**Read this entry carefully: it is a deviation from the ASP.NET default in order to
*match* Node — not a place where the port refuses to match.** It is recorded here
because it is a deliberate, argued decision about response headers that a reader of
this file would otherwise go looking for, and because "we turned off a framework
default" is exactly the kind of thing that gets silently reverted by a future
`Program.cs` edit.

On the merits it is also the right call independent of parity: advertising the server
implementation buys an attacker version-specific exploit selection for no benefit.

**Recommendation:** this arguably does not belong in DIVERGENCES.md, whose stated
rule is "places we have decided *not* to match". There is no residual difference from
Node here — the two servers emit byte-identical header sets. It is already documented
in `KERNEL.md` §2.7. Consider this entry a cross-reference and delete it if you would
rather keep this file strictly to genuine non-matches.

### How to revert

Delete the `AddServerHeader = false` line. Doing so *introduces* a parity break.

---

## 5. An oversized **unauthenticated** audio body is 401 here and 500 on Node

**Decided:** recorded during consolidation; not previously written down.
**Status:** known, not implemented, unreachable from the harness.

### What Node does

`POST /ai/voice/transcribe` mounts its body parser **before** its auth check
(`modules/ai/routes.ts:366-373`):

```ts
aiRouter.post(
  '/ai/voice/transcribe',
  express.raw({ type: [...], limit: CHAT_AUDIO_MAX_BYTES * 2 }),   // ← first
  requireAuth,                                                      // ← second
  ...
)
```

So a >12MB body from a caller with no token blows the `express.raw` ceiling before
`requireAuth` ever runs. body-parser throws `entity.too.large`, which this app renders
as **500** (§2.1 — oversize bodies are 500, not 413).

Note this ordering is specific to this route. `POST /me/voice-notes`
(`routes/me.voiceNotes.ts:72-80`) puts `requireAuth` **first**, so the same request
there is a normal 401 on both servers.

### What .NET does

Returns **401**. ASP.NET's authentication/authorization middleware runs in the
pipeline, ahead of the endpoint, so the request never reaches the body-size check.

### Why not matched

Matching would mean running a body-size check for one route *ahead of*
`UseAuthorization()` — inverting the pipeline's security ordering so that an
unauthenticated caller can make the server buffer and measure 12MB of their body
before it decides who they are. That is a worse default than the divergence, and it
would have to be special-cased to a single path to avoid changing every other route.

The direction of the difference also matters: .NET rejects **earlier** and with a
**more correct** status. 401 is the right answer to an unauthenticated request; the
500 is a Node accident of middleware order, not a designed response.

### What it costs

Nothing measurable. **Unreachable from the harness**: the corpus has no step that
sends an oversized body without a token, and `provisionUser()` authenticates before
any upload step. It is listed here so that if such a row is ever added, the red is
recognised as this decision rather than investigated as a regression.

### How to revert

Register a size-limit middleware scoped to `/ai/voice/transcribe` **before**
`UseAuthorization()` in `UseKernel()`, returning the same 500 the malformed-body path
produces. Do not make it global.

## 6. `POST /me/clarifications` exists here and not in Node

**Decided:** the hold-route slice, taking the option `langflow/PLANNING-AGENT.md` §7
already recommended — "a route the tool component calls directly", so the model is out
of the persistence path.
**Status:** implemented. Adds no parity row — Node has no behaviour here to differ
from — and changes no existing route. Measured after the change: **PASS 84 /
SKIPPED 3**, candidate booted without the `LANGFLOW_*` variables.

### What Node does

Nothing. `routes/me.clarifications.ts` exposes `GET /me/clarifications` and the three
terminal actions (`/{id}/resolve`, `/{id}/defer`, `/{id}/drop`) — and **no create**.

That is not an oversight. In Node a Clarification is only ever written
**in-process**: `toolRunner.runHoldForClarification` for chat, and the voice-note
transcriber for recordings. Both live inside the server, so an HTTP create would have
been a route with no caller.

### What .NET does

Adds `POST /me/clarifications`, authenticated, which performs the whole hold: it
creates the Task, then the Clarification, linked by `taskId`. It is a port of
`runHoldForClarification` — same argument schema (`holdForClarificationArgs`), same
guess precedence (explicit `dueAtGuess`, else the first option's date), same
`costOfWrong` default of `high`, same `MAX_OPEN_CLARIFICATIONS` degradation, same
`sourceQuote` clamp. Responds `201 {clarification, clarifications, task, queueFull}`.

It has since grown one thing `runHoldForClarification` has no equivalent of: an
optional `questions` array (max 3) that files **one task and one clarification row per
question**, all linked by that `taskId`. Node cannot need it — its agent is in-process
and can call the module twice with a task id in hand — but ours cannot, because
`holdForClarification` has no `task_id` argument and a second call files a duplicate
task. Without the array a matter with two gaps had to fold both into one question, and
one question has one answer slot: on 2026-08-16 *"remind me today to go to the friend"*
was held as *"What time should I remind you — and which friend are you visiting?"*, the
user tapped "9 am", and the which-friend gap ceased to exist. Absent the array, request
and response are byte-identical to the single-question form; `clarification` is always
the first row. The queue cap counts rows.

### Why

**Our planning agent runs in Langflow, outside the API.** All it can do is make HTTP
requests, so a behaviour with no route does not exist for it. The consequence was
measured, not theorised: asking Kitto *"Remind me that I have math lec tomorrow"*
fired the `holdForClarification` tool, the tool created the task via `POST /me/tasks`,
the reply said *"Filed. What time is your math lecture tomorrow?"* — and
`db.clarifications` gained nothing. The model asked a question the product had no way
to receive an answer to, and no uncertainty card could ever appear.

The alternative — have the .NET adapter read `clarifications[]` off the flow's
response envelope and write the rows itself — needs no new route, but makes
correctness depend on a language model echoing a structured object faithfully every
turn. PLANNING-AGENT.md §7 called that the "minimum" option for exactly that reason.

**The route also owns the rule, which is the stronger argument for it.** The thing
that must not go wrong is that a *guessed* date fires: a high-cost hold has to land as
`kind:'list'`. Split across two calls, that rule lives in the tool component's Python
— model-adjacent code a prompt-tuning pass can silently change. Behind one route it is
server-side and testable, pinned by
`ClarificationHoldTests.a_high_cost_guess_files_a_passive_task_and_links_the_question_to_it`.

### What it costs

Nothing observable on any ported route, and no contract operation. Three things worth
knowing:

- **`sourceText` is caller-supplied here, and in Node it structurally cannot be.**
  Node passes the user's verbatim words into `runTool` as a *non-tool* argument, with
  an explicit comment that the model "must never get to edit, summarise, or invent
  what the user said". Our agent is the HTTP caller, so the field arrives in the body
  and a model *could* paraphrase it. It is bounded (2000 in, clamped to 600 stored)
  and display-only — nothing reads it back into a prompt — but the guarantee is
  weaker. Closing it properly means the backend tweaking the value into the tool node
  per run, the way `access_token` is meant to be delivered; `LangflowInputBinding`
  only tweaks the input node today.
- **The queue cap counts every open row, deferred ones included.** Node's
  `countOpenClarifications` is a bare `{userId, status:'open'}`, deliberately not
  `VisibleOpen()`. A skipped question is still queued and still returns, so it still
  occupies a slot. This is the one place a clarification count legitimately does not
  compose that predicate.
- **`timezone` is accepted and honoured, but the flow does not send it.** See the next
  section: `HoldTimeNormalizer` is a faithful port of `timeNormalize.ts`, so this route
  *is* the "dedicated agent-facing route that normalises" the gap below asks for. The
  tool component has no IANA zone to pass it, so a naive date still falls back to UTC
  on this path too. The server half now exists; the wiring does not.

### How to revert

Delete the `MapPost("/me/clarifications", …)` block, `ClarificationHoldService`,
`HoldRequest.cs` and `HoldTimeNormalizer`, and point the flow's
`HoldForClarificationTool` back at `POST /me/tasks`. Doing so reinstates the measured
defect above: held questions produce a task and no question.

## AI guards: what Node has that the Langflow path does not

Two guards live in Node's AI module. Neither was ported during the port, because
AI was deferred; their absence was an accident rather than a decision. Recording
the decision now.

### `timeNormalize.ts` — NOT ported, and there is a measured risk

Node's tool runner normalises the model's dates before writing, on the principle
its own header states: *"the server is the source of truth, not the prompt."* A
naive datetime is treated as WALL-CLOCK IN THE USER'S ZONE and converted.

Our agent does not go through a server-side tool runner — its tools call the
public `POST /me/tasks` like any client. Measured on that route:

| `dueAt` sent | .NET stores | as Cairo local |
|---|---|---|
| `2026-09-20T09:00:00` (naive) | `09:00:00Z` | **12:00** |
| `2026-09-20T09:00:00+03:00`   | `06:00:00Z` | 09:00 |
| `2026-09-20` (bare date)      | 400 `invalid_body` | — |

So a naive datetime is read as UTC and lands **wrong by the user's whole offset**.
Node's AI path would have stored 09:00 local; ours stores 12:00.

**Not fixed on `/me/tasks`, deliberately** — that is a parity-frozen public route
and changing how it reads a naive datetime would diverge from the reference for
every client, not just the agent.

**Why it is latent rather than live:** the flow's system prompt requires an
explicit offset ("NEVER emit a naive datetime … rejected by the server"), and the
agent was measured emitting correct offsets — "3pm" Cairo → `12:00Z`, "11am" →
`08:00Z`. The risk is that a prompt is not a guarantee. The right home for a fix
is the AI path, not the shared route: either the flow's tool normalises against
the caller's zone before POSTing, or the tool passes the zone and a dedicated
agent-facing route normalises. Until then, treat a naive datetime as a bug in the
flow's output, not in the route.

**Update — the second option now exists on ONE path, server half only.**
`POST /me/clarifications` (§6, commit `9931082`) accepts a `timezone` and runs
`HoldTimeNormalizer`, a faithful port of `timeNormalize.ts`. So the route this
paragraph asks for is built, for the hold lane. **The wiring is not**: the flow's
`HoldForClarificationTool` has no IANA zone to send it, so a naive date still lands
as UTC there exactly as described above, and `POST /me/tasks` is untouched. Recorded
by the grounding slice on the clarifications slice's report, not independently
measured here.

The missing half is a mechanism, not a policy: the backend would have to tweak the
user's zone into the TOOL nodes per run, the way `access_token` already reaches them.
`LangflowInputBinding` tweaks only the input node today (§7), so that is where such a
change would start.

### `hallucinationGuard.ts` — NOT ported, and not applicable

It strips `[task:id]` / `[voice:id]` citations the model was never shown, replacing
them with `(unverified)`. It guards Node's `contextBuilder` grounding, where the
model is handed source ids it can cite. Our flow retrieves its own context and
reports no source list — the `sources` frame is always empty — so there are no
citations to guard. If grounding ever hands the agent citable ids, this must be
ported with it.

> **⚠️ That condition has now fired.** §7 below ports Node's `MY TASKS` block, which
> hands the agent up to twenty real `[task:<id>]` and `<subtask:<id>>` tokens on every
> turn. The guard is still NOT ported. What keeps it from being live today is that the
> ids the agent *acts* on come from tool results, and the only prose the client renders
> is the envelope's `reply` — but the model can now see the citation syntax, so a
> `[task:…]` in prose has become possible where it was not before. The `sources` frame
> is also still empty even though the data to fill it now exists in
> `AiGroundingRepository`; Node populates it from exactly this list. Both are open, and
> both belong to whoever next owns the AI stream.

---

## 7. The agent's grounding is ported; a missing timezone means UTC, not the server's zone

> **Superseded in part by §16.** The ruling below — that the fallback must not depend on
> `TimeZoneInfo.Local` — stands. Its *conclusion*, that the fallback should therefore be
> UTC, does not: an absent timezone now resolves to `Africa/Cairo`. Read §16 before
> acting on this section.

**Decided:** slice m-grounding, porting `modules/ai/contextBuilder.ts`.
**Status:** implemented. The port is byte-identical to Node everywhere the caller
supplies a timezone; the single difference is what an absent or unusable one means.

### What was ported, and why it is not optional

Node assembles a grounding block for **every** AI turn. Until now the Langflow adapter
sent four fields — `transcript`, `currentDate`, `accessToken`, `mode` — so the model did
relative-date arithmetic unaided and could not see the user's existing matters at all.
Both halves are now sent, as two new tweaks on `PlanningInput-v4`:

| Tweak | Node source | What it carries |
| --- | --- | --- |
| `currentDate` | `formatNow()` | local ISO **with offset** and the weekday — `2026-08-11T14:23:45+03:00 (Tuesday)` |
| `dateReference` | `buildDateReference()` | 14-day weekday→date table + literal anchors (`this weekend = Sat … & Sun …`, `end of this month = …`) |
| `myTasks` | the `=== MY TASKS ===` block | open + snoozed matters, **capped at 20**, each with its real `[task:<id>]` |

The comment above the anchor code in the reference states the motive: *"Literal anchors
for common relative phrases the model otherwise guesses."*

`currentDate` gained the trailing ` (Weekday)` in this change — it is part of
`formatNow()`, and the port is now that one function rather than a partial copy of it.

### The one difference

`DateGrounding` treats an **absent, unknown or malformed** timezone as **UTC**.

Node's no-timezone branch prints the instant with `Date#toISOString()` — a `Z`
timestamp — but takes the weekday, and the entire 14-day table, from
`Intl.DateTimeFormat` with no `timeZone`, i.e. **the server process's own zone**.
Measured: for `2026-12-31T22:10:00Z` on a machine set to `Africa/Cairo`, Node emits
`2026-12-31T22:10:00.000Z (Friday)` — a UTC instant labelled with Cairo's weekday, over
a table whose "today" is 1 January.

### Why not matched

The reference's output on that path is **not reproducible and not self-consistent**. It
depends on the deployment's `TZ`, so a laptop in Cairo and a container in UTC answer the
same request with different tables; and the weekday disagrees with the timestamp printed
beside it. Reproducing it would mean making the port's behaviour a function of
`TimeZoneInfo.Local`, which is an accident of where the process runs.

Treating an unusable zone as UTC makes the fallback coherent, machine-independent, and
byte-identical to Node's own `timezone: 'UTC'` output. It also preserves the existing
`+00:00` contract — an offset-free `currentDate` is the failure that put every derived
`dueAt` out by the user's whole offset.

### What it costs

Nothing observable through the API. `/ai/ask` is **503 on the parity target** (Node with
no `GEMINI_API_KEY`), so no harness row reaches any of this, and the frontend always
sends a timezone. The difference is reachable only by a caller that omits `timezone`
entirely, and only in the weekday word — the dates in the table are then UTC's, which is
the honest answer when the caller has not said where they are.

### What was verified, and how

Not by re-reading the TypeScript. Node's `formatNow()` and `buildDateReference()` were
run under a frozen clock over a grid of 9 instants × 7 zones and the 63 resulting strings
compared to the port: **identical, both functions, every row** (month and year rollovers
under +14:00 and −09:30, both DST transitions, leap February, and the weekend anchor on a
Saturday and on a Sunday). `AiDateGroundingTests` keeps eleven of those pairs as
literals.

The `MY TASKS` block was compared the same way: Node's own `buildPersonalContext()` was
run against the seeded demo account (142 open matters) in the isolated parity Mongo, and
its twenty rendered rows diffed against this port's, reading the same database.
**Byte-identical**, including the sort — undated matters lead, because a missing `dueAt`
sorts before any date in Mongo's ordering, and the cap then truncates *that* order.

### A hazard the cap introduces, handled in the prompt rather than the code

Twenty rows over 142 matters is a window, not a census, so a model reading it as complete
will confidently answer "you have nothing like that". Node has the same cap and the same
exposure. The flow's input block therefore states that the list is capped, that absence
from it is not proof, and that `queryTasks` is the answer when certainty is the point —
with `(no open tasks)` called out as the one value that does mean exactly what it says.

### How to revert

Unset `Ai:Langflow:Fields:DateReference` / `:MyTasks`? No — the fields are always sent
when a node is bound. Reverting means removing the two `target[…]` assignments in
`LangflowInputBinding.BuildRequest` and the two inputs from `PlanningInput-v4`. Doing so
restores the guessing this entry exists to remove.

---

## 8. The conversation document carries a `sessionKey` Node does not write

**Decided:** slice M (Langflow), session identity.
**Status:** implemented, and verified live against Langflow 1.11.2.

### What Node does

`modules/ai/conversationService.ts` `resetConversation()` is one write:

```ts
await AiConversation.updateOne(keyFilter, { $set: { messages: [] } }, { upsert: true })
```

That is the whole of a reset, and for Node it is enough: Gemini is stateless
between calls, so the conversation Node sends is the conversation Node stores.
Emptying `messages` really does erase the model's entire memory of the user.

### What .NET does

The same write, plus `$set: { sessionKey: <a fresh ObjectId> }`. A new field, and
the only field in `aiconversations` that has no counterpart in
`server/src/models/AiConversation.ts`.

It is **absent until the first reset**. The value the port actually uses is
`sessionKey ?? _id` (`AiConversationDocument.SessionGeneration`), so a conversation
that has never been reset stores nothing extra and is byte-identical to the row
Mongoose writes.

### Why

Our agent is **not** stateless. Langflow keeps its own conversation memory, keyed on
the `session_id` we send with every run — the Agent component replays up to
`n_messages: 100` of it. The adapter sent `session_id = userId`, which is a
permanent per-user session: it never changes, for the life of the account.

So Node's reset semantics stop being sufficient. Emptying our `messages` cleared the
history the user can see and left the history the agent answers from, and two
user-visible failures followed:

- **The reset button lied.** The UI empties, the agent remembers everything, and the
  next answer is drawn from a conversation the user believes they deleted.
- **Stale replay.** Re-asking a question inside the immortal session returned a
  memorised envelope with **no tool call** — the agent replaying its previous answer
  instead of acting. That is indistinguishable from the agent hallucinating a
  completed action, and it cost a debugging session. It also meant anyone
  re-verifying AI behaviour on an existing account was measuring a cache.

A stored generation is what a reset can rotate. `session_id` is now
`<userId>:<generation>`: the user half keeps one account's memory out of another's
(Langflow has no notion of our tenancy — the key is the only separation), and the
generation half changes on reset and at no other time.

**Why the document id and not a timestamp or a counter.** The generation must be
stable for the whole life of a conversation — a turn and the continuation that
follows its confirmation have to share a session or the agent forgets its own plan
halfway through — and it must never repeat across users. An id Mongo already mints
satisfies both, and, because `SessionGeneration` falls back to `_id`, no insert path
can forget to stamp one. The three upsert sites (`LoadAsync`, `AppendTurnAsync`,
`ResetAsync`) would otherwise each have to remember, and one that forgot would
produce a conversation with no session at all.

**Why not delete the document on reset instead**, which would give a fresh `_id` for
free: because `GET /me/export` dumps `aiconversations` raw, and a reset that removes
the row makes an export taken afterwards differ from Node's by a whole missing
document rather than by one extra key. Rotating a field also keeps `createdAt` and
the unique key intact.

### The second half: Langflow is asked to forget the retired session

Rotation alone is sufficient for correctness — the old session is never addressed
again. But the transcript is still sitting in Langflow's `message` table after a user
pressed "clear", so the reset also issues
`DELETE /api/v1/monitor/messages/session/<retired session>`.

That is a deletion on another system, so it is **best-effort by construction**:
it runs last, after the local write has committed; a failure is logged and the reset
still answers 200; and it is not given the request's cancellation token, because
abandoning it when the client hangs up would orphan the transcript permanently. The
seam is `IAgentSessionMemory`, whose default registration is a no-op — a deployment
with no external agent has nothing to forget and makes no outbound call.

### What it costs

Nothing on the parity target, which is the state that defines parity: with no
`LANGFLOW_*` configured the provider is `NotConfiguredAiProvider`, the session memory
is `NoAgentSessionMemory`, and `POST /ai/conversation/reset` makes exactly the writes
it always made. Parity re-run at **84 PASS / 3 SKIPPED** with this change in.

The one reachable difference is `GET /me/export` for a user who has reset a
conversation: the exported `conversations[]` row carries a `sessionKey` the reference
does not have. No harness row exercises it — the export scenario's fresh user never
opens the AI surface — and no client reads the field.

### What was verified, and how

On a **fresh account** against live Langflow, because an existing account already
carries the memory being tested:

1. A task-creating question, asked twice, acted **both** times — a real `tool_call`
   and `tool_result` each turn, not a replayed envelope.
2. Ask → `POST /ai/conversation/reset` → "what did I just ask you to do?" The agent
   did **not** know, and Langflow's session list showed the retired session gone and
   a new one in its place.
3. A confirm-gated turn followed by `POST /ai/tools/confirm/{callId}` continued in
   the **same** session, so the continuation still had the plan's context.

`AiSessionIdentityTests` pins all three properties, and the reset/continuity pair was
confirmed to fail when the provider is reverted to `session_id = userId`.

---

## 9. A turn that claims work it never did is corrected, not delivered

**Decided:** slice M (Langflow), session identity — at the coordinator's request.
**Status:** implemented, and verified live in both directions.

### What Node does

Nothing, because it cannot happen the same way. Node's tool loop runs the tools
itself, so the model's function calls and their results are the same objects the
route persists: there is no second, prose-level account of the turn that could
disagree with them. Node's one adjacent guard, `hallucinationGuard.ts`, strips
`[task:id]` citations the model was never shown — a different failure (citing a
source) from this one (reporting an action).

### What .NET does

After the stream ends and before the assistant turn is persisted,
`FabricatedActionGuard` compares the envelope's structured half against the tool
calls that actually ran. If a claim is unaccounted for, the turn is corrected:

- the prose is **not persisted** — an assistant turn is written only for the tool
  calls that really happened, and not at all if there were none;
- an `error` frame (`unverified_action`) is emitted **before** `done`;
- the buffered non-streamed copy of the answer is withheld so it cannot be printed
  after the correction.

### Why

Measured live on a fresh account: one turn replied *"Added it."* with zero
`tool_call` frames and zero rows written. Separately, another wrote a complete
clarification into the envelope under an invented `taskId`
(`hold_math_lec_2026-08-12`) having called nothing.

This is the only failure class in the product where **no surface will ever
contradict the user**. A wrong due date shows up on the matter. A failed tool shows
up as an error. A task the agent only said it created is simply absent, with nothing
anywhere to explain why — and the user has been told, in prose, that it is filed.

The flow's prompt already forbids it. That is not enough, and the reason is
structural: the model that does this is the degraded one — rate-limited, truncated,
or having a bad day — and a degraded model is precisely the one that will not police
itself.

### What is compared, and what deliberately is not

**The structured half only.** The flow's contract calls `tasks` "a receipt of what
you actually did", requires every id in it to be one "the tool returned", and forbids
inventing one. That is checkable. So: every `tasks[].id` and `clarifications[].taskId`
must appear somewhere in the output of a call that executed this turn, and a
`pendingConfirmations` row requires a gated call to exist.

**Never the prose.** "Added it." is a claim too, but detecting it means matching
words, and the reply is written in the user's language. A word matcher fires on
"Hi! How can I help?" in one locale and misses a filed task in another. An envelope
that claims nothing structurally is an ordinary chat turn and passes untouched.

**Ids the model passed INTO a tool do not count** — only what came back. Otherwise an
invented id laundered through a tool call would validate itself.

### Three ways it stays silent, on purpose

A false accusation is worse than a missed one, so no verdict is reached when: the
envelope did not parse (a prose flow, or a truncated answer, which already surfaces
its own error); the envelope claims nothing; or a call ran whose outcome never
arrived, leaving the ids it returned unknown and the claim undisprovable.

### What it costs

Nothing on the parity target: with no `LANGFLOW_*` configured this code is not
reached at all. Within the Langflow path the cost is a rare correction after the
tokens have already streamed — the guard is a correction, not a prevention, because
the envelope is only complete once the answer is on screen. The user sees the reply
and then an error telling them it did not happen, which is the honest ordering
available.

### What was verified, and how

The negative control was made real rather than argued. A stand-in agent served the
backend two turns carrying the **same** envelope and the same prose, differing only
in whether a tool call backed the claim:

| Agent | Frames | Persisted |
| --- | --- | --- |
| claim with no tool call | `sources → token → error(unverified_action) → done → quota` | the user turn only |
| same claim, real `createTask` result | `sources → tool_call → tool_result → token → done → quota` | assistant turn, prose intact |

`AiFabricatedActionTests` pins both directions plus the silent cases; seven of its
eighteen tests were confirmed to fail with the guard disabled.

---

## 10. Money exists here and not in Node

**Decided:** the user, when asked where the financial page should get its data —
"full stack: extract + store", knowing it departs from the Node reference.
**Status:** implemented. `GET /me/finance/summary`, plus an `amount` on tasks,
scanned documents and scan candidates.

### What Node does

Nothing. There is no monetary field anywhere in `server/src/models` — not on
`Task`, not on `ScannedDocument`. The only `amountCents` in the reference is on
the subscription `Invoice`, and `GET /me/billing/invoices` is a deliberate stub
returning `[]`.

A bill's total does reach the Node database, but only as prose inside
`documentSubtitle` — the row-copy string the AI writes most-important-token-first
(`"Due July 30 · $142.37"`). That is a caption, not a quantity: it cannot be
summed, compared or converted, and it is absent whenever the model chose to spend
those 120 characters on an account number instead.

### What .NET does

Stores money as a value: whole minor units plus an ISO 4217 code, with a source
(`ai` | `user`) and a direction (`out` | `in`).

| Field | On |
| --- | --- |
| `amount` | `TaskDocument`, `ScannedDocumentDocument`, `ExtractedTaskCandidateDocument` |
| `amountDueAt` | `ScannedDocumentDocument` |

The document vision pass reports `totalAmount` / `currency` / `direction` /
`amountDueAt` alongside the fields it already returned — the same single call, so
the extra data costs nothing. `GET /me/finance/summary` aggregates the result.

### Why this is not a parity bug

**It is additive and nullable everywhere.** No existing row gains a field, no
existing response changes shape, and `amount` is absent from every document the
reader found no figure in — which is most of them. A parity differ comparing the
two servers on any pre-existing document sees identical bytes. The new key can
only appear on a document extracted by a build that has this code, and Node has
no such document to disagree about.

**The endpoint is new, not changed.** Node answers `GET /me/finance/summary` with
its 404 HTML (`Cannot GET /me/finance/summary`). A harness row for this operation
would be asserting that a feature does not exist.

### The thing to be careful about

The frontend's `/money` page reads this endpoint. **Pointing the app back at the
Node server on `:4000` gives that page a 404**, and the dashboard's money card
disappears with it. That is the honest failure — an empty page would claim the
user has no money — but it is a real constraint on switching backends, and it is
the first user-visible feature that cannot fall back to the reference.

### Why the aggregation is C# and not the agent

Every figure is computed from indexed Mongo reads and summed in memory. The
summary is a per-render endpoint on a page the user navigates back to, and
anything per-render that calls a model is a cost bug waiting to happen. The only
AI in the feature is the extraction pass that was already running.

---

## 11. `GET /me/reminders/upcoming` carries an `urgencyScore` Node does not send

**Decided:** Phase 1 of `Steward/docs/smart-reminder-conflict-spec.md` — "priority
becomes load-bearing". Scope was deliberately held to ordering and ranking; nothing
in this change can move, add, drop or delay a reminder.
**Status:** implemented. Adds no parity row and changes no status code. Full .NET
suite **1783 passed / 0 failed**, three consecutive runs, up from 1692 at baseline.

### What Node does

`server/src/routes/me.reminders.ts` builds each entry in plain JavaScript with six
keys — `id`, `taskId`, `title`, `at`, `kind`, `dueAt` — and sorts the flattened list
by `at`. Priority is read nowhere in the reminder path: not in `leadTime.ts`, not in
`planReminders.ts`, not in `reminderWorker.ts`.

`reminderWorker.ts` iterates `Task.find(...)` with **no sort clause at all**, so when
several reminders come due in the same 30-second tick, the order the user meets them
in is whatever order Mongo returned the documents in.

### What .NET does

Adds `ReminderUrgency` (`BLL/Kernel/Reminders/`), a pure scoring function, and uses
it in two places:

- **`ReminderTick` ranks a batch before writing it.** Entries are flattened across
  tasks, scored, and written least-urgent-first — reversed, because both the in-app
  feed (`{createdAt: -1}`) and the OS tray show newest first, so the last row written
  is the one on top.
- **`UpcomingReminderDto` gains `urgencyScore`**, a double in `[0, 4]`. Advisory
  only: the array is still ordered by `at` and still capped at the soonest 60.

The score is `priorityRank + deadlinePressure`, where pressure is how far the
reminder has travelled through the warning window `ReminderLeadTime` already assigns
that kind of matter — 0 at the heads-up, 1 at the deadline, clamped when overdue.
Normalising against that table is how the spec's `task_domain` term enters without
inventing a ranking of domains against each other.

### Why

The spec's case, restated: a due-date-only system cannot tell "renew the car
insurance sometime" from "renew it or the policy lapses", and firing both as
equal-weight pings is how notification fatigue is manufactured. Priority is already
inferred from the user's own words at every capture point and already surfaced as
`priorityRank` on the task DTO — it was simply never read once a reminder existed.

Ordering was chosen as the whole of Phase 1 because it is the only part with no
downside: no reminder can be lost, delayed or suppressed by a scoring bug. The two
tempting extensions were both rejected for this phase — re-ranking the 60-entry cap
can silently evict a near-term reminder on a busy account, and on iOS without push
that list is the *only* delivery path; and letting priority modulate lead time makes
the deterministic floor depend on a field a language model infers.

### A real defect this surfaced

The ranking was initially thrown away under load. `createdAt` is stamped per row from
`DateTime.UtcNow`, BSON stores milliseconds, and the feed sorts on `{createdAt: -1}`
with no tie-break — so two rows written inside the same millisecond come back in
arbitrary order. Against a local Mongo that happens routinely, and it happens most in
exactly the dense batches where ordering is the point.

Found by a flaky test, not by review. The stamp is now strictly increasing across a
batch: truthful whenever the clock has moved on, nudged a millisecond when it has
not, never backwards and never into the past. Pinned by
`ReminderWorkerTests.gives_every_row_in_a_batch_its_own_millisecond`, which asserts
distinctness rather than order so the failure reads as its true cause.

### What it costs

- **One key on one ported response.** The harness row `reminders-upcoming`
  (`tools/parity/scenarios/60-clarifications-digest.yaml`) runs against an account
  whose only task — `Pay council tax` — carries no `dueAt`, so no reminder is ever
  planned and both servers answer `{"reminders":[]}`. **That is luck, not design:**
  give that scenario a dated task and the row diverges on this key. The key set is
  pinned instead by
  `NotificationEndpointTests.carries_exactly_the_ported_keys_plus_the_one_documented_addition`.
- **Parity was not re-measured end to end.** The Node reference tree
  (`Steward/server`) is currently deleted from that repo's working tree, so `:4100`
  cannot be booted without restoring it. The claim above is read off the scenario
  file and the two implementations, not off a harness run.
- **Notification `createdAt` values within one tick are now synthetic to the
  millisecond.** Nothing reads them as a measurement; the feed and the relative-time
  label both only need ordering.
- **No client reads the new field yet.** `Steward/lib/notifications/syncReminders.ts`
  ignores it, as TypeScript does with any unmodelled key.

### How to revert

Delete `ReminderUrgency.cs` and its two test files, drop `UrgencyScore` from
`UpcomingReminderDto` and the projection, and restore `ReminderTick`'s nested
`foreach (var task in due)` loop with `writtenAt = DateTime.UtcNow`. Doing so
reinstates the arbitrary ordering above — including for the users whose batches are
dense enough for it to matter.

---

## 12. A reminder's final nudge fires a working duration BEFORE the deadline

**Decided:** Phase 2 of `Steward/docs/smart-reminder-conflict-spec.md` — "auto-detected
windows". Scope held to the reminder schedule; the estimate stored on a matter is not
touched, and neither is the daily digest.
**Status:** implemented. Full .NET suite **1821 passed / 0 failed**, four consecutive
runs, up from 1783 after Phase 1. Verified end to end against a live server: five
matters sharing one deadline, each final nudge landing its own resolved duration
early.

### What Node does

`leadTime.ts` schedules the second entry at exactly `dueAt`:

```js
out.push({ at: due, kind: 'due' })
```

Node has the estimate data — `ESTIMATE_BUCKETS`, `snapToEstimateBucket`,
`TaskEstimate {minMinutes, maxMinutes, source}` — and reads it in exactly one place,
the daily digest total. Nothing in the reminder path consults it, and there is no
fallback for a matter that has none.

### What .NET does

Adds `ReminderDuration` (`BLL/Kernel/Reminders/`), which resolves the minutes a
matter needs: its own `estimate.maxMinutes` when it has one, else a keyword default,
else a domain default, else 30. `ComputeRules` now takes that duration and places the
final entry at `dueAt - duration`. `RefineWithAiAsync` clamps its suggestions to the
same instant, so both scheduling paths agree on where the last useful nudge is.

The keyword rows are the SAME eight patterns the lead-time table uses, matched once
through `ReminderLeadTime.MatchKeyword` rather than restated — the two tables answer
different questions about the same shapes of matter, and duplicated regexes would
drift the first time either was edited.

`durationMinutes` is a required parameter, not a defaulted one: a caller that has not
thought about the window should have to say so.

### Why

The final nudge used to arrive at the one moment it could no longer be acted on.
Telling someone at 17:00 that a two-hour tax return was due at 17:00 is an
accusation, not a reminder — and "renew the insurance sometime this month" versus
"renew it in the next twenty minutes or the policy lapses" is the exact failure the
spec opens with.

**The default is derived on read and never stored.** Persisting one would put an
`estimate` object on every task response where Node sends none, and add a third value
to a `source` enum Node defines as `['ai','user']` — a divergence across
`30-tasks-core.yaml` and `40-tasks-bulk.yaml`, the two most heavily covered scenario
files. Deriving it keeps `GET /me/tasks` byte-identical and confines the change to
the reminder schedule, which is inference all the way down already.

**Why a default is needed at all**: almost no matter carries an estimate. Neither the
Langflow planning agent (`PLANNING-AGENT.md` has no such field) nor the voice
extractor (`PlanningVoiceExtractor` never sets it) produces one, so only a hand-typed
estimate or a Gemini document scan ever populates it. Without a fallback, window-aware
scheduling would apply to a rounding-error fraction of real matters.

### What was deliberately NOT changed

`DailyDigestComputer.ReadEstimate` still contributes ZERO for an unestimated matter,
so `estimatedMinutesToday` continues to under-report a day of unestimated work. That
is not an oversight — it is an argued decision carried over from Node and stated in
that method's own summary: *"a fabricated number in a digest whose whole premise is
that it has none is worse than a low total."* A guess is acceptable where the system
already guesses (the entire lead-time table) and not where a headline number is read
as fact. Revisit it deliberately or not at all.

### What it costs

- **`GET /me/reminders/upcoming` reports different `at` values than Node** for any
  dated reminder — earlier by the resolved duration. The harness row
  `reminders-upcoming` still matches because its account's only task carries no
  `dueAt` and both servers answer `{"reminders":[]}`, the same clipped corner Phase 1
  relies on. Any scenario that gains a dated task diverges on both this and
  `urgencyScore`.
- **Phase 1's urgency scores moved.** A final nudge is no longer AT the deadline, so
  its deadline pressure is now marginally below 1 — a `low` matter reads `0.994`
  rather than `1.0`. This is a strict improvement: the pressure term was previously
  degenerate, taking only the values 0 and 1, because the rules floor placed entries
  only at the two ends of the window. It is now continuous, which is what §3.1
  described.
- **Notifications for long matters arrive earlier in wall-clock terms.** A 4-hour
  matter now nudges 4 hours before its deadline. No EXTRA notification is created —
  the count per matter is unchanged at two, which the anti-nag position in
  `reminderWorker`'s clarification-settling comment requires.
- **Snooze is untouched.** `SetSnoozeReminderAsync` fires exactly at the moment the
  user named; subtracting an estimate from a time a person chose would move the one
  instant the system must not second-guess. Pinned by
  `ReminderPlannerTests.fires_a_snooze_once_at_the_snooze_moment_with_no_window_applied`.

### How to revert

Delete `ReminderDuration.cs` and its tests, drop the `durationMinutes` parameter from
`ComputeRules` so the final entry returns to `due`, revert `RefineWithAiAsync`'s clamp
to `d <= due`, and drop `MatchKeyword`/`MatterKeyword` from `ReminderLeadTime`. Doing
so restores a schedule whose last nudge always arrives too late to act on.

---

## 13. A conflict is window overlap, not "the deadlines are close"

**Decided:** Phase 3 of `Steward/docs/smart-reminder-conflict-spec.md` — interval-merge
conflict detection with an urgency-driven resolution.
**Status:** implemented. Full .NET suite **1849 passed / 0 failed**, four consecutive
runs, up from 1821 after Phase 2. Verified end to end against a live server through
`POST /me/conflicts`.

### What Node does

Nothing. There is no conflict detection anywhere in `server/src` — no clash rule, no
duplicate rule, and no `/conflicts` route of any kind. `ConflictService`,
`SlotSuggester` and the three endpoints are .NET-only, from the `ai_flow_V4` split.

**So this entry records a behaviour change, not a divergence from a reference.** It is
here because the change is large, four call sites depend on it, and the surface it
rewrites had no test of any kind to describe what it used to do.

### What .NET did before this

`|other.dueAt - candidate.dueAt| <= 2 hours` — a fixed symmetric radius, identical for
a ten-minute bill and a four-hour tax return, with the reason string *"Scheduled
within two hours of this."* That is exactly the "is the date close" test the spec §3.3
names as **not** conflict detection, and it was wrong in both directions at once.

### What .NET does now

`MatterWindow` (`BLL/Kernel/Reminders/`) gives every matter the span it occupies —
`[dueAt - duration, dueAt]`, the same interval Phase 2 schedules the final nudge
against — and two matters conflict when those spans overlap after one is given 15
minutes of breathing room.

Measured on a live server against the same three drafts:

| draft | old rule | now |
| --- | --- | --- |
| 10-min bill, 90 min before another 10-min bill | clash | **clear** |
| 45-min chore overlapping a 45-min repair | clash | clash |
| two 4-hour jobs, deadlines 3 hours apart | **clear** | clash |

`MatterConflict` also gained `urgency`, `otherUrgency` and `yields`, so the answer
names which of the two should move rather than reporting that a problem exists.
Both sides are scored at NOW, not at their own deadlines: scored at its own deadline
every matter reads as maximally pressing and the comparison carries no information.
A tie leaves the incumbent alone — it is the commitment the user already made.

### Why the buffer is fixed at fifteen minutes

Touching intervals are not really fine: nothing accounts for travel, for finding the
right document, or for the minutes a person needs between one thing and the next. It
is deliberately not scaled to the longer matter — a boundary that moves per pair
cannot be explained to a user in one sentence, and this rule has to be predictable
before it is clever.

### What it costs

- **Fewer clashes reported on lists of short matters, more on lists of long ones.**
  That is the intended correction, but it IS a visible change for existing users: a
  list of bills an hour apart stops warning, and two long jobs that never warned start
  to.
- **`ClashesWithin` changed signature.** It now takes the candidate, because an
  overlap test needs the candidate's own duration and not just an instant. This is the
  predicate `SlotSuggester` proposes free times through, and the endpoint's own comment
  requires that "a suggestion cannot be refused the moment it is taken" — pinned by
  `ConflictServiceTests.agrees_with_the_check_about_which_instants_are_free`.
- **`CheckAsync` takes a `MatterCandidate`** (title, domain, priority, estimate)
  instead of a bare title, because duration and the yields verdict both need more than
  a title. All five call sites were updated; `ConflictPreviewBody` gained optional
  `domain` and `priority` so a draft can be checked as it will actually be saved.
- **`ConflictService.ClashWindow` is gone.** `VoiceAutoFilePolicy` derived its "Later
  that day" offer from it; that offer keeps its previous size through
  `MatterWindow.SuggestedShift`, deliberately decoupled so a detection change cannot
  silently move a UI affordance.
- **No parity row, no contract operation.** Node answers its HTML 404 for all four
  routes.

### 13.1 `GET /me/conflicts` — the list, added later

The three routes above all answer a question about ONE matter, so a clash was only
ever discoverable by the surface that happened to create or edit it: a chat card, a
voice pop-up, a warning in the create sheet. Every one of those is a moment, and a
moment can be missed — the pop-up fades, the card scrolls away. Nothing answered
"what is clashing right now?", so there was no second place to find it.

`GET /me/conflicts` is that place. It calls `KnowledgeAgentService.ScanAsync` with no
date bound; the daily briefing calls the same method bounded to the end of the user's
today. The scan was **extracted from `BriefAsync`, not copied** — it is the pair-dedupe
and the `TimeClash` filter that the briefing already ran, and a second copy of a rule
four surfaces depend on is exactly what this section exists to prevent.

**It is derived, never stored.** There is no conflicts collection, no row written when
voice or chat or a scan causes an overlap, and therefore no eraser and no index. A
conflict is two saved matters wanting the same time, so asking again is what keeps the
answer true — which is also why a clash resolved on any surface disappears from every
other one for free, and why the list covers sources it has never heard of.

**It reports the PAIR.** `MatterConflict` names only the matter that was run into,
because every existing caller already knows which matter it is asking about. A list has
no such context and either side may be the one the user moves, so the response carries
`a`, `b` and `yieldsTaskId` — the last read off `MatterConflict.Yields`, which is stated
from the scanned matter's point of view and is mapped in exactly one place
(`MatterClash.YieldsTaskId`).

Pinned by `ConflictScanTests`: a pair is reported once rather than from both ends, the
`until` bound is honoured, an undated matter clashes with nothing, and the side offered
to move is the one the urgency rule chose.

### 13.2 A closed question takes its notification with it

The voice worker writes one `uncertainty` notification per question it raises, and
the reference never removes a notification once written. Here they are deleted when
the question reaches a terminal status — answered, kept, dropped, or settled by the
user giving the matter a date directly.

**Why the divergence is worth it.** The row's only job is to lead the user to the
question. Once the question is closed it leads nowhere: tapping it opens the card
stack, which renders its "All clear" celebration because there is nothing left to
answer. Reported as the feature being broken, which is a fair reading — a bell that
lists something, and takes you somewhere that says there is nothing, is lying about
one of the two.

**Deleted rather than marked read**, because read/unread controls the bell's COUNT
and not whether a row is listed. Marking it read leaves the same dead link in place,
quietly.

**Deferred questions are deliberately untouched.** "Skip" goes through the same
close-out with a `deferredUntil` and no status change; the question is still open and
the bell is exactly where the user should meet it again.

Cleared in `ClarificationRepository.CloseOutAsync` for the routes, and in
`ClarificationCascade.SettleDateQuestionsAsync` for the date-set path — which reads
the ids before its update, because `UpdateMany` does not report what it touched.

### A pre-existing behaviour left alone

`SlotSuggester` walks each day from the start of the matter's part of day rather than
from the requested time, so it can propose a slot EARLIER than the one asked for. It
is unchanged here and its suggestions are still validated against the same pool, but
it surfaced during the Phase 3 proof and is worth a deliberate look.

### How to revert

Delete `MatterWindow.cs` and its tests, restore `ClashWindow = TimeSpan.FromHours(2)`
with the `(other - due).Duration() > ClashWindow` test, drop `Urgency`/`OtherUrgency`/
`Yields` from `MatterConflict` and the endpoint mapper, and revert the five call sites
to passing a bare title. Doing so restores a rule that flags two quick errands ninety
minutes apart and misses two four-hour jobs that genuinely collide.

---

## Typed clarification answers (`{type:'custom'}` on resolve)

Ported 2026-08-17 (`CustomAnswerInterpreter`), faithful to
`server/src/modules/ai/resolveClarificationAnswer.ts` with two deliberate
divergences: the call walks `PlanningOptions.ModelChain` with a per-attempt
timeout (Node had one hard-coded model and no timeout), and the model identity
comes from `PlanningOptions`, so Node's `gemini-2.5`-only `thinkingBudget: 0`
clause has nothing to apply to. The availability gate is
`PlanningOptions.IsConfigured`, NOT `AiAvailability` (`GEMINI_API_KEY`) — the
seam this feature rides is the Gemini-direct planning one, and satisfying the
old gate by setting `GEMINI_API_KEY` would push six honest 503s elsewhere into
`NotWiredHere` 500s.

---

## 14. Reminders are actually scheduled, and the schedule is adaptive

**Decided:** `Steward/reminder-scheduling-logic.md` §3.1–§3.5. Scope held to the
reminder schedule and the two paths that deliver it; the worker, the per-entry claim
and the push/local split are untouched.
**Status:** implemented. Full .NET suite **1882 passed / 0 failed**, two consecutive
runs, up from 1849 before the change.

### The part that is not a divergence but a defect

`ReminderPlanner` had three production callers: the clarification round-trip
(`ClarificationHoldService`, `ClarificationTaskUpdater`) and the Google/ICS importer
(`ExternalMatterReconciler`). It was **not** called by `POST /me/tasks`,
`PATCH /me/tasks/{id}`, `POST /api/planning/commit`, or the bulk endpoints.

`TaskWriteService.CreateAsync` set `Reminders = new List<ReminderEntryDocument>()` and
nothing ever filled it in. The chat agent's `CreateTaskTool` posts to `/me/tasks`, and
so does the app — so **the main way a person files a reminder produced a matter that
could never notify anybody.** Everything downstream was correct and never ran.

This is ported from Node, which has the same gap; `ClarificationHoldService` already
carried a comment saying so ("Note POST /me/tasks does NOT do this, in either
server"). It is fixed here rather than reproduced, because a reminder nobody is told
about is the one thing the product exists to prevent.

`SetSnoozeReminderAsync` had **zero** production callers on either server, so snoozing
wrote `status` and `snoozedUntil` and left the matter carrying only already-fired
entries — "later today" meant "never". Also fixed.

Voice-note and document-scan seeds hardcoded `kind: "list"` even when they carried a
`dueAt`, and `ComputeRules` returns nothing for a list item — so a due date lifted off
a scanned bill could never fire. They now derive `kind` from `dueAt`, as `POST
/me/tasks` and the chat tool already did.

### What .NET now does that Node does not

- **Adaptive lead time (§3.1).** `ReminderLeadTime.AdaptiveLeadAt`: when the deadline
  is nearer than the base lead time, the heads-up is placed at half the REMAINING
  time instead of being dropped. Node computes `due - leadDays` and silently discards
  the entry when that is in the past, so car insurance filed 20 days out against a
  30-day rule got no warning at all before the deadline itself.
- **Priority decides how many nudges (§3.2).** low 1, normal 2, high 3, urgent 4.
  Priority was previously read nowhere in this path — only in `ReminderUrgency`, which
  affects delivery ORDER. New kinds `midpoint` and `final24h`.
- **Quiet hours (§3.3).** `ReminderQuietHours` moves a nudge out of 21:00–08:00 local.
  The planner had no timezone at all; the zone was read only at fire time, and only to
  word the date. Two guards the spec does not state are enforced: a shift is refused
  when it would land in the past (the evening rule moves backwards, and the worker
  matches `at <= now`), and when it would overtake the entry it precedes.
- **The deadline nudge is never moved by quiet hours.** A 07:00 appointment lives
  inside the window, and pushing its nudge to 08:30 delivers it after the appointment.
  This is a deliberate departure from a literal reading of §3.3.
- **Overdue cadence (§3.5).** A finite, pre-scheduled sequence of `overdue` follow-ups
  by priority: none / +2d / +1d,+4d / +1d,+3d,+7d. Entries whose instant has already
  passed are dropped, so filing something a fortnight late produces ONE nudge rather
  than a burst of seven.
- **Re-planning preserves `firedAt`.** `ReminderPlanner.CarryForwardFired` matches on
  the INSTANT, so editing priority on a matter whose heads-up already went out does
  not re-buzz the user, while moving `dueAt` re-arms the whole schedule as it should.
- **The timezone read is cached per scope.** A bulk snooze covers up to 500 matters on
  one account; without it that was 500 identical `users` queries.

### Deliberately NOT done

§3.2 and §3.4 say the final alert fires "exactly at `DueAt`". It fires at
`DueAt - duration`, per **§12 above**, which is a considered decision this change does
not reverse: telling someone at 17:00 that a four-hour job was due at 17:00 is an
accusation, not a reminder. Read the spec's "at `DueAt`" as "the deadline nudge".

### Copy

`ReminderNotificationText.Body` gains three strings for the new kinds. The `lead` and
`due` strings are byte-for-byte parity fixtures and are unchanged. The device's local
channel carries the same three in `Steward/lib/i18n/messages/{en,ar}/notifications.json`
— both channels must agree, or the same reminder reads differently depending on
whether push is available.

### How to revert

Drop the `ReminderPlanner` argument from `TaskWriteService`, `BulkService`,
`VoiceNoteTaskPersistence` and `DocumentScanReviewService`; delete
`ReminderQuietHours.cs`; restore `ComputeRules` to its three-parameter form and its
`leadAt > now` guard; restore `ReminderKinds` to `{lead, due, ai}`. Doing so restores a
server that plans no reminders for anything a user files through the app or the chat
agent.

---

## 15. The agent's grounding stops hiding the future, and stops answering over a dead lookup

Four related changes to what the planning agent is handed and what it is allowed to say.
All four were driven by one reported symptom — *"I asked what I have on the 28th; it
showed one of three, at the wrong time"* — and by reproducing it against the live flow.

### 15.1 `MY TASKS` sorts undated LAST (Node sorts them first)

Node:

```js
Task.find({ userId, ...notDeleted(), status: { $in: ['open','snoozed'] } })
    .sort({ dueAt: 1, createdAt: -1 })
    .limit(TASK_CAP)
```

Mongo orders **missing fields before every value**, so `dueAt: 1` means "the dateless
backlog first, then the soonest deadlines" — and the cap then truncates from the far end.

Measured on the seeded `demo@kitto.test` (143 open matters): the twenty rows the agent
received were **14 undated plus 6 dated, and all six dated rows were in the past** —
2026-06-09 through 2026-08-05, against a clock reading 2026-08-20. Not one upcoming
matter could reach the prompt, at any cap, for any account carrying twenty-odd undated
items. An agent asked "what do I have on Friday" was structurally incapable of answering
from its own grounding.

`AiGroundingRepository.ListForPromptAsync` now substitutes `TaskQuery.FarFuture` for a
missing `dueAt` before sorting — the same sentinel `TaskQuery.ListAsync` already applies
for the REST list and the UI. A plain `Find` cannot express this (no sort direction puts
missing fields last), so it is an aggregation with `$ifNull` + `$sort` + `$limit`.

Side effect worth stating: **the agent and the matter list now agree about what "first"
means.** They did not before.

### 15.2 `dueAt` reaches the agent in the user's zone, not as a `Z` instant

Node prints `t.dueAt.toISOString()`. So did this port. Meanwhile `CURRENT DATE` beside it
carries the user's offset, and **no rule anywhere in the 572-line system prompt converts
one into the other before an hour is read back to the user**.

The agent usually manages anyway — verified live: given three Cairo matters at `09:00Z`,
`14:00Z` and `18:30Z` it answered 12:00 PM, 5:00 PM and 9:30 PM, all correct. But the one
stored transcript where it read a time back perfectly turned out to be reading its **own
earlier sentence** in the same thread ("I've set that for Friday at 10:00 AM"), not the
data. A matter that already existed has no such sentence to lean on.

`TaskGrounding.FormatDue` now renders `2026-08-28T12:00:00+03:00`. Same reasoning
`DateGrounding.FormatNow` already records for the clock: hand the agent a bare instant and
it invents an offset rather than complaining.

### 15.3 `DATE REFERENCE` anchors every weekday (Node anchors two phrases)

Node's anchor block covers `this weekend` and `end of this month`. Its own comment says
why anchors exist: *"Literal anchors for common relative phrases the model otherwise
guesses."* `next <weekday>` — the commonest relative phrase after today/tomorrow — was
never anchored, so the 14-day table hands the model **two of every weekday and labels
neither**, while the prompt tells it to "resolve phrases by FINDING them there" and then
forbids the arithmetic it must fall back on.

Measured non-deterministic: two runs of the identical prompt
`ذكرني يوم الاثنين بموعد الدكتور` on Monday 2026-08-17 resolved to `2026-08-24` in one and
`2026-08-17` in the other. Same input, same day, same table. Both transcripts are in the
seeded `aiconversations`.

`BuildDateReference` now emits seven extra lines, one per weekday, resolving both
phrasings to the soonest upcoming occurrence. The 14 table rows and Node's two anchors are
unchanged and still asserted byte-for-byte (`matches_node_buildDateReference` became a
superset assertion rather than an equality one).

### 15.4 A failed lookup may no longer be reported as an empty calendar

**The bug behind the report.** Reproduced live: asked "What do I have on Friday August
28?" the agent built a *correct* range, `queryTasks` returned
`{"ok": false, "error": "misconfigured"}`, and the reply was
*"I don't see anything on your schedule for Friday, August 28."*

Note what is NOT wrong here — the range, the timezone arithmetic, and the backend filter
were all verified correct. The failure is entirely in how a dead lookup was phrased.

The prompt made it worse. Section 9 said: *`If your tools cannot answer, say "I don't have
that in your data."`* That sentence is indistinguishable from a real empty result.

Three layers now stop it, because any one alone can be defeated:

1. **Tool payload** — every failure return in all eleven tools carries an
   `agent_directive` spelling out that the call returned no data and that an absence claim
   is forbidden. A 401/403 gets a distinct directive naming the expired session.
2. **Prompt** — Section 9's sentence is replaced; the `ok: false` rule points at
   `agent_directive`; FINAL CHECK gains items 10 (no absence after a failed read) and 11
   (account for every row returned).
3. **`FailedLookupGuard`** — server-side, the sibling of `FabricatedActionGuard`. It reads
   the tool's own envelope rather than the call status, because a tool that reaches our
   API and gets a 401 returns HTTP 200 carrying `{"ok": false}` — Langflow calls that a
   success and the call is recorded `executed`. It emits a `lookup_failed` error frame,
   and withholds the prose **only when no other tool succeeded** (a failed read alongside a
   real `createTask` keeps its receipt).

**`text_reset` is now emitted, for the first time on this server.** Both guards
withheld the answer from the *database* while leaving the retracted sentence on the
user's *screen* — tokens go out live, so "I don't see anything on your schedule" was
already rendered by the time either guard could judge the turn, and the error frame
landed directly beneath it. The client has handled `text_reset` since the frontend
shipped (`lib/ai/draft.ts`) and its contract note says the server "retracts it rather
than leaving the guess standing next to the correction" — but nothing on this server
ever sent one. `FabricatedActionGuard` gains the same fix for free.

It does **not** read the prose. Deciding whether a sentence asserts absence is a language
problem in two languages and the product ships Arabic; the structural question — "was the
agent handed a failure?" — has one answer regardless of phrasing.

**Tried and reverted:** refusing the whole run when no bearer token is present. It turns
`"hi"` into a 500, and a greeting calls no tools. The missing token is logged instead
(`ai.no_tool_token`), and the guard handles the answer.

### 15.5 Also in this change, not divergences

- `queryTasks` gains `due_on=YYYY-MM-DD`, expanding to that whole **local** day using a
  server-injected `utc_offset` (never model-visible, same channel as `access_token`).
  The agent was already building correct ranges by hand; this removes the boundary
  decisions from the model for the commonest question the product answers.
- The `limit` help text stopped claiming "at most 15 rows come back" — the endpoint
  clamps 1–200, fallback 50. The false cap pushed the model to over-narrow filters.
- Per-tool outcome logging (`ai.tool`, `ai.lookup_failed`). There was **no server-side
  trace of tool outcomes at all**: a `queryTasks` returning `{"ok": false}` looked
  identical in the logs to one returning the user's whole list. Names and statuses only —
  arguments and results carry the user's matters.

### How to revert

Restore `ListForPromptAsync` to `.Sort(Sort.Ascending(DueAt).Descending(CreatedAt))`;
drop the `timezone` argument from `TaskGrounding.BuildTaskBlock` and return
`JsIsoDateTimeConverter.ToIso(dueAt)`; delete `WeekdayAnchors` from `DateGrounding`;
delete `FailedLookupGuard.cs` and its call site in `RunTurnAsync`; restore `langflow/planning-agent.v4.json` from git AND re-import it into Langflow (see below). Doing so restores a
server whose agent cannot see any future matter it has more than twenty undated ones, and
which reports a broken lookup as an empty day.

---

## 16. An absent timezone means `Africa/Cairo`, not UTC — and signup writes it

**Decided:** slice tz-default, after a user report that task times, reminders and the
briefing all read three hours behind Egyptian wall-clock time.
**Status:** implemented. `AppTimeZone` is the single source of truth; twelve
independent UTC fallbacks now route through it.

### The bug this replaces

`UserProfileDocument.Timezone` is `string?`, and `UserProvisioningService.CreateAsync`
never set it — so **every account created by this server had no timezone at all**. That
was survivable only because each consumer invented its own answer for the absent case,
and all twelve of them invented UTC:

| Site | Was | Consequence for an Egyptian account |
| --- | --- | --- |
| `TaskQuery.ZoneOffsetMinutes` | `return 0` | "today" / "this week" cut at 02:00 or 03:00 local |
| `DateGrounding.ToLocal` | `now.ToUniversalTime()` | agent told the wrong clock; every derived `dueAt` 3h early |
| `ReminderUserTimezoneReader.DefaultTimezone` | `"UTC"` | notification names the wrong DAY across local midnight |
| `DigestClock.LocalDateKey` | `TimeZoneInfo.Local.Id` | digest cache key = the SERVER's date, not the user's |
| `DailyDigestComputer` | `timezone ?? "UTC"` | busiest-day bucketed against a different calendar than it was labelled with |
| `FinanceSummaryService.ResolveZone` | `TimeZoneInfo.Utc` | a 23:30 spend on the 31st booked to the wrong month |
| `FinanceSummaryDto.Timezone` | `"UTC"` | client told UTC while the months were cut elsewhere |
| `KnowledgeAgentService.ResolveZone` | `TimeZoneInfo.Utc` | briefing rolls over mid-evening |
| `CustomAnswerInterpreter.ResolveZone` | `TimeZoneInfo.Utc` | "Thursday morning" resolved 3h out |
| `PlanningService` prompt | `"UTC"` | extraction grounded on the wrong clock |
| `GeminiDocumentExtractor` prompt | `"UTC"` | same, for scanned documents |
| `AdminCustomerRepository` signups-per-day | `"UTC"` | daily totals unreconcilable with the rest of the page |

None of it raised, logged or rendered anything unusual. Egypt is UTC+02:00 in winter and
UTC+03:00 under DST, so the whole product was simply two or three hours early.

`DateGrounding`'s own doc comment had already measured the symptom and left it standing:
*"Measured on the live stack with `Africa/Cairo`: hand the agent a bare `yyyy-MM-dd` and
it invents `+00:00` rather than complaining, putting every derived `dueAt` three hours
early with no error anywhere."* The fix for the format shipped; the fallback that
produced the same number did not.

### What changed

1. **`AppTimeZone`** (`DAL/Kernel/Time/AppTimeZone.cs`) — `DefaultId = "Africa/Cairo"`,
   plus `Resolve` (→ `TimeZoneInfo`) and `ResolveId` (→ zone name). Every site in the
   table above calls one of them.
2. **Signup provisions it.** `UserProvisioningService.CreateAsync` writes
   `Timezone = AppTimeZone.DefaultId`, so the fallback stops being the normal path.
3. **A stored zone still wins, unconditionally.** `Resolve` returns the caller's zone
   whenever the host can resolve it; the default is reached only for absent, blank or
   unresolvable values. A user on Europe/Berlin is not pulled back to Cairo.

### Why a named zone and not an offset

Egypt reinstated daylight saving in 2023 — last Friday of April to last Thursday of
October. A hardcoded `+02:00` or `+03:00` is wrong for roughly half of every year, and
wrong in the way that surfaces months after it ships. `AppTimeZoneTests` pins both
offsets against real 2026 instants for exactly that reason.

### Why this supersedes §7's ruling

§7 argued that an unusable timezone should mean **UTC** rather than the server's own
zone, on the grounds that `TimeZoneInfo.Local` is "an accident of where the process
runs". That reasoning stands and is unchanged — the conclusion was just incomplete.
UTC is *also* an accident, and a less defensible one: it is nobody's wall clock. The
requirement §7 actually established is that the fallback be **machine-independent and
self-consistent**, and `Africa/Cairo` satisfies both while additionally being the clock
this product's users are on.

§7's cost analysis said the difference is "reachable only by a caller that omits
`timezone` entirely". That was measured against the API surface and was correct there;
what it missed is that the *stored profile field* was empty for every account, so the
server-side readers — workers, digest, finance, reminders — took the fallback on every
single request. The blast radius was the product, not an edge case.

### What it costs

- **Parity:** on the absent-`timezone` path the port no longer matches Node's
  `timezone: 'UTC'` output. `/ai/ask` is 503 on the parity target, so no harness row
  reaches the grounding path; the finance, digest and counts paths now answer in
  `Africa/Cairo` where Node answers in UTC. Accepted: the alternative is a server that
  is provably wrong for its actual users in order to match a reference nobody runs.
- **One cache moved.** `DigestClock.LocalDateKey` is the digest cache key, so the first
  absent-`tz` digest after deploy is recomputed rather than read. That is what the cache
  is for.
- **Existing accounts are not migrated.** Rows written before this change still have
  `timezone: null` and now resolve to the default at read time, which is the intended
  outcome. Nothing needs backfilling; a backfill would only make the field explicit.

### What was verified, and how

`dotnet test` — 1975 tests, 1969 passing. Nine tests asserted the old UTC fallback
directly and were rewritten to assert the default, each keeping a note of what it used
to claim:

- `TaskQueryTests.a_missing_zone_falls_back_to_the_product_default` (was
  `…_falls_back_to_utc`)
- `AiDateGroundingTests.an_unusable_timezone_is_the_product_default_not_the_servers_own`
  × 4 cases (was `…_is_utc_…`)
- `AiDateGroundingTests.the_utc_offset_matches_the_one_on_current_date` — the
  `Not/AZone` and `null` rows moved `+00:00` → `+03:00`
- `LangflowProviderTests.falls_back_to_the_default_zone_rather_than_failing_the_turn_on_a_bad_timezone`
- `FinanceSummaryServiceTests.an_unknown_timezone_falls_back_to_the_default_rather_than_failing`

`AppTimeZoneTests` is new: 16 cases covering the id, that it resolves on the host at
all (a silent degrade to UTC is the failure being prevented, so it must not pass
quietly), both DST offsets, the five absent/unusable inputs, and that a stored zone
beats the default.

The six remaining failures are pre-existing and unrelated — CRLF-sensitive string
comparisons and one ICU zone (`CET`) this host does not publish. Confirmed by running
the suite against a stashed tree first: 18 failing before this change, and the six are a
subset of those 18.

### How to revert

Set `AppTimeZone.DefaultId` to `"UTC"` — every site routes through it, so that one edit
restores the old behaviour everywhere except the two places where the old code was
*differently* wrong: `TaskQuery.ZoneOffsetMinutes` returned a literal `0` and
`DigestClock.LocalDateKey` used `TimeZoneInfo.Local.Id`. Also drop
`Timezone = AppTimeZone.DefaultId` from `UserProvisioningService.CreateAsync`, or new
accounts will keep carrying an explicit zone that no longer matches the fallback.
