# NODE-BUGS — defects in the reference, reproduced on purpose

Bugs found in the Node/Express reference **while porting it**. Every one below is
faithfully reproduced in the .NET port, because the port's contract is
response-compatibility with the reference as it actually behaves, not as it was
meant to behave.

This file is a **decision document, not a work list.** Nothing here is scheduled.
Each entry ends with a recommendation, and the recommendation is the point.

## The rule that governs every entry

**A fix must land on BOTH servers in the same change, or not at all.**

The parity harness diffs live responses. Fixing one of these on the .NET side alone
turns its row red — and *correctly* red, because the two servers would genuinely
disagree. A red row that everyone knows to ignore is worse than the bug: it trains
people to skim the harness output, which is the one thing that must not happen while
routes are being cut over.

So the sequencing for anything marked FIX is:

1. Land the change on Node and .NET together.
2. Update the affected harness rows in the same commit.
3. Only then cut the route over.

Until someone is willing to do all three, **KEEP** is the right answer even for
defects nobody would defend. Deferring is fine. Fixing one side quietly is not.

## Summary

| # | Defect | Reachable today? | Recommendation |
| --- | --- | --- | --- |
| 1 | `scansAwaitingReview` counted two different ways | yes | **FIX** — pick the guarded count |
| 2 | `localDateKey` falls back to host TZ, not UTC | narrow | **FIX (Node-side, carefully)** |
| 3 | `keepRealThemes` burns ids on blank-labelled themes | yes | **FIX** — one-line reorder |
| 4 | `findDuplicates` tests the raw first title | no | **KEEP** |
| 5 | Subtask text is never translated | yes | **FIX** — highest user-visible value |
| 6 | `sessions[].lastUsedAt` is never updated | yes | **KEEP** the field; **FIX** the id churn |
| 7 | `revoke-others` does not verify token ownership | yes | **FIX** — highest severity here |
| 8 | `/me/export` truncation flag and unsorted limit | yes | **FIX** — cheap and it is a GDPR path |

---

## 1. `scansAwaitingReview` is counted two different ways

**Evidence.** `modules/tasks/taskCounts.ts:130-134` guards on `reviewedAt`:

```ts
ScannedDocument.countDocuments({
  userId: uid,
  status: 'ready_for_review',
  reviewedAt: { $exists: false },
}),
```

`modules/tasks/dailyDigest.ts:137` does not:

```ts
tally(ScannedDocument, { userId: uid, status: 'ready_for_review' }),
```

That tally becomes `SourceState.scansAwaitingReview` (`:146`) and is copied verbatim
into the payload at `:290`, so it is both the fingerprint input **and** the number
shown.

**Consequence.** With two scans `ready_for_review` of which one has been reviewed,
`/me/digest` says 2 and `/me/tasks/counts` says 1 — two different "scans to review"
figures on one dashboard. Verified live.

Secondary: stamping `reviewedAt` does not change the digest's number but does move
the fingerprint, so the digest is rebuilt — a paid Gemini prose call — to produce an
identical count.

**Note before you act.** This one is *known* to the reference. `taskCounts.ts:124-129`
comments on it, and `modules/tasks/countsParity.test.ts:115-125` deliberately asserts
only `computeTaskCounts` and never digest parity for this figure. So the divergence
was seen and left; treat the existing test as a constraint on any fix.

**Recommendation: FIX**, taking the guarded count as correct — a reviewed scan is not
awaiting review, and `taskCounts` is what the dashboard badge reads. Cheap, and it
removes a wasted AI call per review. Extend `countsParity.test.ts` to cover the digest
figure so the two cannot drift apart again.

---

## 2. `localDateKey` falls back to the HOST timezone, not UTC

**Evidence.** `modules/tasks/dailyDigest.ts:70-77`:

```ts
function localDateKey(at: Date, timezone: string | undefined): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: timezone,
```

`timeZone: undefined` makes `Intl` use the **host** zone. `safeTimezone`
(`dailyDigest.ts:58-66`) returns `undefined` for an absent *or* invalid zone — and its
own comment claims "Validate once, fall back to UTC", which the code does not do.

Every count path *does* fall back to UTC (`modules/tasks/taskQuery.ts:274-275`,
`if (!timezone) return 0`), and the digest's own Mongo grouping is explicit
(`dailyDigest.ts:245`, `timezone: timezone ?? 'UTC'`). So a single function buckets
its counts against UTC and stamps its date string in host-local time.

**It is the cache key.** Computed at `:341`, read at `:347`
(`DailyDigest.findOne({ userId: uid, localDate })`), upserted at `:390`, and
`{userId, localDate}` is the unique index (`models/DailyDigest.ts:148`). It is also
returned to the client as `payload.localDate`.

**Consequence.** On a non-UTC host, a client that omits `tz` or sends a typo gets a
digest cached under the *server operator's* calendar date while every count inside it
is bucketed to UTC midnight. Near midnight the row for "today" is keyed to the wrong
day and can serve or overwrite the neighbouring day's digest.

**Where the original report overstated it.** This was handed to me as the
highest-impact item on the list. On the evidence it is not, and the ranking matters
because it decides what gets fixed first:

- The trigger is narrow. The first-party client always sends a real zone
  (`Steward/queries/digest.ts:58-60`, `Intl.DateTimeFormat().resolvedOptions().timeZone`).
- On the usual UTC container host the fallback is accidentally correct, so it is
  latent in production rather than active.

**I rank #7 (session revocation) and #5 (untranslated subtasks) above it** — one is a
security hole reachable with a token an attacker may plausibly hold, the other is
visibly broken output for every non-English user, every day. This one needs a
misconfigured client *and* a non-UTC host.

**Recommendation: FIX, on the Node side, with care.** The fix is one line
(`timeZone: timezone ?? 'UTC'`), but `localDate` is a **stored key**: changing it
changes which row a real user reads, so a deploy silently re-keys existing digests and
some users see yesterday's. It needs a migration or an accept-both read path, which is
why it is not the "just fix it" item it looks like.

Also fix the guard test while you are there: `dailyDigest.test.ts:176-181` claims to
prove the UTC fallback but passes on almost any host purely because its clock is
12:00 UTC. It would fail at UTC+14 and does not test what it says it tests.

---

## 3. `keepRealThemes` burns ids before filtering blank labels

**Evidence.** `modules/tasks/dailyDigestProse.ts:198-204`:

```ts
return themes
  .map((theme) => {
    const taskIds = (theme.taskIds ?? []).filter((id) => ids.has(id) && !seen.has(id))
    taskIds.forEach((id) => seen.add(id))
    return { label: theme.label.trim(), count: taskIds.length, taskIds }
  })
  .filter((theme) => theme.label.length > 0 && theme.count > 0)
```

`seen.add` runs inside the `map`; the blank-label rejection is a later `filter`.

**Consequence.** A theme the model returns with an empty or whitespace label claims
its matters, is then discarded, and those matters are gone from the theme strip
entirely — a later correctly-labelled theme covering the same matters shows a lower
count, or is dropped as `count === 0`. Carried-forward themes go through the same path
(`dailyDigest.ts:377`), so one bad generation can suppress themes across later
rebuilds.

**Recommendation: FIX.** Reject blank labels *before* claiming ids — filter first,
then map. It is a one-line reorder with no schema, key or stored-data implications,
and the failure it removes is silently missing dashboard content. This is the cheapest
real fix on the list.

---

## 4. `findDuplicates` tests the raw first-member title

**Evidence.** `modules/tasks/summarize.ts:198` bins on a normalised key but `:208`
and `:212` use the raw title:

```ts
const key = t.title.trim().toLowerCase().replace(/\s+/g, ' ')
...
title: group[0]?.title ?? '',
...
.filter((dupe) => dupe.title.length > 0)
```

`'   '` and `''` both normalise to key `''` and land in one bin, so `group[0].title`
decides survival: `['   ', '']` passes the length check and `['', '   ']` does not.
Order-dependent, exactly as reported.

**Consequence: none today.** `models/Task.ts:310` declares
`title: { type: String, required: true, trim: true }`, so a whitespace-only title
cannot be persisted, and both call sites (`summarize.ts:302`, `dailyDigest.ts:294`)
are fed Task rows. The live effect is milder than reported: the *displayed* title is
the untrimmed text of whichever member sorted first.

**Recommendation: KEEP.** The defect is real but unreachable through the only writer,
and the guard that makes it unreachable (`trim: true` on a required field) is not
going away. Fixing it means touching a hot digest/summary path and re-baselining its
harness rows to buy nothing a user can see. Worth a comment in the source; not worth a
coordinated two-server change.

If it is ever touched for another reason, take `dupe.title` from the normalised key
rather than `group[0]`, which fixes the cosmetic untrimmed-title issue at the same
time.

---

## 5. Subtask text is never translated

**Evidence.** The overlay keys on `_id` — `modules/tasks/matterLocale.ts:70-73`:

```ts
if (copy.subtasks && Array.isArray(presented.subtasks)) {
  presented.subtasks = presented.subtasks.map((sub) => {
    const translated = copy.subtasks?.[String(sub._id)]
```

But `toJSON` has already renamed it — `models/Task.ts:259-262`, wired onto the subtask
schema at `:282`:

```ts
function toIdJSON(_doc: unknown, ret: Record<string, unknown>): Record<string, unknown> {
  ret.id = String(ret._id)
  delete ret._id
```

Both call sites hand `presentMatter` an already-serialised object
(`routes/me.tasks.ts:167-169` and `:574`), so `String(sub._id)` evaluates to the
literal string `"undefined"` and never matches. The writer side keys by real hex ids
(`modules/tasks/translateMatters/run.ts:111`, `:167`), so the data is correct and only
the lookup is wrong.

**Consequence.** An Arabic-reading user opens a translated matter and sees the title
and notes in Arabic with every subtask still in English — **and the translation quota
was already spent** generating the subtask copy that is never shown. Both halves of
that are bad: the visible half is a broken feature, the invisible half is paying per
token for discarded output.

Already recorded as a frozen parity trap in `RESUME.md` and `KERNEL.md` §2.

**Recommendation: FIX — the highest user-visible value on this list.** The change is
`String(sub._id)` → `String(sub.id ?? sub._id)`, which is safe on both serialised and
raw documents. It is a pure display fix: no stored data changes, no keys move, and the
stored translations are already correct, so it starts working immediately for existing
rows.

Be aware it is **not** free at the harness: every task-returning endpoint that leaks
`i18n` currently ships untranslated subtasks on both servers, so several rows re-baseline
together. That is a real cost, and it is still worth paying — this is a feature the
product is currently charging for and not delivering.

---

## 6. `sessions[].lastUsedAt` is never updated

**Evidence.** One write, at creation — `lib/sessions.ts:38-45`:

```ts
await RefreshToken.create({
  userId: user._id,
  tokenHash,
  ...
  lastUsedAt: new Date(),
})
```

No `$set: { lastUsedAt }` exists anywhere. `revokeRefreshToken`, `revokeSessionById`,
`revokeOtherSessions` and `rotateRefreshToken` never touch it, and `requireAuth` never
reads `RefreshToken` at all.

**Consequence, with a correction.** The claim "never updated despite the name" is true
of the *field*, but the user-visible effect is smaller than it sounds:
`rotateRefreshToken` (`sessions.ts:80`) calls `issueSession`, which **creates a new
row** with a fresh `lastUsedAt` and revokes the old one. So the displayed value does
advance on roughly every access-token refresh — `JWT_ACCESS_TTL` defaults to `15m`
(`env.ts:12`). It is approximate, not frozen.

**A genuine secondary bug from the same mechanism, and the more serious one:** because
rotation replaces the row, the session `id` returned by `/auth/sessions/list` changes
every ~15 minutes. So `DELETE /auth/sessions/:id` against an id the user is looking at
can 404 simply because time passed. "Sign out this device" failing intermittently is a
real complaint, and it is much harder to attribute than a stale timestamp.

**Recommendation: KEEP the `lastUsedAt` behaviour, FIX the id churn.** Writing
`lastUsedAt` on every authenticated request adds a database write to the hottest path
in the API to make a settings-screen timestamp more precise — a bad trade. Rotation
already keeps it within one token TTL, which is what the screen actually needs.

The id churn is worth a separate look: give the session family a stable identifier that
survives rotation, and have `DELETE /auth/sessions/:id` resolve through it. That is a
design change, not a patch, so it belongs on the roadmap rather than in a parity pass.

---

## 7. `POST /auth/sessions/revoke-others` does not verify token ownership

**Evidence.** `routes/auth.session.ts:97-107`:

```ts
'/auth/sessions/revoke-others',
requireAuth,
asyncHandler(async (req, res) => {
  const auth = req.auth
  if (!auth) throw Unauthorized()
  const { refreshToken } = RefreshSchema.parse(req.body)
  await revokeOtherSessions(auth.sub, refreshToken)
```

and `lib/sessions.ts:140-147`:

```ts
await RefreshToken.updateMany(
  { userId, revokedAt: { $exists: false }, tokenHash: { $ne: hashToken(currentRawToken) } },
  { $set: { revokedAt: new Date() } },
)
```

**Checked:** a valid access token, and that the body holds a non-empty string.
**Not checked:** that the supplied refresh token exists, is unrevoked, is unexpired, or
belongs to `auth.sub`. It is used purely as an *exclusion* filter — never as an
authorisation input.

**What an attacker can actually do — narrower than "no ownership check" suggests.**
The `updateMany` is hard-scoped to `userId: auth.sub`, so **cross-user revocation is
not possible.** The caller can only affect the account they already hold a token for.
Anyone reading this entry as "one user can sign out another" has misread it.

The real exposure is a **privilege escalation between token types**: a stolen *access*
token alone — a 15-minute bearer, the kind that leaks through logs, referrers or XSS —
is enough to sign every device out of the victim's account. Send any junk string as
`refreshToken`; nothing matches the `$ne`; every live session dies. Normally that
action requires possession of a live *refresh* token, which is the longer-lived,
better-protected credential. So the bug converts a short-lived low-value token into
account-wide disruption, and it hands an attacker who already has a foothold a way to
force the victim through a full re-authentication they may not find suspicious.

Note also this route carries **no `authLimiter`**, unlike `/auth/refresh`
(`auth.session.ts:24`).

**Secondary, and it bites honest clients:** a client that sends a slightly wrong token
gets `204` and silently logs *itself* out along with everything else.

**Recommendation: FIX — the highest severity on this list.** Look the token up, verify
it is live and belongs to `auth.sub`, and 401 if not. That also fixes the silent
self-logout, because a wrong token stops being indistinguishable from a right one. Add
the limiter while you are in there.

This is the entry I would move first, ahead of #2. It is a small, self-contained
change on a route the harness barely exercises, and unlike most of this list the
argument for fixing it does not depend on anyone's judgement about product polish.

---

## 8. `GET /me/export` truncation flag, unsorted and unpaginated

**Evidence.** `routes/me.export.ts:40-45`:

```ts
function section<T>(items: T[]): Section<T> {
  return {
    count: items.length,
    truncated: items.length === MAX_PER_COLLECTION,
```

and all eleven queries take the same shape (`me.export.ts:75`):

```ts
Task.find(scope).limit(limit).lean(),
```

No `.sort()`, no cursor, no `countDocuments` to compare against.
`MAX_PER_COLLECTION = 5_000` (`:32`).

**Consequence.** Two distinct defects:

- A user with **exactly** 5000 matters gets a complete export labelled
  `truncated: true`.
- A user with **more** than 5000 gets an arbitrary, non-reproducible subset —
  unsorted `find` returns storage order, which shifts as documents move and as the
  planner picks different indexes — with no way to fetch the remainder.

The file's own header (`:29-31`) promises nothing is quietly lost. It is.

**Recommendation: FIX.** This is a data-portability endpoint; a silently incomplete
export is the one failure mode it must not have. Sort by `_id`, fetch `limit + 1` and
report `truncated` from whether the extra row existed, and expose a cursor. Sorting
also makes the export deterministic, which is what makes it diffable and testable at
all.

Cheap, and it is the entry with the clearest external obligation attached — a GDPR
subject-access response that omits records without saying so is a compliance problem,
not just a bug.
