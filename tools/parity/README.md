# Parity harness

A differential test harness that proves the .NET reimplementation is
**response-compatible** with the Node/Express reference.

The rule this exists to enforce:

> A route may not be cut over until its row in the matrix is green.

It replays the same ordered HTTP scenarios against both servers, normalizes
away the values that *must* differ (ids, clocks, tokens), and diffs what is
left. It cross-references the result against the frozen contract
(75 paths / 87 operations) so under-coverage is reported rather than hidden.

---

## 1. Prerequisites

* Node 22 (built-in `fetch`, ESM). No install step, no `package.json`, no
  dependency tree.
* `js-yaml`, resolved at runtime by walking up for `node_modules/js-yaml`.
  Override with `PARITY_JS_YAML=/abs/path/to/node_modules/js-yaml` if the
  harness is run from a checkout that does not have one.
* The frozen contract, auto-discovered by looking for
  `docs/contract/paths.auth.yaml` in this repo and in a sibling
  `Life-Admin-Autopilot-Backend/`. Override with `PARITY_CONTRACT_DIR` or
  `--contract`.

## 2. Start the two servers

### Reference (Node) — port 4100

Run it against a **throwaway** database, with **no AI key**. Never point it at
the Atlas cluster in `server/.env`.

```bash
cd Steward/server
MONGODB_URI="mongodb://127.0.0.1:27018/kitto_parity_node" \
GEMINI_API_KEY="" \
PORT=4100 \
NODE_ENV=development \
LOG_LEVEL=info \
npx tsx src/index.ts
```

`NODE_ENV` **must stay `development`.** Setting it to `test` would conveniently
disable rate limiting — but it *also* disables the document-scan worker, the
voice-note transcriber and the ICS/Google sync workers, so scans and voice
notes would never leave `pending` and the harness would be comparing a server
that does not behave like production. Budget the rate limit instead
(section 7).

### Candidate (.NET) — port 5100

Same shape: isolated database, no AI key. The harness runs fine before the .NET
server exists — every row simply reports `UNREACHABLE`.

```bash
cd Life-Admin-Autopilot-Backend
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS='http://[::]:5100' \
MongoDbSettings__ConnectionString=mongodb://127.0.0.1:27018 \
MongoDbSettings__DatabaseName=kitto_parity_dotnet \
Database__Provider=Sqlite \
dotnet run --no-build --no-launch-profile
```

**Bind `[::]`, and dial `localhost` on both sides.** Node's `server.listen(port)`
binds `::` in dual-stack mode; `ASPNETCORE_URLS=http://127.0.0.1:5100` is an
IPv4-**only** listener. With that bind the two servers disagree about the peer
address — Node reports an IPv4 client as `::ffff:127.0.0.1`, an IPv4-only Kestrel
reports a bare `127.0.0.1` — so every row echoing `req.ip` fails for an environment
reason rather than a code one. Section 9.3 explains why `ip` is deliberately
compared literally; this is the bind that makes that comparison honest instead of
noisy.

Keep the dial host the same on both sides as well. `localhost` and `127.0.0.1`
resolve to `::1` and `::ffff:127.0.0.1` respectively — both correct, but mixing
them across the two servers reintroduces the same false failure. `localhost` on
both is the default the flags already use.

Both `--reference` and `--candidate` should therefore be spelled `http://localhost:PORT`.

## 3. Run it

```bash
# Acceptance check: both sides point at the reference. Everything covered
# MUST pass — a server is always compatible with itself. Needs 32 auth
# rate-limit slots, so give it --max-wait (see section 7).
node tools/parity/run.mjs --self-test --max-wait 950

# The real thing.
node tools/parity/run.mjs

# Cheap subsets while iterating.
node tools/parity/run.mjs --self-test --only framework
node tools/parity/run.mjs --skip-tag slow
node tools/parity/run.mjs --list          # corpus + coverage, no requests
```

Exit codes: `0` everything covered passes, `1` at least one `FAIL` **or a
failing framework probe**, `2` the result is not trustworthy — a broken
scenario, an unreachable reference, a reference contradicting its own `expect`,
rows that never ran because the rate limit was exhausted, or rows that never ran
because the candidate could not be provisioned (`SETUP-FAILED`, section 8.1).

### Proving the harness can still fail

`--self-test` shows there are no false FAILures. The negative control shows
there are no false PASSes: it serves the `framework` scenario as a **correct**
port would — same statuses, same bodies, same twelve helmet headers — with one
planted defect per response.

```bash
# one command: start it, run the harness, assert every defect was caught
node tools/parity/selfcheck/bad-candidate.mjs --verify     # exits 0 only if all six were caught

# or the manual flow
node tools/parity/selfcheck/bad-candidate.mjs &
node tools/parity/run.mjs --only framework --candidate http://localhost:5199   # must exit 1
kill %1
```

Six planted defects, one per step:

| Step | Planted defect | Caught by |
| --- | --- | --- |
| `GET /health` | `uptime` typed as a string, plus an extra `version` property | structural walk |
| `GET /nope` | a JSON error envelope where Express serves HTML | the envelope check inside `statusOnly` |
| `PUT /health` | the same, on a method mismatch | the same |
| `GET /me/tasks` | 401 with the right code but the wrong literal message | error-literal comparison |
| `GET /auth/me` (lowercase `bearer`) | `invalid_token` where the reference says `missing_token` | error-literal comparison |
| `GET /auth/me` (correct `Bearer`) | a byte-correct body with a leaked `X-Powered-By` header | **union header diff** (section 5.1) |

`--verify` asserts each defect is reported *at its own step and on its own diff
path*, and that nothing else fails. Two things it exists to stop happening again:

* The `X-Powered-By` step is otherwise byte-correct, so it fails on the header
  and nothing else. That is the proof that candidate-only headers are visible.
* The two 404 defects were **silently un-armed** for a while. Those steps are
  `statusOnly` — the harness deliberately does not require Express's
  XSS-bearing HTML body — and `statusOnly` used to drop the entire body
  comparison, so the JSON envelope went unnoticed and the negative control
  quietly caught three of its five defects while the README still claimed five.
  `statusOnly` now still asserts "neither side is a JSON error envelope", which
  is exactly the property the scenario comment claims to be protecting.

The negative control must emit the correct helmet headers. If it did not, every
step would fail on the missing headers and "all six were caught" would be true
for the wrong reason.

Useful flags — `node tools/parity/run.mjs --help` for the full list:

| Flag | Purpose |
| --- | --- |
| `--self-test` | point both sides at `--reference` |
| `--only SUBSTR` | run scenarios whose name contains SUBSTR (repeatable) |
| `--tag` / `--skip-tag` | select by tag; `strict-ratelimit` is skipped by default |
| `--include-strict` | also run the strictAuthLimiter scenario |
| `--max-wait SECONDS` | on an unexpected 429, wait for the window and retry |
| `--mask-ip` | additionally mask client IPs in session listings |
| `--seed-candidate-user CMD` | provision the candidate's user with CMD instead of candidate signup (section 8.1) |
| `--no-auth-sweep` | skip the generated "every authed route returns 401" scenario |
| `--out FILE` | JSON report path (default `tools/parity/out/parity-report.json`) |

## 4. Reading the matrix

```
OPERATION                   ROUTE                                     STATUS       CODES SEEN / DECLARED
taskUpdate                  PATCH /me/tasks/{id}                      PASS         200,400 / 200,400,401,404,500
```

| Status | Meaning |
| --- | --- |
| `PASS` | every step exercising this operation matched on both sides |
| `FAIL` | at least one step differed — the port is wrong (exit 1) |
| `UNREACHABLE` | the candidate server did not answer (usually: not built yet) |
| `NOT-COVERED` | **no scenario references this operation at all** |
| `SKIPPED` | referenced only by scenarios that this invocation did not run |
| `RATE-LIMITED` | an unexpected 429 — the auth budget ran out, so these rows never really ran (exit 2) |
| `SETUP-FAILED` | the candidate answered but could not provision the scenario's test user, so these rows were never compared (exit 2) — see section 10 |
| `ERROR` | harness/corpus problem: reference unreachable, or it contradicted the scenario's own `expect` (exit 2) |

**Framework probes are counted too.** A step with `frameworkProbe: true` has no
contract operation, so it folds into no row in the table. It used to therefore
have no effect on the summary or the exit code either — a failing probe was
printed under `MISMATCHES` and then ignored, which meant a regression in
Express's fall-through 404 handling could leave the run green. Probe failures
are now counted under the table (`plus N failing framework probe(s)`) and fail
the run.

`CODES SEEN / DECLARED` is the anti-lying column. A row can be `PASS` while
only ever having been exercised on its 401 branch; the codes column shows that
immediately, and the **"RESPONSE CODES DECLARED BY THE CONTRACT BUT NEVER
EXERCISED"** section below the table lists every such gap explicitly.

Below the table you also get:

* `NOT COVERED BY ANY SCENARIO` — the honest coverage number.
* `COVERED BUT NOT RUN THIS INVOCATION` — with the reason each was skipped.
* `GREEN BUT ONLY SHALLOWLY EXERCISED` — rows that passed on a thin scenario
  (usually the generated 401 sweep) while their deep scenario did not run.
* `MISMATCHES` — per failing step, the JSON path of each disagreement:

```
FAIL  tasks-core / patch-null-clears-notes-omitted-leaves-due  [taskUpdate]
       PATCH /me/tasks/{{taskA}}
       $.task.notes
         reference: (absent)
         candidate: null
```

* `HEADER DIFFERENCES ALLOWED BY THE RUN-MODE ALLOWLIST` and
  `DECLARED HEADER EXCEPTIONS` — every header divergence the policy in
  section 5.1 chose not to fail, with the number of steps it touched. Printed
  on a green run as well as a red one.
* `POLLS THAT NEVER SETTLED` — steps whose background work had not finished
  when the comparison was taken. Section 10.
* `out/parity-report.json` — the same data machine-readable: `summary`,
  `operations[]` (with `observedCodes` / `uncoveredCodes`), and `steps[]` with
  every diff, plus `headerPolicy[]` and `pollUnsettled[]` per step.

## 5. The normalizer

Volatile values are masked **positionally**, not erased. Each distinct real
value gets a stable label *within one side's run of one scenario*, and the
label is **where the value first appeared** — the step number plus the JSON
path:

```
6a78c437aa461ae1dc64adcf  ->  <ID:s4$.task.id>
6a78c437aa461ae1dc64addb  ->  <ID:s7$.tasks[1].id>
```

Both sides label independently. If the structures agree the labels agree — so
"the id returned by create is the same id that comes back from list" is still
checked, while the actual hex differs freely.

> **This used to be a counter** — first distinct id seen became `<ID:1>`, the
> second `<ID:2>` — and that made the whole scenario order-sensitive. The mask
> context is built per side in request order, so one diverging early step
> poisoned every row after it: if step 1 succeeded on the reference and 404'd on
> the candidate, the reference minted an id the candidate never saw and every
> later ordinal shifted by one. Byte-identical rows then FAILed on nothing but
> `<ID:1>` vs `<ID:2>`, which made a **cross-slice gap look like the slice's own
> defect**. Measured: removing the document-scans scenario's opening
> `set-timezone` step — owned by an unimplemented slice — turned four FAILs into
> `PASS 8` with no other change.
>
> Keying the label on the first-appearance location removes the coupling
> entirely: no value's label depends on how many values came before it, so a
> value that never appears on one side costs that side nothing. Two costs were
> accepted for that: labels are longer to read, and a list whose **order**
> differs between the two sides now fails on its ids as well as on its
> neighbouring fields (under the counter, two differently-ordered lists both
> said `<ID:1>` at index 0 and agreed). The second is a bug fix, not a cost.

**One value is pinned rather than located: the provisioned account's own id**,
which always renders `<ID:@user>`. That id is an *input* the harness holds for
both sides, not a relationship to be discovered, so labelling it by wherever it
first surfaces is wrong — if an early step succeeds on one side and 404s on the
other, `userId` first appears in different places and every later echo of it
disagrees on the label alone. Pinning makes `userId` mean "this account" on both
sides regardless of which steps ran. An echo of some *other* id still gets a
location label and still fails.

| Mask | Matches | Renders as |
| --- | --- | --- |
| `ID` | 24-hex Mongo ObjectId, GUID | `<ID:s3$.task.id>`, or `<ID:@user>` for the provisioned account |
| `TS` | ISO-8601 timestamp | `<TS>`, or `<TS@midnightUTC>` at an exact UTC day boundary |
| `TOKEN` | JWT, 43-char base64url refresh token | `<TOKEN>` |
| `NUM` | numeric clock readings (`uptime`) | `<NUM>` |
| `EMAIL` | email addresses | `<EMAIL:s3$.user.email>` |
| `IP` | client IPs — **off by default** | `<IP>` |

Three details that matter:

1. **Object keys are traversed in sorted order** when assigning labels, so a
   server that serialises its JSON keys in a different order still gets the
   same labels. Key order itself is reported as an informational note, never
   as a failure — JSON objects are unordered. A JSON path is visited exactly
   once per response, so two distinct values can never claim the same label.
   Text bodies have no path, so tokens inside them fall back to their position
   in the text — encounter order again, but scoped to one response instead of a
   whole scenario.
2. **Scenario literals are never masked.** Every string written literally in
   the YAML goes into a preserve set, so `dueAt: '2026-12-01T09:00:00.000Z'`
   comes back compared *exactly*. Masking it would only weaken the assertion:
   both sides were sent the same bytes, so the echo must match. The exception
   is the per-side signup email, which is registered as a side-varying input
   and masked even though the harness supplied it.
3. **Error envelopes are compared on the raw, unmasked body.** `error.code` and
   `error.message` must match literally, punctuation and capitalisation
   included, because the contract pins those strings.

### Per-step configuration

```yaml
- name: whatever
  maskOff: [TS]                     # compare these values exactly
  maskOn: [IP]
  literalPaths: ['$.quota.resetAt'] # never mask at these JSON paths
  compareHeaders: [location]        # force a strict, raw comparison of these headers
```

## 5.1 Response headers

**Every header is compared, on the union of both sides.** The differ used to
iterate the *reference's* headers only, so anything the candidate emitted on its
own was invisible and the row still passed — a leaked `Server` /
`X-Powered-By` / `X-AspNet-Version`, a debug or trace header, a duplicated CORS
header, a stray `Set-Cookie`. All of those are client-observable and all of them
went unreported.

Four buckets, all declared in `src/headers.mjs` with the reason for each entry:

| Bucket | Behaviour | Members |
| --- | --- | --- |
| Transport / clock | never compared | `date`, `content-length`, `transfer-encoding`, `connection`, `keep-alive`, `alt-svc`, and `content-type` (compared separately) |
| Volatile value | presence compared, value replaced | `etag` → `<ETAG>`, `last-modified` → `<HTTP-DATE>`, `ratelimit-remaining` / `ratelimit-reset` / `retry-after` → `<COUNTER>` |
| **Run-mode allowlist** | candidate-only presence tolerated, and reported | `RateLimit-*`, `Retry-After` |
| Declared exception | a known open divergence, reported and not failed | `etag` (reference-only) |

`compareHeaders:` on a step overrides all four and compares those headers
strictly on their raw values.

### Why `RateLimit-*` is on the run-mode allowlist

express-rate-limit runs with `standardHeaders: true`, so every response a
limiter touches carries `RateLimit-Policy`, `RateLimit-Limit`,
`RateLimit-Remaining` and `RateLimit-Reset`, and a 429 adds `Retry-After`. But
the reference is normally started at `:4200` with `NODE_ENV=test`, where **rate
limiting is disabled entirely** (contract preamble,
`docs/contract/paths.auth.yaml`), so it emits none of them. The same reference at
`:4100` emits all four with the same values the candidate does — measured,
`RateLimit-Policy: 20;w=900` on both. So a candidate-only `RateLimit` header is
a property of how the reference was started, not of the port.

This is **not** a blanket mask, and the direction matters:

* candidate has it, reference does not → allowed, and listed under
  `HEADER DIFFERENCES ALLOWED BY THE RUN-MODE ALLOWLIST`;
* **reference has it, candidate does not → FAIL.** A missing `RateLimit` header
  on a route that should carry one is a real defect and stays red;
* both have it, values differ → FAIL, except for the three counters
  (`remaining`, `reset`, `retry-after`) whose values are per-side and count down
  independently.

Run against `:4100` to compare these for real.

### The `etag` exception

Express emits a weak `ETag` on every `res.json()` body — the frozen contract
says so explicitly ("with a weak ETag and Content-Length"). Kestrel emits none.
That is a genuine, open contract violation, but it is **one** kernel-level gap
that appears on nearly every JSON row, and failing all of them would bury every
slice-level signal.

So it is a declared exception: reported in its own always-printed section with
the number of steps it touched, never counted as a pass of that header. Only the
`reference-only` direction is excepted — if the candidate ever emits an `ETag`,
presence matches and the value is compared as `<ETAG>`. **Delete the entry in
`src/headers.mjs` once the kernel emits one** and the rows go green on their own.

The bar for adding another entry there: the divergence must be systemic (one
defect on most rows, not a per-route bug), already tracked somewhere an owner
will see it, and recorded with the exact condition that removes it again.

### The generated auth sweep

One scenario is not a file: `auth-sweep` is built from the contract at startup
and calls **every** operation whose `security` is non-empty with no
`Authorization` header, expecting 401. 77 steps, zero rate-limit cost.

It is there because testing the auth middleware on two routes does not prove
all 77 are actually behind it — forgetting `[Authorize]` on one controller is a
classic porting mistake that no happy-path scenario would notice. It also pins
the documented middleware order on the `requireAuth, strictAuthLimiter` routes:
authentication runs first, so an unauthenticated call is rejected *without*
consuming a strict rate-limit slot.

Disable with `--no-auth-sweep`.

## 6. Adding a scenario

Drop a `.yaml` file in `scenarios/`. Files run in filename order; steps run in
order within a file.

```yaml
name: my-thing
description: One paragraph on what this proves.
tags: [tasks]          # --tag / --skip-tag selectors
user: fresh            # 'fresh' provisions an isolated account; 'none' means the
                       # scenario signs itself up

steps:
  - name: create
    op: taskCreate                 # operationId from the frozen contract
    method: POST
    path: /me/tasks
    body: { title: 'X', domain: 'home' }
    expect: { status: 201 }        # reference-side sanity assertion
    capture: { taskId: '$.task.id' }

  - name: read-it-back
    op: taskGet
    method: GET
    path: /me/tasks/{{taskId}}
    expect: { status: 200 }
```

Step keys:

| Key | Notes |
| --- | --- |
| `op` | contract operationId. **Required** unless `frameworkProbe: true` |
| `method`, `path` | `{{var}}` interpolated |
| `query` | mapping appended as a query string; `{ bogus: '1' }` for negative probes |
| `headers` | mapping; `User-Agent` is pinned by the harness |
| `auth` | `true` (default, sends `Bearer {{accessToken}}`), `false`, or a literal header value |
| `body` | JSON body |
| `rawText` | raw string body — for malformed-JSON probes |
| `rawFixture` | `pdf.min` \| `audio.min` \| `empty` \| `{ bytes: N, repeat: 'a' }` |
| `jsonPadding` | `{ field, bytes }` — generates an oversized JSON body |
| `contentType` | overrides the request content-type |
| `capture` | `{ varName: '$.json.path' }`; quote paths containing `[0]` |
| `expect` | `{ status: N }` or `{ status: [N, M] }`, asserted against the **reference** only (section 10) |
| `poll` | `{ until: '$.a.status', in: [failed, ready], timeoutMs, intervalMs }` |
| `timeoutMs` | per-request timeout override |
| `maskOff` / `maskOn` | adjust the mask set for this step (section 5) |
| `literalPaths` | JSON paths to compare unmasked |
| `compareHeaders` | force a strict, raw comparison of these headers (all headers are diffed anyway — section 5.1) |
| `frameworkProbe` | `true` for a step that intentionally has no contract operation |

Seeded variables: `{{email}}`, `{{email2}}`, `{{password}}`, `{{newPassword}}`,
and — when `user: fresh` — `{{accessToken}}`, `{{refreshToken}}`, `{{userId}}`.

The loader refuses to start if a step claims an `op` that does not exist, whose
method disagrees, or whose path does not match the contract's path template. It
warns when a path would match a more specific operation. This is deliberate: a
mistyped `op` would silently inflate the coverage number.

**`poll` exists because of background workers.** With no AI key the document
scan and voice note workers retry a mis-classified 503 four times with backoff
and settle on `failed` after roughly 20 seconds. Reading the resource before it
settles is a race that would make the two sides disagree for no good reason, so
those steps poll to a terminal state and only the final response is compared.

## 7. The rate-limit budget (read this before a full run)

The reference enforces two per-**IP** limiters:

* `authLimiter` — **20 per 15 minutes**, shared by signup, signin, refresh,
  verify-email and magic-consume.
* `strictAuthLimiter` — **5 per hour**, for forgot/reset/change password,
  verify-email codes, change-email and magic-link.

A fresh user per scenario costs one `authLimiter` slot **per side**, and in
`--self-test` both sides drain the *same* bucket. The default corpus needs
**16 slots per side** (10 provisioning signups plus the auth-lifecycle
scenario's own 6 signup/signin/refresh/verify/magic calls), so a full
self-test needs **32 against a 20-slot window**, so it stops and waits at least
once — budget 25-40 minutes wall-clock for a full `--self-test --max-wait 950`.
A run against a real .NET candidate needs only 16 per server and fits in one
window.

The generated `auth-sweep` costs nothing: its requests are unauthenticated, and
on the `requireAuth, strictAuthLimiter` routes authentication rejects them
before the limiter counts them.

The header prints the estimate and warns when it exceeds the window. Options:

* `--max-wait 950` — the runner rides out the window and continues (slow, but
  it is a gate, not a unit test). Give the invocation a 45-minute timeout; a
  shorter one kills the run mid-wait.
* **Shard it** — two invocations that each fit inside one window, which is the
  fastest way to get a full green self-test:

  ```bash
  # shard 1 — 18 slots
  node tools/parity/run.mjs --self-test --no-colour \
    --only auth-sweep --only framework --only auth-lifecycle \
    --only profile --only tasks-core --only tasks-bulk \
    --out tools/parity/out/self-test-a.json

  # wait for the 15-minute window, then shard 2 — 10 slots
  node tools/parity/run.mjs --self-test --no-colour --no-auth-sweep \
    --only ai-nokey --only clarifications-digest --only document-scans \
    --only voice-notes --only integrations-unconfigured \
    --out tools/parity/out/self-test-b.json
  ```

  Each shard reports the operations the other shard owns as `SKIPPED`, never
  as `PASS`, so neither report can overstate what it verified.
* Live with it: an unexpected 429 is reported as `RATE-LIMITED`, never silently
  as a pass or a fail.

Against a real .NET candidate the pressure halves, because each server keeps
its own counters.

## 8. Known coverage limits

These are deliberate and visible in the report; they are not silent gaps.

* **`auth-strict-ratelimit` is skipped by default.** Its 8 steps cost 5 strict
  slots per side against a 5-per-hour budget, so a self-test cannot fit them.
  Five of the eight operations still go green on the generated 401 sweep — the
  `GREEN BUT ONLY SHALLOWLY EXERCISED` section names every one. The three
  unauthenticated ones (forgot-password, reset-password, magic-link) report
  `SKIPPED`, never `PASS`. Run with `--include-strict` on a fresh hour.
* **Clarifications are only reachable on their 404 branch.** A `Clarification`
  row is created in exactly two places — the AI chat tool runner and the
  voice-note transcriber — and both are hard-gated on `isAiConfigured()`. With
  no key the list is provably always empty, so resolve/defer/drop cannot be
  exercised on a real row offline.
* **ICS subscribe never reaches its 201.** The happy path requires fetching a
  real remote calendar; the harness must not depend on the internet, so the
  operation is covered through `timezone_required`, `unsafe_feed_url` and the
  bad-scheme branch only.
* **Google OAuth stops at "not configured".** No client credentials, so
  authorize is a 400 and the callback is only exercised on its cancelled and
  invalid-state redirects.
* **AI endpoints are only covered in their no-key state.** That is the whole
  point of running the reference with `GEMINI_API_KEY=""` — those branches are
  deterministic. The streaming `text/event-stream` success path is not covered.

## 8.1 Provisioning couples every scenario to one route

A `user: fresh` scenario gets its account from `POST /auth/signup` **on the side
being replayed**. On the candidate that means every authenticated row in the
corpus depends on candidate signup working. The coupling is unavoidable for a
black-box harness — there is no other way to obtain a token the candidate will
accept — but it used to be reported dishonestly: any signup failure flattened
into `UNREACHABLE` on every row, indistinguishable from "the server is not
built yet".

Now the failure is classified:

| What happened | Row state | Exit |
| --- | --- | --- |
| the candidate did not answer at all | `UNREACHABLE` | 0 |
| the authLimiter budget ran out | `RATE-LIMITED` | 2 |
| the candidate **answered and got signup wrong** | `SETUP-FAILED` | 2 |

`SETUP-FAILED` says what it means: the rows were never compared, and the defect
is in one route rather than in each of them. The reported note carries the
status and body signup actually returned.

### `--seed-candidate-user CMD`

The escape hatch. Instead of calling candidate signup, the harness runs `CMD`
with `PARITY_BASE_URL`, `PARITY_EMAIL` and `PARITY_PASSWORD` in its environment
and reads one JSON object from its stdout:

```json
{ "accessToken": "...", "refreshToken": "...", "userId": "..." }
```

Only `accessToken` is required. Use it when candidate signup is broken,
deliberately excluded, or not yet ported, so one route's regression stops
blanking sixty rows. Anything the command writes to stderr is surfaced if it
fails, and a failure is reported as `SETUP-FAILED`, not as a pass.

## 9. Debatable normalization decisions

Flagged deliberately, because each one is a place where the harness could be
accused of being too lenient or too strict.

1. **Object key order is not a failure.** JSON objects are unordered and no
   client depends on it — but it is a real byte-level difference, so it is
   reported as an informational note instead of being dropped entirely.
2. **`GET /me/export` is pretty-printed by Node** and everything else is
   compact. The harness compares parsed structure, so a port that emits compact
   JSON there still passes, with a `response body whitespace differs` note. If
   byte-identical export files matter, promote that note to a failure.
3. **`ip` in session listings is compared literally** (mask off by default).
   Node reports `::1` for loopback; an IPv4-only Kestrel reports `127.0.0.1`,
   which would fail a row for an environment reason rather than a code reason.
   **That is a bind problem, not a masking problem** — run the candidate on
   `[::]` and dial `localhost` on both sides (section 2) and the two agree
   exactly, on both `::1` and `::ffff:127.0.0.1`. `--mask-ip` turns it into
   `<IP>` if you are stuck on a host that cannot do dual-stack. Left literal by
   default because silently masking it would hide a port that stops recording
   client IPs at all.
4. **`<TS@midnightUTC>`.** A plain `<TS>` for every timestamp would hide a port
   whose quota `resetAt` is not a day boundary. Distinguishing exact UTC
   midnight keeps that property checkable without pinning the date.
5. **Emails are masked, everything else the scenario wrote is not.** The
   per-side signup address has to differ so a self-test does not collide two
   sides in one database. Masking it positionally keeps "the address you signed
   up with is the address `/auth/me` returns" checkable.
6. **`digest.localDate` is compared literally.** It is a timezone-derived
   `YYYY-MM-DD`, deterministic — except for a run that straddles local midnight
   between the two sides, which would produce a one-off spurious failure. Worth
   the risk: it is the only assertion that the zone handling is right.
7. **One scenario is generated, not data.** `auth-sweep` is built in code from
   the contract. It breaks the "corpus is data" rule on purpose: 77 identical
   steps written by hand would rot the moment an operation is added, and
   deriving them from the operation inventory makes that impossible. Every
   other scenario is data.
8. **`expect: { status: N }` is asserted against the reference only.** It
   catches corpus rot — a scenario that no longer reaches the branch it was
   written for — and deliberately does *not* constrain the candidate, whose
   status is compared against the reference's actual behaviour instead. A
   mismatch here is reported as `ERROR`, not `FAIL`, because the harness is
   what is wrong. A **list** of statuses is allowed for the handful of steps
   whose reference status legitimately depends on whether a background worker
   finished (section 10) — it relaxes only this sanity assertion, never the
   reference-vs-candidate comparison, and the `CODES SEEN` column still shows
   which branch actually ran.
9. **Binary bodies are reduced to `{ bytes, sha256 }`.** Exact for stored
   originals, which is what the document-scan file route returns. A route that
   re-encoded an image would fail on the hash even if the image were visually
   identical.

## 10. Rows whose verdict depends on timing

A row that flips between runs is as corrosive as a row that is permanently red:
either way people stop reading the colour. These are the places where the corpus
is not fully deterministic, what was done about each, and how to check.

### The report tells you

Any step whose `poll` never reached a terminal state is listed under
`POLLS THAT NEVER SETTLED`, **whatever its verdict**. The response that got
compared there is a snapshot of work still in progress, so both a PASS and a
FAIL are provisional. It used to be a note, and notes print only for failing
steps — so a row could pass because both sides were equally unfinished and then
fail the next run because one worker got further.

### Known timing- and mode-dependent rows

| Row | Why | Status |
| --- | --- | --- |
| `scanReprocessDocument` | returns 202 only when re-queueing a scan that actually reached `failed`, 200 as an idempotent no-op otherwise — so its status depends on whether the scan worker beat the 60s poll (measured: ~20s to settle on `:4100`, a 3x margin that disappears under load) and it is *always* 200 against `:4200`, where workers are off | **fixed** — `expect: { status: [202, 200] }`, so the row no longer flips to `ERROR`/exit 2 on timing. The reference-vs-candidate comparison is unchanged and `CODES SEEN` still shows which branch ran |
| `scanGetDocument`, `scanListDocuments`, `voiceGetNote` | the same worker race, one step earlier: against `:4200` the scan/note never leaves `pending`, against `:4100` it settles to `failed` in ~20s | listed under `POLLS THAT NEVER SETTLED`; compare against `:4100` before trusting them |
| `taskDigest` | `digest.localDate` is compared literally, so a run that straddles local midnight between the two side-replays fails once | documented, section 9.6 — deliberately left literal |
| any authenticated row | if the reference is `:4100`, the run sits at 16 of a 20-slot window, so one extra invocation turns rows `RATE-LIMITED` | section 7; `:4200` has no limiters at all |
| any authenticated row, again | **the candidate has its own limiter too.** A .NET candidate started in `Development` enforces the same 20-per-15-minutes, and a full run costs it ~16 slots — so a second run inside the window gets 429 on provisioning and reports whole scenarios `RATE-LIMITED` that passed minutes earlier | measured, and the reason the back-to-back runs below restart the candidate first |

**Two runs in one 15-minute window is itself a flake source**, and it is the
easiest one to mistake for a real one, because the reference at `:4200` has no
limiter and gives no hint that the *candidate* ran out. The in-memory store
resets on restart, so the cheap fix is to bounce the candidate between runs
rather than wait out the window:

```bash
lsof -ti tcp:<candidate-port> | xargs kill        # port-scoped: never pkill -f
# ...restart it, then run again
```

### Proving a row is flaky

Two reports, one diff. Nothing else is needed — the JSON report is stable
between identical runs:

```bash
node tools/parity/run.mjs --no-colour --out /tmp/run-a.json
node tools/parity/run.mjs --no-colour --out /tmp/run-b.json

node -e '
const a=require("/tmp/run-a.json"), b=require("/tmp/run-b.json");
const m=r=>new Map(r.operations.map(o=>[o.operationId,o.state]));
const [x,y]=[m(a),m(b)];
for (const [k,v] of x) if (y.get(k)!==v) console.log(k, v, "->", y.get(k));
'
```

Four full runs against `:4200` / a merged candidate — two before this hardening
and two after, the candidate restarted between the second pair — were identical
at operation *and* step level (255 steps each time), so nothing outside the
table above reproduced as flaky here. That is not proof of determinism, only of
the absence of an easy repro — run the diff above before blaming a red row on
the port.

## 11. Where the harness still cannot see divergence

Honest list of the remaining blind spots, so nobody has to rediscover them.

* **Response trailers, HTTP/2 specifics and framing.** `content-length` vs
  `transfer-encoding: chunked` is deliberately ignored (section 5.1).
* **The `etag` value.** Presence is checked; the hash is not, and the current
  reference-only divergence is a declared exception.
* **Cookies beyond presence and value.** `Set-Cookie` is compared as the single
  joined header `fetch` exposes, so two responses that set the same cookies in a
  different order would differ spuriously, and attributes are not parsed.
* **Timing, ordering and concurrency.** Every scenario is sequential. Nothing
  here would catch a race, a deadlock, or a route that is correct but 50x
  slower.
* **Anything requiring an AI key, the internet, or a real OAuth client** —
  section 8.
* **Streaming bodies.** `text/event-stream` success paths are never exercised;
  the harness reads a complete body before comparing.
* **The reference itself.** `--self-test` proves the harness is consistent, not
  that the reference is right. Every `expect:` in the corpus was written from
  the reference's observed behaviour, bugs included — see the "frozen bug" list
  in `docs/RESUME.md`.
