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

Same shape: isolated database, no AI key, listening on `http://localhost:5100`.
The harness runs fine before the .NET server exists — every row simply reports
`UNREACHABLE`.

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

Exit codes: `0` everything covered passes, `1` at least one `FAIL`, `2` the
result is not trustworthy — a broken scenario, an unreachable reference, a
reference contradicting its own `expect`, or rows that never ran because the
rate limit was exhausted.

### Proving the harness can still fail

`--self-test` shows there are no false FAILures. The negative control shows
there are no false PASSes: it serves the `framework` scenario with one planted
defect per response.

```bash
node tools/parity/selfcheck/bad-candidate.mjs &
node tools/parity/run.mjs --only framework --candidate http://localhost:5199   # must exit 1
kill %1
```

Expect five failing steps: a wrongly-typed `uptime` plus an extra property, the
JSON envelope where Express serves HTML (twice), and two error messages whose
literal text drifted.

Useful flags — `node tools/parity/run.mjs --help` for the full list:

| Flag | Purpose |
| --- | --- |
| `--self-test` | point both sides at `--reference` |
| `--only SUBSTR` | run scenarios whose name contains SUBSTR (repeatable) |
| `--tag` / `--skip-tag` | select by tag; `strict-ratelimit` is skipped by default |
| `--include-strict` | also run the strictAuthLimiter scenario |
| `--max-wait SECONDS` | on an unexpected 429, wait for the window and retry |
| `--mask-ip` | additionally mask client IPs in session listings |
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
| `ERROR` | harness/corpus problem: reference unreachable, or it contradicted the scenario's own `expect` (exit 2) |

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

* `out/parity-report.json` — the same data machine-readable: `summary`,
  `operations[]` (with `observedCodes` / `uncoveredCodes`), and `steps[]` with
  every diff.

## 5. The normalizer

Volatile values are masked **positionally**, not erased. Each distinct real
value gets a stable ordinal *within one side's run of one scenario*:

```
6a78c437aa461ae1dc64adcf  ->  <ID:1>
6a78c437aa461ae1dc64addb  ->  <ID:2>
```

Both sides number independently. If the structures agree the ordinals agree —
so "the id returned by create is the same id that comes back from list" is
still checked, while the actual hex differs freely.

| Mask | Matches | Renders as |
| --- | --- | --- |
| `ID` | 24-hex Mongo ObjectId, GUID | `<ID:n>` |
| `TS` | ISO-8601 timestamp | `<TS>`, or `<TS@midnightUTC>` at an exact UTC day boundary |
| `TOKEN` | JWT, 43-char base64url refresh token | `<TOKEN>` |
| `NUM` | numeric clock readings (`uptime`) | `<NUM>` |
| `EMAIL` | email addresses | `<EMAIL:n>` |
| `IP` | client IPs — **off by default** | `<IP>` |

Three details that matter:

1. **Object keys are traversed in sorted order** when assigning ordinals, so a
   server that serialises its JSON keys in a different order still gets the
   same numbering. Key order itself is reported as an informational note, never
   as a failure — JSON objects are unordered.
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
  compareHeaders: [location]        # extra headers to diff (content-type is always compared)
```

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
| `expect` | `{ status: N }`, asserted against the **reference** only |
| `poll` | `{ until: '$.a.status', in: [failed, ready], timeoutMs, intervalMs }` |
| `timeoutMs` | per-request timeout override |
| `maskOff` / `maskOn` | adjust the mask set for this step (section 5) |
| `literalPaths` | JSON paths to compare unmasked |
| `compareHeaders` | extra response headers to diff |
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
   Node reports `::1` for loopback; a Kestrel host could report `127.0.0.1`,
   which would fail a row for an environment reason rather than a code reason.
   `--mask-ip` turns it into `<IP>`. Left literal by default because silently
   masking it would hide a port that stops recording client IPs at all.
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
   what is wrong.
9. **Binary bodies are reduced to `{ bytes, sha256 }`.** Exact for stored
   originals, which is what the document-scan file route returns. A route that
   re-encoded an image would fail on the hash even if the image were visually
   identical.
