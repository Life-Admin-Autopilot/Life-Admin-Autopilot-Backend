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
