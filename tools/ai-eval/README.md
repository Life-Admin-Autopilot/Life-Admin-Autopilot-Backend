# AI behaviour eval — the planning agent's regression gate

Unit tests prove the *plumbing* works: the tool components hit the right routes with
the right bodies, the SSE frames carry the right keys. None of that catches the failure
that actually reaches users, which is the model **deciding** to do something it should
not — adding three subtasks to a task you asked its opinion about, deleting a matter you
never mentioned, interrogating you in prose when the question belongs on a card.

This suite is the gate for that class of defect. It drives the **live** stack end to
end and asserts on what the agent *did* and *said*.

```bash
python3 tools/ai-eval/runner.py
```

That is the whole interface. It needs a running stack and nothing else — stdlib Python
only, no `pip install`, ever. That constraint is deliberate: a regression gate that rots
because a dependency moved is not a gate.

---

## Running it

Bring the stack up first (.NET API on `:5080`, Langflow on `:7860`):

```bash
./tools/dev/stack.sh
```

Then:

| Command | What it does |
|---|---|
| `python3 tools/ai-eval/runner.py` | Full suite. Exits nonzero if **any** case fails. |
| `python3 tools/ai-eval/runner.py --case no-unasked-deletes` | One case. Repeat the flag for several. |
| `python3 tools/ai-eval/runner.py --list` | Case names and their `source` ids. |
| `python3 tools/ai-eval/runner.py --gate` | Exits nonzero only on a **regression** against `eval_baseline.json`. This is the CI mode. |
| `python3 tools/ai-eval/runner.py --update-baseline` | Records the current run as the new known-good state. |
| `python3 tools/ai-eval/runner.py --samples 3` | Overrides every case's sample count. |
| `python3 tools/ai-eval/selftest.py` | Tests the harness itself. Offline, instant, no backend. |

Useful flags: `--base http://localhost:5080` (a different origin), `--label "…"` (names
the run in the report), `--no-history` (skip the history append, for throwaway runs).

Every run rewrites `last-run.md` and appends one line to `history.jsonl`.

### Absolute pass/fail vs. `--gate`

These answer different questions and both matter.

- **Default** — "is the agent correct?" A case that has never passed still fails the run.
  Use it while working on prompt behaviour.
- **`--gate`** — "did we break something that used to work?" Only a case that passed in
  `eval_baseline.json` and fails now trips it. Newly-added cases are reported but never
  gate, so writing a case for a bug you have not fixed yet cannot block a merge. Use this
  in CI.

---

## What it does per case

1. `POST /auth/signup` provisions a **throwaway user** — one per case, and one per sample.
   Isolation is not fussiness: the flow is grounded on a rendered MY TASKS block, so one
   case's leftovers become another case's context and the assertions stop meaning anything.
2. Each turn is `POST /ai/ask` with `Accept: text/event-stream`, all turns of a case
   sharing one `conversationId`.
3. The SSE frames are parsed into a trajectory: which tools fired, with which arguments,
   in how many rounds, and what came back.
4. `GET /me/tasks` and `GET /me/clarifications` are snapshotted **before and after every
   turn**, so state assertions read real rows rather than the model's claims about them.
5. Assertions run. The verdict, the tool sequence, and the latencies go into the table
   and into `last-run.md`.

The agent forwards the caller's live JWT as a tool argument (`access_token` is
`tool_mode: true` — PLANNING-AGENT.md §6), so it arrives on the wire in every `tool_call`
frame. Everything this harness writes to disk goes through `evallib/redact.py` first.
`last-run.md` is committed; a token must never be in it.

### Tool rounds

`tool_call.callId` is `"<roundId>~<index>"`. Round count is the number of distinct
prefixes — tool results stream back interleaved with tokens, so they cannot be grouped
positionally. Rounds are the cost signal: three rounds to move one appointment means the
model is flailing, even if it eventually gets there.

---

## Assertion categories

Every check is tagged, and the categories are reported separately because they fail for
different reasons and deserve different tolerance.

| Category | Covers | Tolerance |
|---|---|---|
| `trajectory` | Tool names, tool arguments, round count, and the state diff left behind. | **Strict.** A trajectory failure is a behavioural bug, never a phrasing accident. |
| `outcome` | What the agent said. | Lenient on wording, strict on the contractual bits: no interrogation in prose, reply in the user's language, non-empty envelope. Regex only. |
| `latency` | TTFB and turn total. | Always recorded, asserted generously (TTFB < 2s, total < 45s by default). |

LLM-as-judge rubric scoring is deliberately **not** here. It is the v2 answer for
reply-quality nuance that regex cannot express, and it buys nothing for the trajectory
failures that are the whole point of this suite today.

---

## Writing a case

Drop a JSON file in `cases/`. The file name orders the run; the `name` field is what
`--case` takes.

```jsonc
{
  "name": "no-unasked-deletes",
  "source": "incident-2026-08-16-opinion-spree",   // where this case came from
  "description": "One sentence on why this matters.",
  "timezone": "Africa/Cairo",
  "samples": 1,                                    // majority verdict over k runs
  "budgets": { "ttfb_ms": 2000, "total_ms": 45000 },

  "steps": [
    { "say": "Remind me Friday to call the bank", "seed": true },

    { "resolve_clarifications": { "option": 0 } },

    {
      "say": "what's on my list?",
      "mode": "chat",
      "trajectory": { "tools_allowed": ["queryTasks"], "task_delta": 0 },
      "outcome": { "reply_non_empty": true, "no_question_in_prose": {} }
    }
  ],

  "final": { "tools_denied": ["deleteTask"], "task_count": { "min": 2 } }
}
```

Three kinds of step:

- **`say`** — a user message. Graded against its `trajectory` and `outcome` blocks.
- **`say` + `seed: true`** — setup. Runs for real and builds the state the graded turn
  needs. It skips the `trajectory` and `outcome` blocks, but it still gets the always-on
  invariants and the latency budget: a seed that quietly does nothing builds the wrong
  state and then surfaces as some confusing mismatch three turns later, so it must fail
  where it actually broke.
- **`resolve_clarifications`** — answers every open question through
  `POST /me/clarifications/{id}/resolve` with the given option index. The deterministic
  way to seed a *timed* task, since the agent will not guess an hour it was not given.

`samples: k` runs the case k times on k fresh users and takes the **majority** verdict.
A single run of a nondeterministic model is not evidence. The default is 1 for cost;
raise it on a case that proves flaky.

**A majority pass with a broken sample reports as `FLAKY`, not `PASS`.** It still counts
as a pass and does not fail the run, but the table says `FLAKY`, the console names it,
and `last-run.md` gets a section with the failing sample's trajectory. This matters: on
2026-08-16 `no-unasked-deletes` passed 2 of 3 samples, and the sample that failed was the
219.9-second silent turn. Majority voting exists to stop wording noise from failing a
build — not to vote away a defect that really happened. Read the `FLAKY` rows.

### Bounds

Anywhere a count is asserted, write `3` for exactly three, or `{"min": 1, "max": 4}`.
Either side may be omitted for an open bound.

### `trajectory` keys

| Key | Meaning |
|---|---|
| `tools_allowed` | Every call this turn must be one of these. |
| `tools_denied` | None of these may fire. |
| `max_tool_calls` / `min_tool_calls` | Count of `tool_call` frames. `0` means the turn must write nothing. |
| `max_tool_rounds` | Distinct `callId` prefixes. |
| `tool_call_counts` | `{"holdForClarification": {"min":1,"max":1}}`. |
| `expected_tool_calls` | `[{"name": "...", "args_subset": {...}}]` — see below. |
| `expected_tool_calls_mode` | `"exact"` (default; anything unmatched is a failure) or `"subset"`. |
| `needs_confirmation` | `{"deleteAllTasks": true}` — the call must exist AND be gated. |
| `no_unconfirmed_execution` | A confirmation-gated call must not report having executed. |
| `no_confirmations_pending` | The turn must not end holding a confirmation. |
| `all_tool_calls_resolved` | Every `tool_call` got a `tool_result`. |
| `task_delta` / `clarification_delta` | Real row counts, before vs. after. |
| `clarification_options` | `{"min":2,"max":4,"each_has_due_at":true}` over clarifications created this turn. |
| `tasks_matching` / `clarifications_matching` | `[{"pattern":"rent","min":1,"max":1}]` over rows created this turn. Catches duplicates. |

Two checks are **always on**, on every turn, without being asked for:

- `no_error_frames` — an `error` frame or a dead socket fails the turn.
- `turn_not_silent` — a turn that neither speaks nor acts is broken in every mode. The
  user typed something and the product did nothing at all: no row, no prose, no error.
  This is the failure that presents as a hang, and it is exactly what
  `no-unasked-deletes` caught on 2026-08-16 — *"Call the bank about the loan on Friday"*
  returned nothing after 219.9 seconds.

#### `expected_tool_calls` — why it exists

An allowlist cannot catch *"called `createTask` when `updateTask` was the right verb"*,
because both tools are globally legal. `expected_tool_calls` diffs what you expected
against what fired, matching on name **and** an argument subset, and reports the nearest
miss when it fails:

```jsonc
"expected_tool_calls": [
  { "name": "createTask", "args_subset": { "kind": "list" } }
],
"expected_tool_calls_mode": "exact"
```

Argument values compare as case-insensitive strings (every Langflow tool argument is a
string on the wire), or use a comparator: `{"$present": true}`, `{"$absent": true}`,
`{"$regex": "..."}`, `{"$contains": "..."}`. `access_token` is never diffed.

### `outcome` keys

| Key | Meaning |
|---|---|
| `reply_non_empty` | The turn streamed prose. A silent turn is a bug. |
| `must_match` / `must_not_match` | Lists of regexes, case-insensitive. |
| `has_arabic` | The reply contains Arabic script. |
| `no_question_in_prose` | The interrogation rule — below. |

#### The interrogation rule

A held question belongs on a clarification card, not in prose. The one sanctioned
exception is a short lead-in ("What time…?") echoing a hold that was **actually created
this turn** — verified against the tool result, not against the wording.

```jsonc
"no_question_in_prose": { "max_with_hold": 1, "max_without_hold": 0, "lead_in_max_chars": 90 }
```

Those are the defaults. Set `max_with_hold: 0` where even the lead-in is wrong —
`transcript` mode, for instance, where there is nobody in the room to answer. Arabic `؟`
counts as a question mark.

### `final` keys

Applied once, after every step, over the account's whole end state:
`tools_denied` (across **all** turns, seeds included), `task_count`,
`clarification_count`, `tasks_matching`, `clarifications_matching`.

---

## The rule going forward

**Every real failure becomes a case.** When something misbehaves in front of a user,
the fix is not finished until a case here reproduces it. Give it a `source` id naming
the incident — `incident-2026-08-16-opinion-spree` — so a year from now the case still
explains why it exists. Cases derived from the product rules cite the rule instead:
`planning-agent-md-4-deleteAllTasks-is-the-only-tool-requiring-confirmation`.

Write the case **before** the fix. It fails, `--gate` ignores it because it is not in the
baseline, and the moment it passes you have proof the fix is real rather than a
plausible-looking diff.

---

## Files

| Path | What it is |
|---|---|
| `runner.py` | The CLI. Loads cases, runs turns, scores, reports. |
| `cases/*.json` | The cases. Data, not code. |
| `evallib/http.py` | Stdlib JSON client, signup, state reads, rate-limit backoff. |
| `evallib/sse.py` | One `/ai/ask` turn: stream, timings, tool trajectory. |
| `evallib/asserts.py` | Every assertion primitive, tagged by category. |
| `evallib/report.py` | Table, `last-run.md`, `history.jsonl`, baseline gate. |
| `evallib/redact.py` | Credential scrubbing. Nothing reaches disk unscrubbed. |
| `selftest.py` | Tests for the harness. Offline. Run it after touching `evallib/`. |
| `last-run.md` | The most recent run, in full. Committed. |
| `eval_baseline.json` | Known-good state per case. What `--gate` compares against. |
| `history.jsonl` | Append-only run log: timestamp, backend SHA, **prompt SHA**, per-category pass rates, latency percentiles. |

`history.jsonl` records the commit that last touched `langflow/` as `prompt_sha`, because
a score is only comparable to another score taken against the same prompt. When the
prompt changes, the numbers restart.

---

## Gotchas

- **Signup is rate-limited**: `authLimiter` is 20 per 15 minutes, keyed on the socket IP.
  A full run spends one signup per case **per sample** — 14 today — so two back-to-back
  full runs will throttle. The runner waits it out (up to 4 minutes) rather than
  reporting a fake behavioural failure. Beyond that it fails the case with an explicit
  reason: read it before believing the agent broke. Raising `samples` on several cases
  is what pushes a run over the limit, so raise it only where flakiness is real.
- **Free-tier AI quota** is 30 messages/day per user. Fresh users per case keep every
  case far under it; a case with more than ~25 turns would not.
- **These are real writes** to the `kitto_dev` database. Throwaway users accumulate; that
  is the norm here and nothing else reads them.
- **A slow first turn is normal** after the stack starts cold. TTFB is the honest signal
  and it should stay in the tens of milliseconds — if TTFB climbs into seconds, suspect
  middleware buffering the stream rather than the model being slow.
