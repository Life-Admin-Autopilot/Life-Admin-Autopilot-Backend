# KERNEL.md — the shared kernel contract

**Read this before writing a line of slice code.** The kernel is frozen. Seven
slices are being built on it in parallel, so anything you change here breaks
somebody else. If you believe the kernel is wrong, say so — do not patch around it.

## Where you are

**The .NET backend is its own repository**, separate from the Node app:

| | Path | Branch |
| --- | --- | --- |
| **.NET backend — you work here** | `/Users/mina/Documents/Mina/Life-Admin-Autopilot-Backend` | `feat/node-parity` (local only, not pushed) |
| Node source of truth — read-only reference | `/Users/mina/Documents/Mina/Steward/server/src` | — |

`Steward` is a *different repo*. `feat/node-parity`, `docs/KERNEL.md`, `docs/contract/`
and every `.csproj` exist only in the backend repo — looking for them under
`Steward` (or a worktree of it) finds nothing, which is not a sign the kernel is
missing.

### The two reference servers — both are Node

**`:4100` and `:4200` are BOTH Node**, same source tree, different `NODE_ENV`. No
.NET server runs by default; start yours on a free port (5100+).

| | Mode | Use it for |
| --- | --- | --- |
| **`:4200`** | `NODE_ENV=test`, db `kitto_parity_testmode` | **Canonical for iteration.** Response shapes, status codes, error envelopes. |
| **`:4100`** | `NODE_ENV=development`, db `kitto_parity_node` | Anything test mode switches OFF — see below. |

`NODE_ENV=test` changes exactly three things (verified by grepping `NODE_ENV` across
`server/src`), and **never a response shape**:

1. **Rate limiters are skipped** (`middleware/rateLimit.ts` `skipInTest`). Confirmed
   live: `:4100` returns `RateLimit-Policy/Limit/Remaining/Reset`, `:4200` returns
   none. **Verify every 429 body, `Retry-After` and `RateLimit-*` header against
   `:4100`.**
2. **All five background workers return immediately** — voice-note transcription,
   reminders, document scan, ICS feed, Google sync. **The voice, document and
   integrations slices must use `:4100`** for anything that depends on a worker
   side-effect; on `:4200` the job simply never runs.
3. One boot log line is suppressed. Irrelevant.

Parity Mongo is `mongodb://127.0.0.1:27018`; .NET uses db `kitto_parity_dotnet`.
Never touch the Atlas cluster in `server/.env`.

The target is **byte-level response compatibility with the Node server running
without `GEMINI_API_KEY`**: same status codes, same JSON field names and nesting,
same literal error messages. Every rule below exists because the obvious .NET
default diverges from Node. The frozen spec is `docs/contract/*.yaml` (87
operations); the source of truth for behaviour is `Steward/server/src`, and a live
reference runs on `http://localhost:4100` — curl it to settle any question.

---

## 0. Where things live

| Layer | Project | Kernel folder |
| --- | --- | --- |
| PL | `Life-Admin-Autopilot-Backend/` | `Kernel/` — middleware, binders, auth, rate limits, modules, hosting |
| BLL | `Life-Admin-Autopilot.BLL/` | `Kernel/` — DTOs, mappers, task query, bulk service, reminders, imports |
| DAL | `Life-Admin-Autopilot.DAL/` | `Kernel/` — errors, Mongo documents, repository base, quota, erasers |
| Tests | `Life-Admin-Autopilot.Tests/` | `Kernel/` — `KernelWebApplicationFactory`, probe module, parity tests |

`Program.cs` calls exactly two kernel methods: `builder.Services.AddKernel(config)`
and `app.UseKernel()`.

**Do not edit `Program.cs`.** See §9.

---

## 1. Errors — one envelope, one exception type

Throw `AppException` (`DAL/Kernel/Errors/AppException.cs`) for every expected,
client-visible failure. The middleware renders it as:

```json
{"error":{"code":"…","message":"…","details":…}}
```

`details` is **omitted entirely** when null — never `"details": null`.

```csharp
throw AppException.BadRequest("invalid_body", "Invalid task payload.", details);
throw AppException.NotFound("task_not_found", "Task no longer exists.");
throw AppException.Conflict("email_taken", "An account with this email already exists.");
throw AppException.PaymentRequired("quota_exceeded", $"You've hit today's limit of {limit} messages.", payload);
```

**Message strings are part of the contract.** Copy them verbatim from the Node
route you are mirroring — punctuation, casing, em dashes and all. Do not improve
the wording.

### Branch order in `KernelErrorMiddleware`

Mirrors `middleware/errorHandler.ts`. An earlier branch shadows a later one.

1. `ObjectIdCastException` → **404** `not_found` "Not found"
2. `ValidationException` → **400** `validation_error` "Request validation failed"
3. `AppException` → its own status/code/message
4. anything else → **500** `internal_error` "Internal server error"

---

## 2. The seven cross-cutting edge cases

These are all verified live. A "correct" implementation breaks parity.

### 2.1 Malformed JSON and oversize bodies are 500, not 400/413

`express.json({ limit: '256kb' })` throws a `SyntaxError` / `PayloadTooLargeError`
that Node's error handler does not recognise, so both fall through to the generic
500. ASP.NET would answer 400 and 413.

Handled for you by `KernelBody` (§4). Do not add your own body reading, and do not
add an `[ApiController]` attribute — its automatic 400 emits `ProblemDetails`,
which Node never produces (`SuppressModelStateInvalidFilter` is already on).

### 2.2 Unknown route / wrong method returns the default 404, not the envelope

**Do not add a catch-all route or a fallback endpoint.** Node's Express falls
through to its own HTML 404, and routing an unknown path into the JSON envelope is
the failure this rule exists to prevent.

Express's 404 covers BOTH cases: an unknown path, and a **known path with an
unmatched method** (Express only matches path+method together, so `PUT /health`
falls through to the same 404).

> **⚠️ KNOWN KERNEL BUG — open, affects every route, no slice can fix it.**
>
> ASP.NET routing matches the path, finds no method match, and short-circuits
> **405** before any kernel middleware runs:
>
> ```
> PUT :4200/health  → 404  (Express, text/html)
> PUT :5110/health  → 405  (ASP.NET, empty body)
> ```
>
> The harness row `framework/unknown-method-on-known-path-is-html-404` FAILs on
> `status`. Reported by slice a-account against `GET /health`, which the kernel
> owns.
>
> **Do not work around this in a slice**, and do not add a method-specific route to
> silence it. The fix is a kernel-level 405→404 rewrite; see §2.2.1 for the exact
> change and its current status.

Once that lands, the remaining delta is **body-only**: Express serves an HTML page
(`Cannot GET /nope`) with `Content-Type: text/html; charset=utf-8`; ASP.NET's 404
has an empty body and no content type. Status and "not JSON" are what clients
branch on, and both are preserved.

**The harness currently FAILs `framework/unknown-route-is-html-404` on exactly that
body/content-type delta.** That is a genuine difference, so the harness is not wrong
to see it — but a permanently-red row that everyone is told to ignore trains people
to ignore red rows. It needs a decision, not tolerance. §2.2.1 recommends masking it
with a documented reason rather than reproducing Express's HTML, because that HTML
reflects the request path and would introduce XSS.

### 2.2.1 Status of the two 404 deltas

Neither is fixed. Both need a kernel change plus a `dotnet test` run, and the kernel
author's environment was destroyed mid-session before either could be verified — so
this is a written specification, not half-landed code. Reviewed by slice a-account,
who found a composition defect and a security issue in the first draft; both are
folded in below.

**Fix A — the 405 status divergence. Required, and unambiguous.**

One middleware, registered in `UseKernel()` immediately after
`KernelErrorMiddleware` and **outside** `NodeCorsMiddleware`, so it observes the
final status without disturbing the CORS 204 short-circuit. After `await _next`:

1. `405` + method is **not** OPTIONS → rewrite to **404** and drop the `Allow`
   header. ⚠️ *The Allow-drop is inferred from Express's finalhandler, NOT verified —
   confirm with `curl -sD- -X PUT :4200/health` before relying on it.*
2. `405` + method **is** OPTIONS → **200** + `Allow`, mirroring Express's
   auto-OPTIONS responder. **Move `NodeCorsMiddleware.MimicExpressAutoOptions`
   wholesale — do not reimplement it from this summary.** It also rewrites `Allow`
   to insert `HEAD` beside `GET` (Express lists both; ASP.NET emits only `GET`), and
   a from-scratch reimplementation silently drops that and regresses preflight rows.

Leave the existing CORS placement alone otherwise: for OPTIONS with an absent or
allowlisted origin the middleware short-circuits at 204 and routing never runs, so
no 405 is possible. The gap is only non-OPTIONS methods, which neither branch
inspects.

**Fix B — the HTML body. Recommended AGAINST; mask the harness row instead.**

My earlier position was to reproduce Express's HTML. I've reversed it, because
a-account surfaced a cost I had not weighed.

The body embeds the request path — `Cannot PUT /health` — which is
attacker-controlled. Express's finalhandler runs it through `encodeUrl` **and**
`escapeHtml`. A port that interpolates the raw path into `<pre>` creates **reflected
XSS on every unknown route**, served same-origin by an API that also serves
authenticated JSON.

Weigh that against the benefit: no client parses a 404 body, and the frontend
branches on status. Fix A alone removes the only difference a client can observe.
Adding an attacker-controlled HTML reflection path to a JSON API to green a
cosmetic row is a bad trade.

**So: mask `header.content-type` and `$` (body) for the two `framework/*` 404 rows
in the harness, with an inline comment citing this section.** A declared, reviewed
exception is not the same as a standing red row nobody explains — it answers
a-account's "red rows train people to ignore red rows" objection without buying an
XSS surface.

**If the coordinator overrules this and wants byte-exact HTML anyway**, it is not
mergeable without all four of:

- `HtmlEncoder.Default.Encode` on the path — never raw interpolation;
- a test asserting a request to `/<script>alert(1)</script>` comes back escaped;
- Express's `Content-Security-Policy: default-src 'none'` and
  `X-Content-Type-Options: nosniff` headers, which are defence-in-depth here rather
  than cosmetic parity;
- a byte-exact capture from `curl -sD- -X GET :4200/nope`, **still outstanding** —
  a-account's shell died before taking it.

A further defect if it is built: **`GetEndpoint() is null` is the wrong guard.** On a
method mismatch ASP.NET *does* match the path and selects its internal
method-not-allowed endpoint, so `GetEndpoint()` is non-null — meaning after Fix A
turns the 405 into a 404, an endpoint-null guard declines and you get a bodyless
404, fixing status while leaving content-type and body red. Do **not** widen it to
"any 404 with nothing written" either; that hijacks a slice returning a bare
`Results.NotFound()`. Correct approach: perform both translations in the **same**
middleware and set a local flag when Fix A rewrites a 405, then write HTML only when
`GetEndpoint() is null || thatFlag`. Ordering is then implicit and correct.

### 2.3 Three `details` shapes, explicitly selected

`ValidationDetails` in `DAL/Kernel/Errors/ValidationDetails.cs` exposes all three.
There is no generic one. Pick by looking at the Node route.

| Node source | Code | `details` shape | Mapper |
| --- | --- | --- | --- |
| throwing `Schema.parse(req.body)` | `validation_error` | **array** of `{path,message}`, dot-joined path | `AsPathMessageArray` — or just `throw new ValidationException(issues)` |
| `safeParse()` + `error.flatten()` | route's own (`invalid_body`, `invalid_query`, `invalid_code`, `invalid_metadata`, `invalid_review`, `invalid_answer`) | `{formErrors, fieldErrors}` | `AsFlattened` |
| `me.icsFeeds` only | `invalid_feed` | **raw issues array** with `code/expected/received/path(array)/message` | `AsRawIssues` |

`AsFlattened` keys nested issues under the **top-level field name**. An issue at
`mic.quality` appears under `"mic"`, because zod's `flatten()` uses `issue.path[0]`.
Verified live.

Node routes using the throwing `.parse()` lane (everything else uses `safeParse`):
`auth.password`, `auth.email`, `auth.session`, `auth.magic`, `me.notifications`.

Use `ZodMessages` (`DAL/Kernel/Errors/ZodMessages.cs`) for the message text —
`"Invalid email"`, `"String must contain at least 8 character(s)"`,
`"Unrecognized key(s) in object: 'bogus'"`, `"must not be empty"`, and so on. Add a
constant only after seeing the real output on port 4100.

### 2.4 Validation order is observable

Use `NodeFieldRules` (`DAL/Kernel/Validation/NodeFieldRules.cs`). Do not hand-roll.

| Rule | zod chain | Consequence |
| --- | --- | --- |
| `NormalizeEmail` | `.email().toLowerCase().trim()` | check runs **before** trim → `"  a@b.com  "` is **rejected**; `"A@B.com"` → `"a@b.com"` |
| `NormalizeSixDigitCode` | `.trim().regex(/^\d{6}$/)` | trim runs **before** the regex → `" 424242 "` is **accepted** |
| `TryNormalizeDisplayName` | `.min(1).max(80).trim()` | length runs **before** trim → `"   "` passes and stores as `""` |

### 2.5 Rate limiters key on the raw socket IP

`trust proxy` is OFF in Node, so `req.ip` is the socket address and
`X-Forwarded-For` is ignored. The kernel does the same. Reading the forwarded
header would let any client spoof past every limiter. State is in-process; there is
no shared store.

---

### 2.6 Only `application/json` bodies are parsed at all

`app.ts` calls `express.json({ limit: '256kb' })` with no `type` option, so
body-parser matches the single pattern `application/json`. Any other content type is
skipped entirely — `req.body` stays `{}` and the route's own validators report the
fields as missing.

Measured on `POST /auth/signup` with a payload that is valid as JSON:

| `Content-Type` | Parsed? | Status |
| --- | --- | --- |
| `application/json` | yes | 201 |
| `application/json; charset=utf-8` | **yes** — parameters never affect the match | 201 |
| `APPLICATION/JSON` | yes — media types are case-insensitive | 201 |
| *(absent)* | no | 400 |
| `text/plain` | no | 400 |
| `application/x-www-form-urlencoded` | no | 400 |
| `application/vnd.api+json`, `application/json-patch+json` | **no** — a `+json` suffix is not `application/json` | 400 |

Handled for you by `KernelBody` (§4); `KernelBody.IsJsonContentType` is the predicate
if you need it directly.

**The gate runs BEFORE the body is read**, which is load-bearing rather than an
implementation detail: body-parser never touches the stream of a request it skips, so
neither the 256kb ceiling nor a JSON syntax error can fire on one. The same malformed
or 300kb body is a **500** as `application/json` and a plain **400** as `text/plain`.
A gate applied after reading gets both of those backwards.

**This is also a security control.** `text/plain` is a CORS *simple* request — a
cross-origin page can send one with no preflight. The content-type gate is what stops
such a request carrying a JSON body into a state-changing endpoint.

**Trap when you write tests.** `new StringContent(json)` defaults to
`text/plain; charset=utf-8`, so it now yields an EMPTY body and your route reports its
fields as missing. Always pass the media type:
`new StringContent(json, Encoding.UTF8, "application/json")`. Every existing call site
in the repo already does.

**Raw upload routes are NOT affected.** `me.voiceNotes`, `me.documentScans` and
`/ai/voice/transcribe` use `express.raw({ type: [...] })` with their own per-route MIME
allowlists, which is a different matcher from `express.json()`'s. `RawBodyReader` is
unchanged and each of those slices owns its own allowlist.

### 2.7 Twelve security headers on EVERY response, error paths included

`app.use(helmet())` (helmet 8.1.0, no options) puts its full default set on every
response, and Express's error handler replaces the **body**, not the headers — so a
200, a 400, a 429 and a 500 all carry the same twelve. `app.disable('x-powered-by')`
means no server-identity header at all; the kernel matches by setting
`KestrelServerOptions.AddServerHeader = false`, so no `Server: Kestrel` either.

Installed by `HelmetHeadersMiddleware` in `UseKernel()`, ahead of CORS. `Defaults` on
that class is the authoritative list — assert against it rather than re-typing
literals. Two values a port tends to "improve" and must not:
`X-XSS-Protection: 0` (helmet disables the legacy auditor deliberately) and a CSP
whose directives are joined with `;` and **no** following space.

**Consequence for anything that sets a header then throws:** the error middleware
must not call `Response.Clear()`. It no longer does. That is also what keeps the
429's `Retry-After` and the `RateLimit-Policy/Limit/Remaining/Reset` family — set by
the limiter immediately before it throws — and the CORS `Vary` /
`Access-Control-Allow-Credentials` pair on the response. If you add a middleware that
rewrites error responses, preserve this property.

## 3. CORS

Configured from `Kernel:Cors:Origins`, falling back to the `CORS_ORIGINS`
environment variable (comma-separated, same as Node). `NodeCorsMiddleware` is a
hand-written port of the `cors` package as `app.ts` configures it; the built-in
ASP.NET middleware cannot reproduce it. Three cases:

| Origin | Behaviour |
| --- | --- |
| **absent** (native app, curl, server-to-server) | **Allowed.** `Vary: Origin` + `Access-Control-Allow-Credentials: true`, but no `Access-Control-Allow-Origin`. A bare `OPTIONS` still short-circuits with 204. |
| **allowlisted** | `Access-Control-Allow-Origin` echoes the origin (never `*`), plus `Vary` and credentials. Preflight → 204 with the methods list and an echo of `Access-Control-Request-Headers`. |
| **anything else** | **No CORS headers at all**, not even `Vary`. The request is processed normally; a preflight falls through to a 200 with `Allow`. Not an error — the browser refuses the response. |

You do not touch CORS. It is already correct.

---

## 4. Request binding

### JSON bodies — `KernelBody.ReadAsync<T>(ctx, options)`

```csharp
var body = await KernelBody.ReadAsync<CreateTaskBody>(
    ctx, KernelBodyOptions.Strict_("Invalid task payload."));
```

**Lenient is the default, and that is the parity-correct default.** A plain zod
object STRIPS unknown keys and succeeds — `POST /auth/signup` with an extra field
returns **201** on the live Node server. Only a schema marked `.strict()` rejects
them.

Use `KernelBodyOptions.Strict_` **only** where the Node schema carries `.strict()`.
In the Node source that is: the `me.tasks` body schemas, and the `me.tasks` /
`me.digest` query schemas. Nothing else — `auth.*`, `PATCH /me`,
`me.notifications`, `ai`, `clarifications`, `me.voiceNotes`, `me.documentScans`,
`me.icsFeeds` are all lenient.

Behaviour: a body whose `Content-Type` is not `application/json` is not read at all
and deserializes as `{}` (§2.6); an empty body likewise deserializes as `{}` (express
sets `req.body = {}`, and the route's own schema then decides); malformed JSON and
>256kb are 500 (§2.1) — but **only when the content type made it JSON in the first
place**; a strict violation is `400 {code}` with
`formErrors: ["Unrecognized key(s) in object: 'x'"]`.

### Query strings — `QueryReader`

Unknown parameters are a 400, never silently ignored — the frontend depends on the
rejection. Empty-string values are rejected too (`?status=`, `?q=`).

```csharp
var q = new QueryReader(ctx.Request.Query, "sort", "limit", "cursor", "status", "q");
var status = q.CsvEnum("status", TaskVocabulary.Statuses);
var text   = q.String("q", minLength: 1, maxLength: 200);
var limit  = q.Int("limit", min: 1, max: 200, fallback: 50);
q.ThrowIfInvalid("invalid_query", "Invalid task list query.");
```

Accumulate every issue, then call `ThrowIfInvalid` **once** — zod reports them all
in one response.

### Raw uploads — `RawBodyReader`

The frontend posts **raw bytes with `x-*` metadata headers**, not multipart.

```csharp
var upload = await RawBodyReader.ReadAsync(
    ctx, maxBytes: MaxVoiceBytes,
    emptyMessage: "No audio payload received.",
    tooLargeMessage: $"Voice note exceeds {maxMb}MB.");

var h = RawBodyReader.Headers(ctx);
var durationMs = h.Int("x-voice-note-duration-ms", "durationMs", min: 0, max: 600_000);
var capturedAt = h.IsoDate("x-voice-note-captured-at", "capturedAt");
h.ThrowIfInvalid("invalid_metadata", "Missing or invalid x-voice-note-* headers.");
```

The two-ceiling trick is ported: the transport limit is `2 × maxBytes` so a normally
oversize upload gets a friendly `400 payload_too_large` instead of a terse 413. Only
a body over twice the limit becomes a 500.

The header-reader field names are the **schema** names (`durationMs`), not the
header names — that is what Node's `fieldErrors` keys are.

---

## 5. Auth

Scheme `KernelBearer`, and it is the default. Just use `[Authorize]` /
`.RequireAuthorization()`.

| Condition | Response |
| --- | --- |
| header absent, or not starting with `"Bearer "` (including `Basic …`) | `401 missing_token` "Missing access token" |
| bad signature / expired / wrong alg / missing `sub` or `email` | `401 invalid_token` "Invalid or expired access token" |

Tokens are Node-shaped: HS256, `{ sub, email }`, **no issuer, no audience**, zero
clock skew. Secret comes from `Kernel:Jwt:AccessSecret`, then `JWT_ACCESS_SECRET`,
then `Jwt:Key`.

### The claim shape — read it this way, always

```csharp
var user = ctx.RequireUser();   // throws 401 missing_token if absent
user.Id;        // ObjectId — what every Mongo `userId` reference stores
user.IdString;  // 24-hex — what the API exposes and the JWT `sub` carries
user.Email;
```

Never dig through `User.Claims` yourself. Both `sub`/`email` and
`ClaimTypes.NameIdentifier`/`ClaimTypes.Email` are populated, but
`RequireUser()` is the only supported reader.

**Two user ids, and the distinction is load-bearing.** `UserProfileDocument.Id` is
the ObjectId the API exposes and every foreign key stores.
`UserProfileDocument.IdentityUserId` is the SQL ASP.NET Identity key, used only to
reach credentials. Never leak the Guid to a client; never put it in a Mongo
reference.

---

## 6. Response DTOs — never serialize an entity

Mongoose applies a per-schema `toJSON` transform on the way out. A direct
serialization leaks `passwordHash` / `storageKey` and omits derived fields.
`BLL/Kernel/Mappers/KernelMappers.cs` makes every transform explicit:

```csharp
return Results.Ok(new { tasks = docs.Select(d => d.ToDto()) });
```

| Document | Drops | Derives |
| --- | --- | --- |
| `TaskDocument` | `_id`→`id`, `__v` | `priorityRank` |
| `SubtaskDocument` | `_id`→`id` (its **own** transform — Mongoose does not recurse) | — |
| `UserProfileDocument` | `passwordHash`, `_id`→`id`, `__v` | `hasPassword` |
| `ClarificationDocument` | `sourceKey`, `_id`→`id`, `__v` | — |
| `NotificationDocument`, `TaskBulkOpDocument` | `_id`→`id`, `__v` | — |

Rules for **any new mapper** you write — whether or not your slice owns a new
collection. A standalone transform over an existing kernel document counts:

- Read the `toJSON` block of the matching `server/src/models/*.ts` first. **The
  deletions are the whole point.**
- Nullable members carry `[JsonIgnore(WhenWritingNull)]` — Mongoose never stores an
  unset optional, so it never appears in the JSON.
- Declare properties in Mongoose schema order, with `id` and derived fields last.
- Timestamps use `JsIsoDateTimeConverter` (registered globally): always three
  fractional digits and a `Z`, matching `Date#toISOString()`. STJ's default trims
  `.600` to `.6`, which is a parity break.

**If you need a sub-object transform that `KernelMappers` currently builds inline**
— e.g. `SubscriptionStateDocument` → `SubscriptionStateDto`, which exists only
inside `ToDto(UserProfileDocument)` — the transform belongs in `KernelMappers` as a
public extension, not copied into your slice. a-account hit exactly this, correctly
declined to edit a `Kernel/` file, and wrote its own copy, so that transform now
exists twice. *Pending kernel change: expose the standalone
`ToDto(this SubscriptionStateDocument)` and have the user mapper call it.* Until
that lands, if you find yourself duplicating a kernel transform, **report it rather
than editing `Kernel/`** — a second copy that drifts is exactly what the mapper
layer exists to prevent.

### Two mappers you must write, spelled out

The documents/voice slices own these models, so the kernel does not define them —
but the transforms are non-obvious, so here they are verbatim:

`ScannedDocument`: `_id`→`id`, drop `__v`, then
`canRetry = status == "failed" && manualRetries < MAX_MANUAL_SCAN_RETRIES`, then
**drop** `storageKey`, `rawExtractedText`, `attempts`, `maxAttempts`,
`manualRetries`, `lockedUntil`, `nextRunAt`, `lastError`, `notifiedAt`.

`VoiceNote`: `_id`→`id`, drop `__v`, then **drop** `storageKey`, `attempts`,
`maxAttempts`, `lockedUntil`, `nextRunAt`, `lastError`, `notifiedAt`, `clarifyItems`
(the last is an internal staging lane; it surfaces to clients as Clarifications).

---

## 7. Data access

### Repository base — `MongoRepositoryBase<TDocument>`

Its reason to exist is two predicates that Node spreads across 40+ call sites, and
that a slice must never hand-write:

```csharp
NotDeleted()          // { deletedAt: { $exists: false } }  — NOT { deletedAt: null }
VisibleOpen(now)      // { status: 'open', $or: [ {deferredUntil:{$exists:false}}, {deferredUntil:{$lte:now}} ] }
UserScoped(userId)
LiveForUser(userId)   // UserScoped AND NotDeleted
ParseObjectId(value)  // throws ObjectIdCastException → 404 not_found
```

Every Task read composes `NotDeleted()`. Every clarification count or list composes
`VisibleOpen()` — the dashboard and the digest once disagreed for exactly this
reason.

### Documents and collection names

`DAL/Kernel/Documents/` holds `TaskDocument`, `UserProfileDocument`,
`ClarificationDocument`, `NotificationDocument`, `TaskBulkOpDocument`, plus the
vocabularies (`TaskVocabulary`, `UserVocabulary`, …) with the closed enum lists,
`PRIORITY_RANK` and `NormalizeTag`.

`MongoCollections` holds the Mongoose-pluralised names. **Add your slice's
collection constant in your own file**, not to that class — it is a merge-conflict
magnet.

### Indexes — `IMongoIndexProvider`

Mongoose creates indexes from the schema; the .NET driver creates nothing. Some are
a **correctness** requirement, not an optimisation: the quota primitive's
duplicate-key retry only works because a unique index exists to produce the error.

```csharp
services.AddMongoIndexProvider<MySliceIndexes>();
```

`KernelIndexProvider` already covers the three usage counters, `users`, `tasks`,
`clarifications`, `notifications` and `taskbulkops`. Creation runs in the background
at boot (never blocking) and is idempotent.

---

## 8. Shared services

### 8.1 `TaskQuery` — the day-boundary authority

`BLL/Kernel/Tasks/TaskQuery.cs` + `TaskQuery.Time.cs`. Seven modules depend on it;
if the UI can express a filter the agent cannot, "show me what you just showed me"
stops working.

```csharp
TaskQuery.BuildFilter(userId, filter, now)     // user-scoped, always NotDeleted
TaskQuery.ListAsync(collection, userId, filter, sort, limit, cursor)  // $facet: page + total in one trip
TaskQuery.EncodeCursor(offset) / DecodeCursor(cursor)                 // base64url offset, lenient decode
TaskQuery.GetDayBoundaries(now, timezone)                             // today/tomorrow/dayAfter/weekEnd
TaskQuery.StartOfLocalDay(at, timezone)
```

Notes that bite: `undated` overrides an explicit due range; `untagged` overwrites a
tag filter; `overdue` narrows status to `open|snoozed` unless one was given; free
text is regex-escaped over title **and** notes; `weekEnd` is the next **7 days**, not
the calendar week; an unrecognised timezone **throws** (a 500), matching Node's
uncaught `Intl` RangeError — do not add a silent UTC fallback.

### 8.2 `BulkService` — the only journaled Task write path

`BLL/Kernel/Tasks/BulkService.cs`. Route **every** multi-task mutation through it. A
hand-rolled `UpdateMany` silently removes undo.

- Writes the `TaskBulkOp` journal **before** the bulk write.
- Deletion is always **soft**. The chat agent calls this same service, so it is
  structurally incapable of an irreversible bulk delete.
- Delete cascades `ClarificationCascade.DropForTasksAsync`.
- **Undo restores the tasks but does NOT un-drop the clarifications.** That asymmetry
  is ported deliberately — a dropped question is a settled conversation. Do not
  "fix" it.
- No-op actions are skipped, not journaled, so undo cannot restore a change that
  never happened.
- `ToMongoOps` is public and shared: in a `prior` patch, BSON **null** means "the
  field was absent, `$unset` it on undo"; a missing key means "not touched". The
  categorize flow must reuse this function, not reimplement it.

### 8.3 One quota primitive — `IUsageQuotaStore`

Node clones this three times. There is one implementation.

```csharp
var bucket = new UsageQuotaBucket(
    MongoCollections.AiUsageCounters, userId,
    new Dictionary<string,string> { ["date"] = UsageQuotaBuckets.UtcDate(), ["kind"] = "message" },
    limit);

var admission = await quota.TryAdmitAsync(bucket);
if (!admission.Admitted)
    throw AppException.PaymentRequired("quota_exceeded",
        $"You've hit today's limit of {admission.Limit} messages.",
        new { kind = "message", tier, limit = admission.Limit, used = admission.Used,
              resetAt = UsageQuotaBuckets.NextUtcMidnightIso() });
```

**The 402 payload is per-caller** — scans send `{tier,limit,used}`, AI sends
`{kind,tier,limit,used,resetAt}`, translate sends `{locale,limit,used,resetAt}` — so
the store returns an admission, never an exception.

**Reserve/release contract:** the slot is consumed *before* the expensive work. If
the work fails before producing a result, call `ReleaseAsync` exactly once. **Never
release on undo** — that makes do → undo → do an unlimited loop around the cap.
`RecordAsync` is the ungated increment (Node's `recordUsage`).

If you add a counter collection, register its unique index (§7).

### 8.4 `ImportedTimeResolver` — the date-only policy

`BLL/Kernel/Integrations/ImportedTimeResolver.cs`. **We never invent a time.**
`ResolveDateOnly` (source's date + the user's stated default time, `high`
confidence), `ResolveFloating` (`low` confidence, `needsConfirmation: true`),
`ResolveExact`. Missing/invalid timezone **throws** `TimezoneRequiredException`
rather than defaulting to UTC — an import runs with no device present, and guessing
UTC for a user in Cairo moves every reminder two hours invisibly.

### 8.5 `ReminderPlanner` + `ReminderLeadTime`

`SetRulesRemindersAsync` writes the deterministic lead-time schedule and never
throws. **Call it only when `dueAt` or `kind` changes** — it overwrites `reminders`
fresh, clearing `firedAt`. `SetSnoozeReminderAsync` fires once, at the snooze moment.

AI refinement goes through `IReminderRefiner`. The kernel registers
`NullReminderRefiner` (not configured), which is the no-`GEMINI_API_KEY` parity
target. The AI slice **replaces** that registration — use `services.Replace(...)`,
not `TryAdd`.

### 8.6 Account deletion — `IUserDataEraser`

Node keeps one hand-maintained 12-collection list. Register your own instead:

```csharp
internal sealed class VoiceNoteEraser(IMongoDatabase db) : IUserDataEraser
{
    public string Name => "voice-notes";
    public Task EraseAsync(UserErasureContext ctx, CancellationToken ct) => …;
}

services.AddUserDataEraser<VoiceNoteEraser>();
```

Order: `Storage` (100) → `Sessions` (200) → `Dependents` (300, the default) →
`Account` (1000, **kernel-only**). Blob cleanup must be at `Storage` order, before
the rows holding the storage keys vanish. Be idempotent; the cascade is re-runnable.
Non-account erasers that throw are logged and the cascade continues.

---

## 9. Registration conventions — how seven agents avoid each other

**Nobody edits `Program.cs`.** Three mechanisms:

### 9.0 Where slice code goes — no new projects

**You do not add a `.csproj`.** There are exactly four projects and the solution is
frozen at four. All of them are SDK-style with **default globbing** (no explicit
`<Compile>` items), so a new `.cs` file is picked up with zero project-file edits —
which is also why no two slices ever conflict in a `.csproj`.

Put your files in an existing project, under `Features/<Slice>/`:

| What | Project directory | Namespace root | Your namespace |
| --- | --- | --- | --- |
| endpoints / modules / controllers | `Life-Admin-Autopilot-Backend/Features/<Slice>/` | `Life_Admin_Autopilot_Backend` | `Life_Admin_Autopilot_Backend.Features.<Slice>` |
| services, DTOs, mappers | `Life-Admin-Autopilot.BLL/Features/<Slice>/` | `Life_Admin_Autopilot.BLL` | `Life_Admin_Autopilot.BLL.Features.<Slice>` |
| Mongo documents, repositories | `Life-Admin-Autopilot.DAL/Features/<Slice>/` | `Life_Admin_Autopilot.DAL` | `Life_Admin_Autopilot.DAL.Features.<Slice>` |
| tests | `Life-Admin-Autopilot.Tests/Features/<Slice>/` | `Life_Admin_Autopilot.Tests` | `Life_Admin_Autopilot.Tests.Features.<Slice>` |

Note the PL root namespace uses **underscores throughout**
(`Life_Admin_Autopilot_Backend`), while BLL/DAL/Tests use a **dot before the layer**
(`Life_Admin_Autopilot.BLL`). That is pre-existing; match it exactly.

A slice needs a PL file (its module) at minimum. Add BLL/DAL files only when the
slice actually has service or persistence logic.

### 9.1 `AddXxxFeature()` — the exact signature

It is an extension on **`IServiceCollection`**, never on `IHostApplicationBuilder`
or `WebApplication`. It runs at `builder.Services` time, before the container is
built, so there is no `WebApplication` in scope. Copy this shape verbatim:

```csharp
namespace Life_Admin_Autopilot_Backend.Features.Account;

public static class AccountFeature
{
    public static IServiceCollection AddAccountFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IAccountService, AccountService>();
        services.AddUserDataEraser<AccountEraser>();
        return services;
    }
}
```

Always take `IConfiguration` even if you do not read it yet, and always return
`IServiceCollection` — `IEndpointModule.AddServices` hands you both and the uniform
signature is what lets the scanner stay dumb.

### `IEndpointModule` (assembly-scanned)

```csharp
public sealed class TasksModule : IEndpointModule
{
    public void AddServices(IServiceCollection s, IConfiguration c) => s.AddTasksFeature(c);
    public void MapEndpoints(IEndpointRouteBuilder e) => e.MapTasksEndpoints();
}
```

One per slice, in that slice's folder, named `XxxModule`, public parameterless
constructor. The scanner walks every loaded assembly whose name starts with
`Life-Admin-Autopilot`. A controller-only slice implements `AddServices` and leaves
`MapEndpoints` as the default no-op — MVC discovers controllers separately.

`Life-Admin-Autopilot.Tests/Kernel/KernelProbeModule.cs` is a worked example.

### `AddXxxFeature()` DI extension

Every slice owns exactly one:

```csharp
public static class TasksFeature
{
    public static IServiceCollection AddTasksFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ITaskRepository, TaskRepository>();
        services.AddUserDataEraser<TaskEraser>();
        services.AddMongoIndexProvider<TaskIndexes>();
        services.AddKernelWorker<ReminderWorker>();
        return services;
    }
}
```

`AddServices` should do nothing but call it, so the DI surface stays greppable. Use
`TryAdd*` for anything another slice might also want; use `Replace` only when
deliberately overriding a kernel default (`IReminderRefiner` is the one expected
case).

### Additive registries

`IUserDataEraser`, `IMongoIndexProvider` and `IKernelRateLimiter` are all
`IEnumerable<T>` collections. Adding to them never touches another slice's file.

---

## 10. Rate limiting

Eight limiters, names from `KernelRateLimiters`. **Use the constant, never a
literal** — three are shared buckets and a typo silently creates a private one.

| Constant | Window / max | Key | Kind |
| --- | --- | --- | --- |
| `Auth` | 15 min / 20 | socket IP | fixed window |
| `StrictAuth` | 60 min / 5 | socket IP | fixed window |
| `AiAsk` | 1 min / 30 | user → IP | sliding |
| `TaskSearch` | 1 min / 30 | user → IP | sliding |
| `TaskSummary` | 1 min / 10 | user → IP | sliding — **SHARED** across summary routes |
| `AiConfirm` | 1 min / 30 | user → IP | sliding |
| `AiVoice` | 1 min / 12 | user → IP | sliding — **SHARED** (voice upload + chat transcribe) |
| `DocumentScan` | 1 min / 6 | user → IP | sliding — **SHARED** across scan routes |

```csharp
[Authorize]
[RateLimit(KernelRateLimiters.AiVoice)]          // controllers
endpoints.MapPost(...).RateLimited(KernelRateLimiters.AiAsk);   // minimal APIs
```

Apply **after** authentication so the sliding limiters key on the user id — MVC
filters and endpoint filters both already run after the auth middleware.

Response shapes: the fixed-window limiters emit `RateLimit-Policy`,
`RateLimit-Limit`, `RateLimit-Remaining` and `RateLimit-Reset` on **every** response
and add `Retry-After` + `X-Content-Type-Options: nosniff` on the 429, with their own
message. The sliding limiters set only `Retry-After` and always say
`"You are going a little fast — give it a moment and try again."`

Disabled when `Kernel:RateLimit:Enabled=false` — the test fixture does this, matching
Node's `NODE_ENV=test` skip.

---

## 11. Background workers

```csharp
internal sealed class ReminderWorker(IServiceProvider sp, ILogger<ReminderWorker> log)
    : KernelPollingWorker(sp, log)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);
    protected override string WorkerName => "reminder";
    protected override async Task RunOnceAsync(CancellationToken ct) { … }
}

services.AddKernelWorker<ReminderWorker>();
```

The base class reproduces Node's `setInterval` + overlap guard: a slow tick delays
the next one instead of racing it. Rules — `RunOnceAsync` must not throw; **claim
work atomically** (a conditional update stamping a lock or `firedAt`) before doing
it, because the double-send guard belongs in the claim, not the scheduler; resolve
scoped services from `Services` inside the tick.

Workers are skipped entirely when `Kernel:Workers:Enabled=false` (the test fixture
sets this).

---

## 12. Testing your slice

```csharp
public sealed class TaskEndpointTests : IClassFixture<KernelWebApplicationFactory>
{
    private readonly KernelWebApplicationFactory _factory;
    public TaskEndpointTests(KernelWebApplicationFactory f) => _factory = f;

    [Fact]
    public async Task rejects_an_unknown_query_parameter()
    {
        var response = await _factory.CreateApiClient().GetAsync("/me/tasks?bogus=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

The fixture points Mongo at `mongodb://127.0.0.1:27018`, disables rate limiters and
workers, pins a CORS allowlist and JWT secret, and puts Identity on a private SQLite
file. `CreateApiClient()` does not follow redirects — a 3xx is a parity failure.

For an authenticated call, mint a Node-shaped token with
`KernelPipelineTests.NodeShapedToken(sub, email)`.

**Give your slice its own Mongo database.** Seven slices run tests concurrently
against one `mongod`, and several seed and delete rows in `users`. Sharing one
database is a real cross-slice flake source. Derive a factory — three lines, and the
pattern a-account proved out:

```csharp
public sealed class AccountWebApplicationFactory : KernelWebApplicationFactory
{
    public AccountWebApplicationFactory() =>
        With("MongoDbSettings:DatabaseName", "kitto_parity_dotnet_a_tests");
}
```

*Pending kernel change:* the base fixture should default to a per-factory-type
database (`kitto_parity_dotnet_{GetType().Name.ToLowerInvariant()}`) so derived
factories are isolated with no `With()` call at all. Not yet landed — until it is,
set it explicitly as above.

### 12.1 Running your candidate server

**`dotnet run` alone is wrong.** `Properties/launchSettings.json` silently overrides
your environment: it pins `https://localhost:7276;http://localhost:5115` and forces
`ASPNETCORE_ENVIRONMENT=Development`, so your server comes up on the wrong port in
the wrong environment while you wait on the port you asked for. Always pass
`--no-launch-profile`:

```bash
cd Life-Admin-Autopilot-Backend
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS='http://[::]:<your port>' \
MongoDbSettings__ConnectionString=mongodb://127.0.0.1:27018 \
MongoDbSettings__DatabaseName=kitto_parity_dotnet_<slice> \
Kernel__Jwt__AccessSecret=<same secret the harness mints tokens with> \
Kernel__RateLimit__Enabled=false \
Kernel__Workers__Enabled=false \
dotnet run --no-build --no-launch-profile
```

`RateLimit`/`Workers` disabled mirrors the `:4200` reference. Turn limiters back ON
(and compare against `:4100`) when the thing under test is a 429.

**Bind `[::]`, and dial `localhost` on BOTH sides.** This used to read
`http://127.0.0.1:<port>`, which is wrong: that is an IPv4-**only** listener, while
Node's `server.listen(port)` binds `::` in dual-stack mode. The two then disagree
about the peer address for an environment reason that has nothing to do with your
code, and any row echoing `req.ip` — today `sessions[].ip`, and anything else that
records a client address later — fails on a port that is actually correct.

Measured on both servers, once the candidate binds `[::]`:

| Dialled as | Node `:4200` | .NET `[::]` | .NET `127.0.0.1` (the old, wrong bind) |
| --- | --- | --- | --- |
| `http://127.0.0.1:<port>` | `::ffff:127.0.0.1` | `::ffff:127.0.0.1` | `127.0.0.1` — **mismatch** |
| `http://localhost:<port>` | `::1` | `::1` | connection refused over `::1` |

A dual-stack listener reports an IPv4 peer in IPv4-mapped form, which is exactly
what Node reports. Keep the dial host identical on both sides too — `localhost` and
`127.0.0.1` produce *different* (both legitimate) peer addresses, so mixing them
across the two servers reintroduces the same false failure from the other end. It
also matters for rate limiting: the limiters key on the raw socket IP, so `::1` and
`::ffff:127.0.0.1` are separate buckets.

Ignore the alarming-looking `Server=(localdb)\mssqllocaldb;…` default in
`appsettings.json` — LocalDB is Windows-only and unreachable here, but nothing opens
a SQL connection at boot. If your slice touches Identity, set
`Database__Provider=Sqlite` (§13.1).

### 12.2 The harness cannot green an authed slice until `/auth/signup` exists

`runner.mjs` `provisionUser()` signs up **on the candidate**. Until slice b-auth
ships `POST /auth/signup`, every scenario marked `user: fresh` reports "candidate
side unavailable" and its operations come back **UNREACHABLE** — including
operations that are fully implemented and byte-correct.

**This is not your bug and you cannot fix it from a slice.** Do not implement
`/auth/signup` yourself to go green; that collides head-on with b-auth.

For a real signal in the meantime, restrict to scenarios needing no provisioning:

```bash
node tools/parity/run.mjs --only auth-sweep --only framework
```

and hand-roll a differential for your own 200/404 branches. a-account's four rows
pass this way. Expect "your rows must be PASS" on a full run to be unachievable for
slices 2–7 until signup lands.

**Never point a test at the Atlas cluster in `server/.env`.** That is production
data.

Mongo-backed tests should skip (return early) when the parity instance is
unreachable — see `UsageQuotaTests.TryCreateStore` — so the suite stays green on a
machine without it.

---

## 13. Configuration keys the kernel reads

| Key | Env fallback | Default | Meaning |
| --- | --- | --- | --- |
| `Kernel:Cors:Origins` | `CORS_ORIGINS` | empty | comma-separated allowlist |
| `Kernel:Jwt:AccessSecret` | `JWT_ACCESS_SECRET`, then `Jwt:Key` | — | HS256 secret |
| `Kernel:Jwt:ValidIssuer` / `ValidAudience` | — | off | Node signs neither |
| `Kernel:RateLimit:Enabled` | — | `true` | |
| `Kernel:Workers:Enabled` | — | `true` | |
| `Kernel:Mongo:EnsureIndexes` | — | `true` | |
| `Kernel:UseHttpsRedirection` | — | `false` | a 307 to https breaks every parity check |
| `MongoDbSettings:ConnectionString` / `DatabaseName` | — | — | pre-existing |
| `Database:Provider` | — | `SqlServer` | `SqlServer` \| `Sqlite` — see §13.1 |
| `Database:EnsureCreated` | — | `true` on SQLite, **never** on SQL Server | |
| `ConnectionStrings:SqliteConnection` | — | `Data Source=life-admin-autopilot.db` | |

### 13.1 The Identity SQL provider seam

Credentials live in ASP.NET Identity on SQL; the profile lives in Mongo. That
decision is about **where data lives**, not which engine backs Identity — so the
engine is a config value, `Database:Provider`, handled in `AddDataAccessLayer` via
`DAL/Kernel/Data/DatabaseProvider.cs`.

**Default is `SqlServer`; nothing changes unless configured.** Production keeps SQL
Server. `Sqlite` exists because the shipped default connection string is
`Server=(localdb)\mssqllocaldb;…` and LocalDB is Windows-only — on macOS/Linux the
first endpoint that touches Identity fails outright. `KernelWebApplicationFactory`
sets `Sqlite` with a private temp file per factory, so slice tests get a working
credential store for free.

**Migrations are provider-specific. SQL Server is the canonical target.**

- `20260724022126_InitialIdentity` was generated for SQL Server and production must
  keep applying it.
- **Never run `dotnet ef migrations add` while `Database:Provider=Sqlite`.** A
  SQLite-shaped migration overwriting the canonical set breaks the production
  deploy. If you must regenerate, set the provider back to `SqlServer` first.
- SQLite never participates in migration history at all — it gets its schema from
  `EnsureCreated()` in `SqliteSchemaInitializer`, which is hard-gated to SQLite and
  cannot run against SQL Server (EnsureCreated would bypass migrations and leave a
  database they can never touch).

**Known gap, not yet fixed:** `Program.cs` never calls `Migrate()`. A fresh SQL
Server deploy therefore starts against an empty database and the checked-in
migration is never applied. Whoever owns deployment needs to add a migration step
or an explicit startup `Migrate()` for the SqlServer path.

**Parity trap for the auth slice:** Identity's DEFAULT `IdentityOptions.Password`
requires an uppercase letter, a digit and a non-alphanumeric character. Node's
signup schema is only `z.string().min(8).max(128)`, so a Node-valid password like
`password123` is REJECTED by Identity out of the box. Relax
`IdentityOptions.Password` in the auth slice to match Node, or signup diverges on
every simple password.

---

## 14. Checklist before you call a slice done

Several items are conditional — a read-only slice legitimately satisfies about half
of these vacuously. "Not applicable" is a valid answer; a-account owned no
collection, wrote no body binder and registered no eraser, and that was correct.

**Always:**

- [ ] No edits to `Program.cs`, `KernelExtensions.cs`, or anything under a `Kernel/` folder.
- [ ] Every response goes through an explicit mapper — no entity is serialized directly.
- [ ] Error codes and messages copied verbatim from the Node route.
- [ ] No kernel transform duplicated into your slice (§6) — report instead.

**If your slice reads Tasks or Clarifications:**

- [ ] Every Task read composes `NotDeleted()`; every clarification list composes `VisibleOpen()`.

**If your slice mutates more than one Task at a time:**

- [ ] Multi-task mutations go through `BulkService`.

**If your slice accepts a request body or query string:**

- [ ] The right one of the three `details` mappers.
- [ ] Body strictness matches the Node schema (lenient unless it says `.strict()`).
- [ ] Query binding rejects unknown and empty parameters.

**If your slice owns a collection:**

- [ ] An `IUserDataEraser` registered for every collection the slice owns.
- [ ] An `IMongoIndexProvider` registered for every uniqueness invariant.

**If your slice has a rate-limited route (check the Node route for a limiter):**

- [ ] Rate limiters applied by constant, after `[Authorize]`.
- [ ] 429 body and `Retry-After` verified against **`:4100`** — `:4200` has limiters off.

**Always, to finish:**

- [ ] `dotnet build` clean, `dotnet test` green.
- [ ] Spot-checked against **`:4200`** with curl (§0), using
      `--no-launch-profile` to run your candidate (§12.1).
- [ ] Harness run scoped per §12.2 if your scenarios need `user: fresh`.
