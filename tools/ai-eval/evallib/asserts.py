"""Assertion primitives.

Every check is tagged with a category, and the categories are reported
separately because they fail for different reasons and deserve different
tolerance:

``trajectory``
    What the agent *did* — tool names, tool arguments, tool-round count, and the
    state diff it left behind (/me/tasks, /me/clarifications). Strict. A
    trajectory failure is a behavioural bug, never a phrasing accident.

``outcome``
    What the agent *said*. Lenient on wording, strict on the few things that are
    contractual (no interrogation in prose, the user's language, non-empty
    envelope). Regex only — LLM-as-judge rubric scoring is deliberately out of
    scope for v1.

``latency``
    Always recorded, asserted generously. A slow turn is a real regression but
    not the same class of defect as a wrong one.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Iterable, Mapping

from .sse import Turn

TRAJECTORY = "trajectory"
OUTCOME = "outcome"
LATENCY = "latency"

DEFAULT_TTFB_MS = 2000.0
DEFAULT_TOTAL_MS = 45000.0

ARABIC_RE = re.compile(r"[؀-ۿ]")
#: A sentence that ends in a question mark, Latin or Arabic.
QUESTION_RE = re.compile(r"[^.!?؟\n]*[?؟]")

#: Never diffed: the agent forwards the caller's bearer token as a tool argument.
IGNORED_ARGS = frozenset({"access_token", "accessToken"})


@dataclass(frozen=True)
class Check:
    category: str
    name: str
    ok: bool
    detail: str

    def line(self) -> str:
        mark = "PASS" if self.ok else "FAIL"
        return f"[{mark}] ({self.category}) {self.name}: {self.detail}"


# ---------------------------------------------------------------------------
# bounds
# ---------------------------------------------------------------------------


def _bounds(spec: Any) -> tuple[float, float]:
    """``3`` -> (3,3); ``{"min":1,"max":4}`` -> (1,4); missing side is open."""
    if isinstance(spec, (int, float)):
        return float(spec), float(spec)
    if isinstance(spec, Mapping):
        low = spec.get("min", spec.get("exact", float("-inf")))
        high = spec.get("max", spec.get("exact", float("inf")))
        return float(low), float(high)
    raise ValueError(f"Not a bounds spec: {spec!r}")


def _in_bounds(value: float, spec: Any) -> tuple[bool, str]:
    low, high = _bounds(spec)
    return low <= value <= high, f"got {value:g}, expected {_fmt_bounds(low, high)}"


def _fmt_bounds(low: float, high: float) -> str:
    if low == high:
        return f"exactly {low:g}"
    if low == float("-inf"):
        return f"<= {high:g}"
    if high == float("inf"):
        return f">= {low:g}"
    return f"{low:g}..{high:g}"


# ---------------------------------------------------------------------------
# argument matching
# ---------------------------------------------------------------------------


def _arg_matches(actual: Any, expected: Any) -> bool:
    if isinstance(expected, Mapping):
        if "$present" in expected:
            present = actual not in (None, "", [], {})
            return present is bool(expected["$present"])
        if "$absent" in expected:
            absent = actual in (None, "", [], {})
            return absent is bool(expected["$absent"])
        if "$regex" in expected:
            return re.search(str(expected["$regex"]), str(actual or ""), re.I) is not None
        if "$contains" in expected:
            return str(expected["$contains"]).lower() in str(actual or "").lower()
    return str(actual or "").strip().lower() == str(expected).strip().lower()


def _args_subset_matches(args: Mapping[str, Any], subset: Mapping[str, Any]) -> list[str]:
    """Return the list of mismatch descriptions; empty means it matched."""
    problems: list[str] = []
    for key, expected in subset.items():
        if not _arg_matches(args.get(key), expected):
            problems.append(f"{key}={args.get(key)!r} (wanted {expected!r})")
    return problems


# ---------------------------------------------------------------------------
# trajectory
# ---------------------------------------------------------------------------


def check_trajectory(spec: Mapping[str, Any], turn: Turn) -> list[Check]:
    checks: list[Check] = []
    add = checks.append
    calls = turn.tool_calls

    # Always-on: an error frame or a dead socket invalidates everything else.
    add(
        Check(
            TRAJECTORY,
            "no_error_frames",
            not turn.error_frames and turn.transport_error is None,
            turn.transport_error
            or (f"error frames: {turn.error_frames}" if turn.error_frames else "clean"),
        )
    )

    # Always-on: a turn that neither speaks nor acts is broken in every mode.
    # The user typed something and the product did nothing at all — no row, no
    # prose, no error. This is the failure that looks like a hang.
    silent = not turn.reply.strip() and not calls
    add(
        Check(
            TRAJECTORY,
            "turn_not_silent",
            not silent,
            f"no reply and no tool calls after {_ms(turn.total_ms)}"
            if silent
            else f"{len(turn.reply.strip())} chars of prose, {len(calls)} tool call(s)",
        )
    )

    if "tools_allowed" in spec:
        allowed = set(spec["tools_allowed"])
        offenders = sorted({c.name for c in calls if c.name not in allowed})
        add(
            Check(
                TRAJECTORY,
                "tools_allowed",
                not offenders,
                f"disallowed {offenders}; sequence: {turn.tool_sequence()}"
                if offenders
                else f"only {sorted(allowed & set(turn.tool_names)) or 'none'} used",
            )
        )

    if "tools_denied" in spec:
        denied = set(spec["tools_denied"])
        offenders = sorted({c.name for c in calls if c.name in denied})
        add(
            Check(
                TRAJECTORY,
                "tools_denied",
                not offenders,
                f"forbidden tool(s) fired: {offenders}; sequence: {turn.tool_sequence()}"
                if offenders
                else f"none of {sorted(denied)} fired",
            )
        )

    if "max_tool_calls" in spec:
        ok, detail = _in_bounds(len(calls), {"max": spec["max_tool_calls"]})
        add(Check(TRAJECTORY, "max_tool_calls", ok, f"{detail}; {turn.tool_sequence()}"))

    if "min_tool_calls" in spec:
        ok, detail = _in_bounds(len(calls), {"min": spec["min_tool_calls"]})
        add(Check(TRAJECTORY, "min_tool_calls", ok, f"{detail}; {turn.tool_sequence()}"))

    if "max_tool_rounds" in spec:
        ok, detail = _in_bounds(turn.tool_rounds, {"max": spec["max_tool_rounds"]})
        add(Check(TRAJECTORY, "max_tool_rounds", ok, detail))

    for name, bounds in (spec.get("tool_call_counts") or {}).items():
        count = sum(1 for c in calls if c.name == name)
        ok, detail = _in_bounds(count, bounds)
        add(Check(TRAJECTORY, f"tool_call_count[{name}]", ok, detail))

    if "expected_tool_calls" in spec:
        checks.extend(_check_expected_calls(spec, turn))

    if "needs_confirmation" in spec:
        for name, expected in spec["needs_confirmation"].items():
            matching = [c for c in calls if c.name == name]
            actual = any(c.needs_confirmation for c in matching)
            ok = bool(matching) and actual is bool(expected)
            add(
                Check(
                    TRAJECTORY,
                    f"needs_confirmation[{name}]",
                    ok,
                    f"{len(matching)} call(s), needsConfirmation={actual}, wanted {expected}",
                )
            )

    if spec.get("no_confirmations_pending"):
        gated = [c.name for c in calls if c.needs_confirmation]
        add(
            Check(
                TRAJECTORY,
                "no_confirmations_pending",
                not gated,
                f"turn ended waiting on {gated}" if gated else "turn ended with nothing pending",
            )
        )

    if spec.get("all_tool_calls_resolved"):
        dangling = [c.name for c in calls if not c.resolved]
        add(
            Check(
                TRAJECTORY,
                "all_tool_calls_resolved",
                not dangling,
                f"no tool_result frame for {dangling}" if dangling else "every call resolved",
            )
        )

    if spec.get("no_unconfirmed_execution"):
        leaked = [
            c for c in calls if c.needs_confirmation and c.resolved and c.error is None
            and _looks_executed(c.result)
        ]
        add(
            Check(
                TRAJECTORY,
                "no_unconfirmed_execution",
                not leaked,
                f"{len(leaked)} confirmation-gated call(s) reported execution"
                if leaked
                else "gated calls stayed dry",
            )
        )

    if "task_delta" in spec:
        ok, detail = _in_bounds(turn.task_delta, spec["task_delta"])
        titles = [t.get("title") for t in turn.new_tasks()]
        add(Check(TRAJECTORY, "task_delta", ok, f"{detail}; created {titles}"))

    if "clarification_delta" in spec:
        ok, detail = _in_bounds(turn.clarification_delta, spec["clarification_delta"])
        questions = [c.get("question") for c in turn.new_clarifications()]
        add(Check(TRAJECTORY, "clarification_delta", ok, f"{detail}; asked {questions}"))

    if "clarification_options" in spec:
        checks.extend(_check_clarification_options(spec["clarification_options"], turn))

    if spec.get("no_failed_tool_calls"):
        checks.append(_check_no_failed_calls(turn))

    if spec.get("no_orphan_clarifications"):
        checks.append(_check_no_orphan_clarifications(turn))

    for rule in spec.get("tasks_matching") or []:
        checks.append(
            _check_matching(
                "tasks_matching",
                rule,
                [_task_text(t) for t in turn.new_tasks()],
            )
        )

    for rule in spec.get("clarifications_matching") or []:
        checks.append(
            _check_matching(
                "clarifications_matching",
                rule,
                [_clarification_text(c) for c in turn.new_clarifications()],
            )
        )

    return checks


def _check_no_failed_calls(turn: Turn) -> Check:
    """No tool call in this turn came back an error.

    A failed call is not just a wasted round trip: it PERSISTS as a receipt, so
    the chat renders it forever afterwards. The 2026-08-17 phantom-questions
    incident was a failed `holdForClarification` sitting beside the successful
    retry that repaired it, drawn as two answerable question cards for one
    matter. The specific cause was `access_token` being a tool argument the
    model had to copy on every call; this assertion is what keeps it gone.
    """
    failed = []
    for call in turn.tool_calls:
        result = call.result
        error = call.error or (
            result.get("error") if isinstance(result, Mapping) and result.get("ok") is False else None
        )
        if error:
            failed.append(f"{call.name}:{error}")
    return Check(
        TRAJECTORY,
        "no_failed_tool_calls",
        not failed,
        f"failed calls: {failed}" if failed else f"{len(turn.tool_calls)} call(s), none failed",
    )


def _check_no_orphan_clarifications(turn: Turn) -> Check:
    """The rows on disk and the receipts in the transcript are the same set.

    Both directions matter, and each is a real bug shape:

    A row with no receipt is a question the chat cannot render or resolve — it
    only ever appears in the queue, detached from the turn that raised it.

    A receipt with no row is the phantom: the chat draws an answerable question
    card for something that was never persisted, so answering it cannot resolve
    anything. That is exactly what a queue-full hold produces — it returns a
    filed task and `clarification: null` — and it is why the card must key off
    receipts rather than off the mere presence of a hold call.
    """
    on_disk = {str(c.get("id") or c.get("_id")) for c in turn.new_clarifications()}
    on_disk.discard("None")

    receipts: set[str] = set()
    for call in turn.tool_calls:
        result = call.result
        if not isinstance(result, Mapping):
            continue
        rows = result.get("clarifications")
        if not isinstance(rows, list):
            single = result.get("clarification")
            rows = [single] if isinstance(single, Mapping) else []
        for row in rows:
            if isinstance(row, Mapping) and row.get("id"):
                receipts.add(str(row["id"]))

    orphan_rows = sorted(on_disk - receipts)
    orphan_receipts = sorted(receipts - on_disk)
    ok = not orphan_rows and not orphan_receipts
    detail = f"{len(on_disk)} row(s), {len(receipts)} receipt(s) — matched"
    if not ok:
        detail = (
            f"rows with no receipt: {orphan_rows}; receipts with no row: {orphan_receipts}"
        )
    return Check(TRAJECTORY, "no_orphan_clarifications", ok, detail)


def _looks_executed(result: Any) -> bool:
    """`deleteAllTasks` must only ever come back as a dry-run preview."""
    if not isinstance(result, Mapping):
        return False
    if result.get("dryRun") is True or "affectedCount" in result:
        return False
    return bool(result.get("deleted") or result.get("deletedCount"))


def _check_expected_calls(spec: Mapping[str, Any], turn: Turn) -> list[Check]:
    """Diff expected [{name, args_subset}] against what actually fired.

    An allow/deny list cannot catch "called createTask when updateTask was the
    right verb" if both tools are globally legal, which is why this exists.
    """
    expected: list[Mapping[str, Any]] = list(spec["expected_tool_calls"])
    mode = spec.get("expected_tool_calls_mode", "exact")
    remaining = list(turn.tool_calls)
    missing: list[str] = []

    for want in expected:
        name = want["name"]
        subset = want.get("args_subset") or {}
        hit = None
        near_misses: list[str] = []
        for call in remaining:
            if call.name != name:
                continue
            problems = _args_subset_matches(call.args, subset)
            if not problems:
                hit = call
                break
            near_misses.append(f"{name}({'; '.join(problems)})")
        if hit is None:
            detail = f"{name}"
            if subset:
                detail += f" with {dict(subset)}"
            if near_misses:
                detail += f" -- closest: {near_misses[0]}"
            missing.append(detail)
        else:
            remaining.remove(hit)

    checks = [
        Check(
            TRAJECTORY,
            "expected_tool_calls",
            not missing,
            f"missing {missing}; actual: {turn.tool_sequence()}"
            if missing
            else f"all {len(expected)} expected call(s) present",
        )
    ]

    if mode == "exact":
        extras = sorted(c.name for c in remaining)
        checks.append(
            Check(
                TRAJECTORY,
                "no_unexpected_tool_calls",
                not extras,
                f"unexpected {extras}; actual: {turn.tool_sequence()}"
                if extras
                else "nothing extra fired",
            )
        )
    return checks


def _check_clarification_options(rule: Mapping[str, Any], turn: Turn) -> list[Check]:
    new = turn.new_clarifications()
    checks: list[Check] = []
    if not new:
        return [
            Check(
                TRAJECTORY,
                "clarification_options",
                False,
                "no clarification was created, so its options cannot be checked",
            )
        ]
    for clarification in new:
        options = list(clarification.get("options") or [])
        label = (clarification.get("question") or "?")[:48]
        ok, detail = _in_bounds(
            len(options), {"min": rule.get("min", 0), "max": rule.get("max", 4)}
        )
        checks.append(
            Check(TRAJECTORY, f"clarification_options[{label}]", ok, f"option count {detail}")
        )
        if rule.get("each_has_due_at"):
            dateless = [o.get("label") for o in options if not o.get("dueAt")]
            checks.append(
                Check(
                    TRAJECTORY,
                    f"clarification_options_dueAt[{label}]",
                    not dateless,
                    f"options without dueAt: {dateless}" if dateless else "every option dated",
                )
            )
    return checks


def _check_matching(name: str, rule: Mapping[str, Any], haystacks: Iterable[str]) -> Check:
    pattern = re.compile(rule["pattern"], re.I)
    hits = [text for text in haystacks if pattern.search(text)]
    ok, detail = _in_bounds(len(hits), rule)
    return Check(
        TRAJECTORY,
        f"{name}[{rule['pattern']}]",
        ok,
        f"{detail}; matched {[h[:60] for h in hits]}",
    )


def _task_text(task: Mapping[str, Any]) -> str:
    return " ".join(
        str(task.get(key) or "") for key in ("title", "notes", "domain", "kind")
    )


def _clarification_text(clarification: Mapping[str, Any]) -> str:
    draft = clarification.get("draft") or {}
    return " ".join(
        str(part or "")
        for part in (
            clarification.get("question"),
            clarification.get("sourceText"),
            draft.get("title"),
            draft.get("notes"),
        )
    )


# ---------------------------------------------------------------------------
# outcome
# ---------------------------------------------------------------------------


def check_outcome(spec: Mapping[str, Any], turn: Turn) -> list[Check]:
    checks: list[Check] = []
    add = checks.append
    reply = turn.reply.strip()

    if spec.get("reply_non_empty"):
        add(
            Check(
                OUTCOME,
                "reply_non_empty",
                bool(reply),
                f"{len(reply)} chars" if reply else "the turn streamed no prose at all",
            )
        )

    for pattern in spec.get("must_match") or []:
        hit = re.search(pattern, reply, re.I | re.S)
        add(
            Check(
                OUTCOME,
                f"must_match[{pattern}]",
                hit is not None,
                f"reply: {reply[:200]!r}" if hit is None else f"matched {hit.group(0)[:60]!r}",
            )
        )

    for pattern in spec.get("must_not_match") or []:
        hit = re.search(pattern, reply, re.I | re.S)
        add(
            Check(
                OUTCOME,
                f"must_not_match[{pattern}]",
                hit is None,
                f"forbidden phrasing {hit.group(0)[:80]!r} in reply: {reply[:200]!r}"
                if hit
                else "absent",
            )
        )

    if "no_question_in_prose" in spec:
        add(_check_no_question(spec["no_question_in_prose"], turn))

    if spec.get("has_arabic"):
        add(
            Check(
                OUTCOME,
                "has_arabic",
                ARABIC_RE.search(reply) is not None,
                f"reply: {reply[:160]!r}",
            )
        )

    return checks


def _check_no_question(rule: Any, turn: Turn) -> Check:
    """The interrogation rule.

    A held question belongs in a clarification card, not in prose. The ONE
    sanctioned exception is a short lead-in ("What time…?") echoing a hold that
    was actually created this turn — anything longer, or anything at all when no
    hold was written, is the agent interrogating the user.
    """
    config: Mapping[str, Any] = rule if isinstance(rule, Mapping) else {}
    lead_in_max = int(config.get("lead_in_max_chars", 90))
    allowance = int(
        config.get("max_with_hold", 1) if turn.held else config.get("max_without_hold", 0)
    )

    questions = [q.strip() for q in QUESTION_RE.findall(turn.reply) if q.strip()]
    context = "a hold was created" if turn.held else "no hold was created"

    if len(questions) > allowance:
        return Check(
            OUTCOME,
            "no_question_in_prose",
            False,
            f"{len(questions)} question(s) in prose but {allowance} allowed "
            f"({context}): {[q[:90] for q in questions]}",
        )

    too_long = [q for q in questions if len(q) > lead_in_max]
    if too_long:
        return Check(
            OUTCOME,
            "no_question_in_prose",
            False,
            f"lead-in longer than {lead_in_max} chars: {[q[:120] for q in too_long]}",
        )

    return Check(
        OUTCOME,
        "no_question_in_prose",
        True,
        f"{len(questions)}/{allowance} question(s) in prose ({context})",
    )


# ---------------------------------------------------------------------------
# latency
# ---------------------------------------------------------------------------


def check_latency(budgets: Mapping[str, Any], turn: Turn) -> list[Check]:
    ttfb_budget = float(budgets.get("ttfb_ms", DEFAULT_TTFB_MS))
    total_budget = float(budgets.get("total_ms", DEFAULT_TOTAL_MS))
    checks: list[Check] = []

    ttfb = turn.ttfb_ms
    checks.append(
        Check(
            LATENCY,
            "ttfb_ms",
            ttfb is not None and ttfb <= ttfb_budget,
            f"{_ms(ttfb)} (budget {ttfb_budget:.0f}ms)",
        )
    )
    total = turn.total_ms
    checks.append(
        Check(
            LATENCY,
            "total_ms",
            total is not None and total <= total_budget,
            f"{_ms(total)} (budget {total_budget:.0f}ms)",
        )
    )
    return checks


def _ms(value: float | None) -> str:
    return "n/a" if value is None else f"{value:.0f}ms"


# ---------------------------------------------------------------------------
# after — answering ONE held question, then grading what survives
# ---------------------------------------------------------------------------


def first_matching(
    clarifications: Iterable[Mapping[str, Any]],
    where: Mapping[str, Any],
) -> dict[str, Any] | None:
    """The first row matching every clause of ``where``.

    ``kind`` compares exactly; ``question`` is a case-insensitive regex, because a
    case should not have to reproduce the model's wording to point at a row.
    """
    for clarification in clarifications:
        if "kind" in where and clarification.get("kind") != where["kind"]:
            continue
        if "question" in where and not re.search(
            str(where["question"]), str(clarification.get("question") or ""), re.I
        ):
            continue
        return dict(clarification)
    return None


def check_after(
    spec: Mapping[str, Any],
    resolved: Mapping[str, Any] | None,
    clarifications: list[dict[str, Any]],
) -> list[Check]:
    """Grade the state one resolve left behind.

    This is where a multi-question hold either holds up or does not: answering the
    date must promote the task AND leave its siblings open. A hold that folded two
    gaps into one question passes the first half and fails the second, because
    there is no sibling left to find.
    """
    checks: list[Check] = []
    payload = resolved or {}
    still_open = [c for c in clarifications if c.get("status") in (None, "open")]

    if "open_clarifications" in spec:
        ok, detail = _in_bounds(len(still_open), spec["open_clarifications"])
        checks.append(
            Check(
                TRAJECTORY,
                "after.open_clarifications",
                ok,
                f"{detail}; still open: {[(c.get('kind'), (c.get('question') or '')[:48]) for c in still_open]}",
            )
        )

    for rule in spec.get("open_matching") or []:
        checks.append(
            _check_matching(
                "after.open_matching",
                rule,
                [_clarification_text(c) for c in still_open],
            )
        )

    task_spec = spec.get("resolved_task") or {}
    if task_spec:
        task = payload.get("task") or {}
        if "kind" in task_spec:
            checks.append(
                Check(
                    TRAJECTORY,
                    "after.resolved_task.kind",
                    task.get("kind") == task_spec["kind"],
                    f"task kind {task.get('kind')!r}, wanted {task_spec['kind']!r}",
                )
            )
        if task_spec.get("due_at_present"):
            checks.append(
                Check(
                    TRAJECTORY,
                    "after.resolved_task.dueAt",
                    bool(task.get("dueAt")),
                    f"dueAt {task.get('dueAt')!r}",
                )
            )

    if "resolved_status" in spec:
        status = (payload.get("clarification") or {}).get("status")
        checks.append(
            Check(
                TRAJECTORY,
                "after.resolved_status",
                status == spec["resolved_status"],
                f"answered row is {status!r}, wanted {spec['resolved_status']!r}",
            )
        )

    return checks


# ---------------------------------------------------------------------------
# case-level
# ---------------------------------------------------------------------------


def check_final(
    spec: Mapping[str, Any],
    turns: list[Turn],
    tasks: list[dict[str, Any]],
    clarifications: list[dict[str, Any]],
) -> list[Check]:
    """Assertions over the whole case rather than one turn."""
    checks: list[Check] = []
    all_calls = [call for turn in turns for call in turn.tool_calls]

    if "tools_denied" in spec:
        denied = set(spec["tools_denied"])
        offenders = sorted({c.name for c in all_calls if c.name in denied})
        checks.append(
            Check(
                TRAJECTORY,
                "final.tools_denied",
                not offenders,
                f"fired across the case: {offenders}" if offenders else f"none of {sorted(denied)} fired",
            )
        )

    if "task_count" in spec:
        ok, detail = _in_bounds(len(tasks), spec["task_count"])
        checks.append(
            Check(
                TRAJECTORY,
                "final.task_count",
                ok,
                f"{detail}; titles {[t.get('title') for t in tasks]}",
            )
        )

    if "clarification_count" in spec:
        ok, detail = _in_bounds(len(clarifications), spec["clarification_count"])
        checks.append(Check(TRAJECTORY, "final.clarification_count", ok, detail))

    for rule in spec.get("tasks_matching") or []:
        check = _check_matching("final.tasks_matching", rule, [_task_text(t) for t in tasks])
        checks.append(check)

    for rule in spec.get("clarifications_matching") or []:
        checks.append(
            _check_matching(
                "final.clarifications_matching",
                rule,
                [_clarification_text(c) for c in clarifications],
            )
        )

    return checks
