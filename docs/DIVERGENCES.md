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
`sourceQuote` clamp. Responds `201 {clarification, task, queueFull}`.

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
