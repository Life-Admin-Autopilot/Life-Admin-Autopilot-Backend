#!/usr/bin/env python3
"""Kitto planning-agent behaviour eval — the AI regression gate.

    python3 tools/ai-eval/runner.py

Runs every JSON case in `cases/` against the LIVE stack (.NET :5080 + Langflow
:7860), provisioning a throwaway user per case so no two cases can see each
other's tasks. Prints a table, writes `last-run.md`, appends `history.jsonl`,
and exits nonzero on failure.

Stdlib only, forever. See README.md.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
import traceback
from pathlib import Path
from typing import Any, Mapping

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from evallib import asserts, report, sse  # noqa: E402
from evallib.http import (  # noqa: E402
    ApiClient,
    DEFAULT_BASE_URL,
    DEFAULT_MAX_RATE_LIMIT_WAIT_S,
)

REPO_ROOT = HERE.parent.parent
CASES_DIR = HERE / "cases"
LAST_RUN = HERE / "last-run.md"
HISTORY = HERE / "history.jsonl"
BASELINE = HERE / "eval_baseline.json"

DEFAULT_TIMEZONE = "Africa/Cairo"


# ---------------------------------------------------------------------------
# case loading
# ---------------------------------------------------------------------------


def load_cases(only: list[str] | None) -> list[dict[str, Any]]:
    files = sorted(CASES_DIR.glob("*.json"))
    cases: list[dict[str, Any]] = []
    for path in files:
        case = json.loads(path.read_text(encoding="utf-8"))
        case.setdefault("name", path.stem)
        case["_path"] = str(path)
        cases.append(case)

    if not only:
        return cases

    wanted = set(only)
    selected = [case for case in cases if case["name"] in wanted]
    missing = wanted - {case["name"] for case in selected}
    if missing:
        known = ", ".join(sorted(case["name"] for case in cases))
        raise SystemExit(f"Unknown case(s): {sorted(missing)}. Known cases: {known}")
    return selected


# ---------------------------------------------------------------------------
# execution
# ---------------------------------------------------------------------------


def run_sample(
    case: Mapping[str, Any],
    client: ApiClient,
    base_url: str,
    sample_index: int,
) -> dict[str, Any]:
    """One independent pass of a case, on its own throwaway user."""
    timezone = case.get("timezone", DEFAULT_TIMEZONE)
    case_budgets = case.get("budgets") or {}

    account = client.signup(label=f"{case['name'][:24]}-s{sample_index}")
    token = account["token"]
    if not token:
        raise RuntimeError("signup returned no accessToken")

    turns: list[sse.Turn] = []
    records: list[dict[str, Any]] = []
    checks: list[asserts.Check] = []
    conversation_id: str | None = None

    for step in case["steps"]:
        if "resolve_clarifications" in step:
            _resolve_open_clarifications(client, token, step["resolve_clarifications"])
            continue

        tasks_before = client.tasks(token)
        clars_before = client.clarifications(token)

        turn = sse.ask(
            base_url,
            token,
            step["say"],
            timezone=step.get("timezone", timezone),
            mode=step.get("mode", "chat"),
            conversation_id=conversation_id,
        )
        conversation_id = turn.conversation_id or conversation_id

        turn.tasks_before = tasks_before
        turn.clarifications_before = clars_before
        turn.tasks_after = client.tasks(token)
        turn.clarifications_after = client.clarifications(token)
        turns.append(turn)

        seed = bool(step.get("seed"))
        budgets = {**case_budgets, **(step.get("budgets") or {})}
        step_checks: list[asserts.Check] = []

        if seed:
            # A seed is setup, not the thing under test — but it still gets the
            # always-on invariants (no error frames, not silent) and the latency
            # budget. A seed that quietly does nothing builds the wrong state and
            # then reports as some confusing mismatch three turns later; it must
            # fail where it actually broke.
            step_checks.extend(asserts.check_trajectory({}, turn))
            step_checks.extend(asserts.check_latency(budgets, turn))
        else:
            step_checks.extend(asserts.check_trajectory(step.get("trajectory") or {}, turn))
            step_checks.extend(asserts.check_outcome(step.get("outcome") or {}, turn))
            step_checks.extend(asserts.check_latency(budgets, turn))
        checks.extend(step_checks)
        records.append(_turn_record(turn, seed))

    if case.get("final"):
        checks.extend(
            asserts.check_final(
                case["final"],
                turns,
                client.tasks(token),
                client.clarifications(token),
            )
        )

    return {
        "passed": all(check.ok for check in checks),
        "checks": checks,
        "turns": records,
        "user": account["email"],
    }


def _resolve_open_clarifications(client: ApiClient, token: str, spec: Mapping[str, Any]) -> None:
    """Answer held questions the deterministic way, so a case can seed real state."""
    option = int(spec.get("option", 0))
    for clarification in client.clarifications(token):
        if clarification.get("status") not in (None, "open"):
            continue
        try:
            client.resolve_clarification(token, clarification["id"], option)
        except Exception:
            # A question whose task is gone closes itself out; not a case failure.
            pass


def _turn_record(turn: sse.Turn, seed: bool) -> dict[str, Any]:
    return {
        "seed": seed,
        "question": turn.question,
        "mode": turn.mode,
        "reply": turn.reply,
        "tool_sequence": turn.tool_sequence(),
        "tool_rounds": turn.tool_rounds,
        "tool_calls": [call.to_json() for call in turn.tool_calls],
        "task_delta": turn.task_delta,
        "clarification_delta": turn.clarification_delta,
        "ttfb_ms": turn.ttfb_ms,
        "first_token_ms": turn.first_token_ms,
        "total_ms": turn.total_ms,
        "transport_error": turn.transport_error,
        "error_frames": turn.error_frames,
    }


def run_case(
    case: Mapping[str, Any],
    client: ApiClient,
    base_url: str,
    samples_override: int | None,
) -> dict[str, Any]:
    """Run a case `samples` times and take the majority verdict.

    A single run of a nondeterministic model is not evidence. `samples: 1` stays
    the default for cost; raise it on a case that proves flaky.
    """
    samples = samples_override or int(case.get("samples", 1))
    results: list[dict[str, Any]] = []
    error: str | None = None

    for index in range(samples):
        try:
            results.append(run_sample(case, client, base_url, index))
        except Exception as exc:
            error = f"{type(exc).__name__}: {exc}"
            traceback.print_exc(file=sys.stderr)
            results.append(
                {
                    "passed": False,
                    "checks": [
                        asserts.Check(
                            asserts.TRAJECTORY, "harness", False, f"case could not run — {error}"
                        )
                    ],
                    "turns": [],
                    "user": None,
                }
            )

    passed_count = sum(1 for result in results if result["passed"])
    passed = passed_count * 2 > samples

    # Report the sample that explains the verdict: a failing one when the case
    # failed, a passing one when it passed.
    representative = next(
        (result for result in results if result["passed"] == passed), results[0]
    )

    # A case that passed by MAJORITY while some sample failed is not clean, and a
    # bare "PASS" would bury a real intermittent defect — which is exactly what
    # happened to the 219.9s silent turn on 2026-08-16. Surface it loudly.
    losing_samples = [result for result in results if not result["passed"]]
    flaky = passed and bool(losing_samples)
    flaky_checks: list[dict[str, Any]] = []
    flaky_turns: list[dict[str, Any]] = []
    flaky_reason = ""
    if flaky:
        loser = losing_samples[0]
        broke = [check for check in loser["checks"] if not check.ok]
        flaky_reason = f"{broke[0].name}: {broke[0].detail}" if broke else "unknown"
        flaky_checks = [
            {"category": c.category, "name": c.name, "ok": c.ok, "detail": c.detail}
            for c in loser["checks"]
        ]
        flaky_turns = loser["turns"]

    turns = representative["turns"]
    graded = [turn for turn in turns if not turn["seed"]] or turns
    failing = [check for check in representative["checks"] if not check.ok]

    return {
        "name": case["name"],
        "source": case.get("source"),
        "description": case.get("description"),
        "passed": passed,
        "samples_run": samples,
        "samples_passed": passed_count,
        "error": error,
        "flaky": flaky,
        "flaky_reason": flaky_reason,
        "flaky_checks": flaky_checks,
        "flaky_turns": flaky_turns,
        "reason": (
            f"{failing[0].name}: {failing[0].detail}"
            if failing
            else (
                f"passed {passed_count}/{samples} — {flaky_reason}"
                if flaky
                else "all checks passed"
            )
        ),
        "checks": [
            {"category": c.category, "name": c.name, "ok": c.ok, "detail": c.detail}
            for c in representative["checks"]
        ],
        "turns": turns,
        "ttfb_ms": _peak(graded, "ttfb_ms"),
        "first_token_ms": _peak(graded, "first_token_ms"),
        "total_ms": _total(turns, "total_ms"),
        "tool_rounds": sum(turn["tool_rounds"] for turn in turns),
    }


def _peak(turns: list[dict[str, Any]], key: str) -> float | None:
    values = [turn[key] for turn in turns if turn.get(key) is not None]
    return max(values) if values else None


def _total(turns: list[dict[str, Any]], key: str) -> float | None:
    values = [turn[key] for turn in turns if turn.get(key) is not None]
    return sum(values) if values else None


# ---------------------------------------------------------------------------
# cli
# ---------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--case", action="append", help="run one case by name (repeatable)")
    parser.add_argument("--base", default=DEFAULT_BASE_URL, help="backend origin")
    parser.add_argument("--samples", type=int, help="override every case's sample count")
    parser.add_argument("--label", help="a name for this run, recorded in the report")
    parser.add_argument("--gate", action="store_true",
                        help="exit nonzero only on a REGRESSION against eval_baseline.json")
    parser.add_argument("--update-baseline", action="store_true",
                        help="record this run as the new known-good baseline")
    parser.add_argument("--list", action="store_true", help="list case names and exit")
    parser.add_argument("--no-history", action="store_true", help="skip appending to history.jsonl")
    parser.add_argument(
        "--max-rate-limit-wait", type=float, default=DEFAULT_MAX_RATE_LIMIT_WAIT_S,
        help="seconds the runner may sleep off a 429 before failing the case "
             "(authLimiter is 15 min / 20 per IP, so a full window is 900)")
    args = parser.parse_args()

    if args.list:
        for case in load_cases(None):
            print(f"{case['name']:36} {case.get('source') or ''}")
        return 0

    cases = load_cases(args.case)
    client = ApiClient(base_url=args.base, max_rate_limit_wait_s=args.max_rate_limit_wait)

    if not client.health():
        print(
            f"The backend at {args.base} is not answering /health.\n"
            "Bring the stack up first: ./tools/dev/stack.sh (API :5080 + Langflow :7860).",
            file=sys.stderr,
        )
        return 2

    started_at = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
    print(f"Running {len(cases)} case(s) against {args.base}\n")

    results: list[dict[str, Any]] = []
    for case in cases:
        print(f"  … {case['name']}", flush=True)
        result = run_case(case, client, args.base, args.samples)
        results.append(result)
        print(f"    {'PASS' if result['passed'] else 'FAIL'} — {result['reason'][:120]}", flush=True)

    run = _summarise(results, args, started_at, client)

    print()
    print(run["table"])
    print()
    stats = run["category_stats"]
    print(
        "categories: "
        + "  ".join(
            f"{name} {value['passed']}/{value['passed'] + value['failed']}"
            for name, value in stats.items()
            if value["passed"] + value["failed"]
        )
    )

    flaky = [case for case in results if case.get("flaky")]
    if flaky:
        print()
        for case in flaky:
            print(
                f"FLAKY {case['name']} — passed {case['samples_passed']}/{case['samples_run']}"
                f" samples; the failing one broke on {case['flaky_reason'][:100]}"
            )

    baseline = report.load_baseline(BASELINE)
    regressions = report.find_regressions(baseline, run)
    fixed = report.find_fixed(baseline, run)
    new_cases = report.find_new_cases(baseline, run)
    run["regressions"] = regressions
    run["mode"] = "gate" if args.gate else "absolute"

    if baseline:
        if regressions:
            print(f"REGRESSED since baseline: {regressions}")
        if fixed:
            print(f"fixed since baseline: {fixed}")
        if new_cases:
            print(f"new since baseline (not gated): {new_cases}")

    report.write_last_run(LAST_RUN, run)
    if not args.no_history:
        report.append_history(HISTORY, run)
    if args.update_baseline:
        report.write_baseline(BASELINE, run)
        print(f"baseline updated: {BASELINE}")

    print(f"\nreport: {LAST_RUN}")

    if args.gate:
        return 1 if regressions else 0
    return 0 if run["passed"] == run["total"] else 1


def _summarise(
    results: list[dict[str, Any]],
    args: argparse.Namespace,
    started_at: str,
    client: ApiClient,
) -> dict[str, Any]:
    every_check = [
        asserts.Check(c["category"], c["name"], c["ok"], c["detail"])
        for case in results
        for c in case["checks"]
    ]
    turn_ttfb = [
        turn["ttfb_ms"] for case in results for turn in case["turns"] if turn.get("ttfb_ms")
    ]
    turn_total = [
        turn["total_ms"] for case in results for turn in case["turns"] if turn.get("total_ms")
    ]

    return {
        "started_at": started_at,
        "label": args.label,
        "base_url": args.base,
        "git_sha": report.git_sha(REPO_ROOT),
        "prompt_sha": report.prompt_sha(REPO_ROOT),
        "samples": args.samples or "per-case",
        "total": len(results),
        "passed": sum(1 for case in results if case["passed"]),
        "cases": results,
        "category_stats": report.category_stats(every_check),
        "latency": {
            "ttfb_p50": report.percentile(turn_ttfb, 0.50),
            "ttfb_p95": report.percentile(turn_ttfb, 0.95),
            "total_p50": report.percentile(turn_total, 0.50),
            "total_p95": report.percentile(turn_total, 0.95),
        },
        "rate_limit_waits": client.rate_limit_waits,
        "table": report.render_table(results),
    }


if __name__ == "__main__":
    raise SystemExit(main())
