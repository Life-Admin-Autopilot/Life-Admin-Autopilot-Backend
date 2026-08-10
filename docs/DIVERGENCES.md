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
