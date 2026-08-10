# RESUME — Node → .NET parity port

State as of the last working session. Read this first; it is the handoff.

## The one-line goal

.NET must be **response-compatible with the Node server running without `GEMINI_API_KEY`**. Node already degrades to `503 ai_not_configured` and the frontend handles it, so "defer AI" and "behave exactly like Node" are the same target.

## Where everything lives

| What | Where |
|---|---|
| .NET backend (this repo) | `/Users/mina/Documents/Mina/Life-Admin-Autopilot-Backend`, branch `feat/node-parity` |
| Node reference source | `/Users/mina/Documents/Mina/Steward/server/src` — **separate git root** |
| Frontend | `/Users/mina/Documents/Mina/Steward` |
| Slice worktrees | `/Users/mina/Documents/Mina/backend-slices/{a-account,b-auth,c-tasks}` |
| Kernel contract for slice authors | `docs/KERNEL.md` |
| Frozen API contract (87 ops) | `docs/contract/*.yaml` |
| Parity harness | `tools/parity/` |

**Nothing is pushed.** All branches are local only.

## Restarting the environment

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"   # SDK 10.0.302

# 1. isolated Mongo (NEVER use server/.env — it points at a shared Atlas cluster)
mongod --dbpath <scratch>/mongo-parity/data --port 27018 --bind_ip 127.0.0.1 \
       --logpath <scratch>/mongo-parity/log/mongod.log --fork

# 2. Node reference, test mode — rate limiters + workers OFF. Default for iteration.
cd /Users/mina/Documents/Mina/Steward/server
MONGODB_URI="mongodb://127.0.0.1:27018/kitto_parity_testmode" GEMINI_API_KEY="" \
  PORT=4200 NODE_ENV=test npx tsx src/index.ts

# 3. Node reference, dev mode — rate limiters + workers ON.
MONGODB_URI="mongodb://127.0.0.1:27018/kitto_parity_node" GEMINI_API_KEY="" \
  PORT=4100 NODE_ENV=development npx tsx src/index.ts
```

`GEMINI_API_KEY=""` disables AI (`isAiConfigured()` is a truthiness check) and dotenv will not overwrite an already-set variable, so the inline empty string wins over `.env`.

**Which port to use.** `:4200` for everything by default. `:4100` only for (a) 429 bodies / `Retry-After` / `RateLimit-*`, and (b) anything depending on a background worker side-effect — document scans, voice notes, reminders firing, ICS/Google sync. `NODE_ENV=test` changes exactly three things and none is a response shape: limiters skipped, all five workers early-return, one boot log line.

The auth limiter is 20/15min and the strict one 5/hour, **keyed on raw socket IP with trust-proxy off** — so `localhost`, `127.0.0.1` and the LAN IP are three independent buckets. Useful when `:4100` locks you out.

## Done

- **Contract frozen** — 75 paths / 87 operations / 212 schemas, authored against a live server, zero collisions, all `$ref`s resolve.
- **Kernel** — error envelope, CORS, binding, auth, DTO mappers, repository base, one quota primitive, `TaskQuery`, `BulkService`, rate limiters, the `IEndpointModule` / `IUserDataEraser` / `IMongoIndexProvider` registries, the SQL provider seam, and the 405→404 method-mismatch fix.
- **Parity harness** — 87/87 operations covered, self-test green, plus a negative control that plants five defects and confirms all five are caught.
- **Slices A (account), B (auth), C (tasks core)** — all merged into `feat/node-parity`.

### Current measured state

`dotnet build` clean, **`dotnet test` 309 passing** (288 + 21 on `slice/kernel-hardening`).

Full harness run, integration branch vs the live Node reference:

```
87 contract operations  |  PASS 44   FAIL 32   ERROR 8   SKIPPED 3
```

**Every FAIL and ERROR traces to an unimplemented slice, not a regression.** Verified: the two framework rows that look like kernel failures (`malformed-json-is-500-not-400`, `oversized-json-body-is-500-not-413`) fail only because `PATCH /me` does not exist yet — on a route that *does* exist, both servers return 500 as required. The 8 ERRORs are cascades where an upload step could not run, so `{{scanId}}` / `{{noteId}}` were never captured.

Reproduce with:

```bash
ASPNETCORE_URLS="http://[::]:5100" ASPNETCORE_ENVIRONMENT=Development \
Database__Provider=Sqlite ConnectionStrings__DefaultConnection="Data Source=/tmp/parity-int.db" \
MongoDbSettings__ConnectionString="mongodb://127.0.0.1:27018" \
MongoDbSettings__DatabaseName="kitto_parity_dotnet_int" \
Jwt__Key="<any 64+ chars>" Kernel__Cors__Origins="http://localhost:3000,capacitor://localhost" \
dotnet run --project Life-Admin-Autopilot-Backend --no-launch-profile

node tools/parity/run.mjs --reference http://localhost:4200 --candidate http://localhost:5100 --no-colour
```

Bind `[::]` and dial `localhost` on both sides — `127.0.0.1` is IPv4-only and breaks any row echoing `req.ip`.

### Three kernel defects found by slice B — ALL FIXED on `slice/kernel-hardening`

None of these failed a harness row; all three took hand-rolled differentials to find,
and each now has one in `Life-Admin-Autopilot.Tests/Kernel/KernelHardeningTests.cs`.

1. **Bodies were parsed regardless of Content-Type** (#21). **Fixed** — `KernelBody.ReadAsync`
   now gates on `IsJsonContentType` *before* reading the stream, matching body-parser's
   default `application/json` matcher: parameters and casing still parse
   (`application/json; charset=utf-8`, `APPLICATION/JSON`), everything else — absent,
   `text/plain`, form-encoded, and the `+json` suffix types — leaves the body `{}` so the
   route's own validators produce Node's exact `Required` errors. Gating before the read
   matters: the same malformed or 300kb body is a 500 as `application/json` and a 400 as
   `text/plain`. Also a security fix (`text/plain` is a CORS simple request). See KERNEL.md §2.6.
2. **No security headers** (#22). **Fixed** — new `HelmetHeadersMiddleware` emits all
   **twelve** helmet 8.1.0 defaults (not the four one grep showed) on every response, and
   `KestrelServerOptions.AddServerHeader = false` drops `Server: Kestrel`.
   `KernelErrorMiddleware.WriteAsync` no longer calls `Response.Clear()`, so the 429's
   `Retry-After` / `RateLimit-*` family and the CORS pair survive the error path.
   See KERNEL.md §2.7. **Verified live**: header sets are identical to the reference on
   200 (`:4200`), 400 and 429 (`:4100`), including the 429 body.
3. **`KERNEL.md` §12.1 prescribed an IPv4-only bind** (#23). **Fixed** — §12.1 and
   `tools/parity/README.md` now prescribe `ASPNETCORE_URLS='http://[::]:<port>'` and
   dialling `localhost` on both sides. Measured: with `[::]`, an IPv4 peer is recorded as
   `::ffff:127.0.0.1` and a `localhost` peer as `::1` — matching Node exactly on both.

## AI phase — Langflow (started, interrupted)

The product decision: **everything converges on one planning agent** — chat, voice transcripts and documents all route into it, and it asks clarifying questions and calls tools. Two design forks were settled deliberately:

- **Chat gets its own mode on the same agent**, rather than being fed into the task extractor. The shipped chat has 11 tools and 7 of them act on *existing* tasks; routing "what's due next week?" into an extractor would create a task called "due next week".
- **Documents still produce reviewable candidates**, not prose. Prose cannot carry a CitationChip, and `docs/features.md` names that chip the mitigation for the product's biggest risk.
- **Streaming is preserved** via an adapter that translates Langflow's stream into the frontend's existing 7-event SSE contract, so the frontend changes nothing.

On `slice/m-langflow` (`42a7f6d`), state is uneven:

| Artifact | State |
|---|---|
| `langflow/planning-agent.v4.json` | **Done, validated** — 15 nodes, 14 edges, 0 dangling, all 11 frontend tools |
| `langflow/document-agent.json` | **Done, validated** — 4 nodes, 3 edges, 0 dangling |
| `PLANNING-AGENT.md`, `DOCUMENT-AGENT.md` | Done |
| .NET SSE adapter (`Features/Ai/`) | **Incomplete, 4 compile errors** — fix the build first |

Neither flow embeds a secret or a hardcoded host. The supplied baseline (`Steward/langflow/langflow.json`) carries a live Mistral key, a signed JWT, `verify=False`, and two `localhost:7276` endpoints — see task #35.

**Nothing here has run against a live Langflow.** None is installed on this machine (nothing on `:7860`), so every flow behaviour is authored-blind. Standing one up is the next real gate: the vocabulary rewrite in particular needs one real run to confirm the model obeys the frontend's enums (`health|home|car|finance|family|pets`, `low|normal|high|urgent`) rather than the abandoned branch's.

## Interrupted mid-work — WIP is committed, NOT verified

A session limit terminated both remaining slice agents mid-task. Their work was uncommitted on disk; I committed it to their own branches so it could not be lost. **Neither was built, tested, or parity-checked. Treat both as untrusted drafts — amend or reset freely.**

| Branch | WIP commit | Port | Mongo db | State when it stopped |
|---|---|---|---|---|
| `slice/b-auth` | `7ec4fef` | 5120 | `kitto_parity_dotnet_b` | Features under all four projects. Was mid-investigation of two `pendingEmail` parity failures — checking whether the field was genuinely absent or just ordered differently. Start there. |
| `slice/c-tasks` | `fe6c1af` | 5130 | `kitto_parity_dotnet_c` | Features scaffolded across PL/BLL/DAL. Had not yet started its candidate server. |

**Rebase both onto `feat/node-parity` before continuing.** They forked at `c69d54d`, *before* the Identity provider seam landed as `56e6e1f`, and `b-auth` carries its own copy of those same kernel files — expect overlap there, not genuine divergence.

Slice A is unaffected and complete: `slice/a-account` @ `30085fe`.

## Next actions, in order

1. **Fix the 405→404 kernel bug** (below). Blocker-class: affects every route, no slice can fix it.
2. Commit the pending `tools/parity/` edits — they belong to the harness author, verify before sweeping them up.
3. Merge slice branches into `feat/node-parity` as their harness rows go green.
4. Launch the remaining slices: D (notifications/reminders), E (document scans), F (ICS), G (Google), then H (clarifications), I (AI shell), J (digest), K (profile/account/export — **last**, it needs every slice's `IUserDataEraser` registered).
5. Phase 3: stand up YARP, drive the matrix green, flip routes lowest-risk first.

## The 405→404 bug — everything needed to fix it

**Symptom.** A wrong method on a *known* path returns 405 on .NET and 404 on Express.

**Cause.** `NodeCorsMiddleware.InvokeAsync` only calls `MimicExpressAutoOptions` inside the `if (!isAllowed)` branch, so a no-Origin non-OPTIONS request never has its 405 inspected.

**Fix.** Extract into its own middleware registered in `UseKernel()` after `KernelErrorMiddleware`, outside CORS.

**Verified Express behaviour** (measured against `:4200`, do not re-derive):

| Request | Status | Content-Type | Body |
|---|---|---|---|
| `PUT /health` | **404** | `text/html; charset=utf-8` | `<!DOCTYPE html>…<pre>Cannot PUT /health</pre>…` |
| `GET /nonexistent` | **404** | `text/html; charset=utf-8` | `<!DOCTYPE html>…<pre>Cannot GET /nonexistent</pre>…` |
| `POST /me/tasks/counts` | **404** | — | (authed route, still 404 — the method miss beats the auth check) |
| `OPTIONS /health` | **204** | — | CORS short-circuits before routing |

So the rule is: **405 → 404 for every method except OPTIONS**; OPTIONS is already handled by the CORS short-circuit. Note `OPTIONS` returns 204, not the 200 `MimicExpressAutoOptions` produces — that 200 path applies only to non-allowlisted origins, where CORS does not short-circuit.

### ARBITRATION (final): implement Fix A only. Do NOT reproduce the HTML body.

This reverses an earlier ruling — the earlier one is wrong, ignore it if you find it quoted elsewhere.

- **Fix A — rewrite 405 → 404.** IMPLEMENT. This is the only difference a client can observe: no client parses a 404 body, and the frontend branches on status.
- **Fix B — reproduce Express's HTML 404 body/content-type.** DO NOT IMPLEMENT. It buys a cosmetic green harness row in exchange for a reflected-XSS surface (the body embeds the attacker-controlled request path, served same-origin by an API that also serves authenticated JSON).
- **Instead:** mask the body and content-type assertions on the two `framework/*` 404 rows in the harness corpus, with an inline comment citing `KERNEL.md` §2.2.1. A declared, reviewed exception answers "a standing red row trains people to ignore red rows" without buying the risk. The harness owner makes that edit — it is their file.

The earlier ruling assumed the trade was cosmetic-vs-correct. It is cosmetic-vs-XSS. If Fix B is ever revisited it is non-mergeable without: HTML-escaping, an XSS test using `/<script>alert(1)</script>`, Express's CSP header, and a byte-exact capture of the real body — which nobody has yet managed to take.

**Implementation note for Fix A, from the third round of review.** Two guards were proposed and both are wrong: `GetEndpoint() is null` never fires on a method mismatch, and "any 404 with nothing written" is too broad — a slice returning a bare `Results.NotFound()` would get its JSON envelope replaced. Do **both** translations inside **one** middleware using a local flag that records that *this* middleware performed the 405→404 rewrite, and act only on that. Precision here matters more than either standalone guard.

### The two fixes DO NOT COMPOSE as first specified — read this before implementing

Caught in review by the slice-A author. The HTML-404 fix was specified as gated on `GetEndpoint() is null`. **That is false on a method mismatch** — routing did match an endpoint, it just rejected the method. So applying the 405→404 rewrite first yields a 404 with an *empty* body, and the harness row stays red on content-type and body. The fix would look done and not be.

Correct rule: gate the HTML body on **"final status is 404 and nothing has been written"**, not on `GetEndpoint() is null`. Ordering matters — the body writer must run *after* the status rewrite.

Two more requirements from the same review:

- **HEAD augmentation.** Express lists `HEAD` alongside `GET` in `Allow`; ASP.NET emits only `GET`. Already handled inside `MimicExpressAutoOptions`; preserve it when extracting that logic into its own middleware.
- **Reflected XSS.** The Express 404 body interpolates the request path (`Cannot PUT /health`). Writing the raw path into an HTML response is an injection vector. **HTML-escape it**, and add a test with a path like `/<script>alert(1)</script>` asserting the tags come back escaped. Node's `finalhandler` escapes; a naive port will not.

## Other open items

- **Harness `provisionUser()` signs up on the candidate**, so every `user: fresh` scenario reports UNREACHABLE until slice B ships `/auth/signup`. Do **not** implement signup in another slice to work around this — it collides head-on with slice B. Scope runs with `--only auth-sweep --only framework` meanwhile.
- **Identity's password policy breaks parity.** It demands uppercase + digit + non-alphanumeric, so `password123` — valid under Node's `min(8).max(128)` — is rejected. Slice B owns the fix.
- **Test fixture hardcodes one Mongo database** for all concurrent slices. Needs a per-factory database.
- **`KernelMappers.ToDto(this SubscriptionStateDocument)` is not exposed**, so slice A had to duplicate it.
- **`Program.cs` never calls `Migrate()`** — a fresh environment starts against an empty database.
- **`dotnet run` is actively wrong** for launching a candidate: `launchSettings.json` silently pins ports 5115/7276 and `Development`. Use `--no-launch-profile`.
- **Migrations are provider-specific.** SQL Server stays canonical; SQLite uses `EnsureCreated` and never joins migration history. Never run `migrations add` while `Provider=Sqlite`.

## Parity traps already discovered — do not "fix" these

Full list in `docs/KERNEL.md` §2. The ones that bite hardest:

- Malformed JSON and oversize bodies return **500**, not 400/413.
- Unknown route returns a **non-JSON** 404.
- **Three** distinct validation-`details` shapes coexist; a single generic mapper breaks two of them.
- Request bodies are **lenient** by default — unknown keys are stripped. Only `me.tasks` bodies and the `me.tasks`/`me.digest` queries are strict.
- Validation *order* is observable: `"  a@b.com  "` is rejected, `" 424242 "` is accepted.
- `renewsAt`/`canceledAt` are **absent keys** when unset, never `null`.
- Timestamps use the 3-digit JS ISO form (`.600`, not `.6`).
- `i18n` leaks on every task-returning endpoint except `GET /me/tasks` and `GET /me/tasks/{id}`.
- Subtask text is never translated (the overlay keys on `sub._id` after `toJSON` renamed it to `id`, so the key is the literal string `"undefined"`). **Frozen bug — port as-is.**
- Two reachable 500s are part of the contract: `POST /me/tasks` with `kind:"reminder"` and no `dueAt`; and `PATCH {"dueAt":null}` on a reminder, which then makes all three subtask endpoints 500 forever.
- Undo restores the task but does **not** un-drop its clarification.
- `ai_not_configured` carries **six** different literal messages; `undo_not_found` two; `invalid_credentials` three.
- Delete idempotency varies: document-scan DELETE is idempotent, ICS and Google DELETE 404 on the second call.
- Document-scan reprocess returns **200** on a non-failed scan (idempotent no-op) and **202** only when re-queueing a genuinely failed one.

## Security work, tracked separately

Rotate the credentials pasted into chat plus the Mistral key `langflow/README.md:175` self-reports as leaked — the Azure Storage account key is the urgent one. Delete or authorize the unauthenticated `*TestController`s (live IDOR; `docs/ai-flow.md:91` shows the Langflow agent depending on one). Fail-fast on `Jwt:Key`. Add lockout + rate limiting to login. Stop echoing `ex.Message` in `PlanningController` 500s. Upgrade `Microsoft.OpenApi` 2.0.0 (advisory GHSA-v5pm-xwqc-g5wc).
