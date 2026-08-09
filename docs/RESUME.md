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
- **Kernel** (`36bec0d`, `56e6e1f`) — error envelope, CORS, binding, auth, DTO mappers, repository base, one quota primitive, `TaskQuery`, `BulkService`, rate limiters, and the `IEndpointModule` / `IUserDataEraser` / `IMongoIndexProvider` registries. Build clean, **165 tests**.
- **Parity harness** (`c69d54d`) — 87/87 operations covered, self-test green, plus a negative control that plants five defects and confirms all five are caught.
- **Slice A** (`30085fe` on `slice/a-account`) — health, subscription, invoices, integrations. 176 tests, all four rows PASS.

## In flight when work stopped

- **slice-b-auth** — worktree `backend-slices/b-auth`, port 5120, db `kitto_parity_dotnet_b`. Unblocked; building behind `IAuthCredentialStore`.
- **slice-c-tasks** — worktree `backend-slices/c-tasks`, port 5130, db `kitto_parity_dotnet_c`.

Check `git -C <worktree> log --oneline` and `git status` in each before assuming anything was lost.

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

**Also decided:** emit Express's real HTML 404 body. Arbitrated in favour of fixing the server rather than masking the harness row — a permanently-red row trains people to ignore red rows.

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
