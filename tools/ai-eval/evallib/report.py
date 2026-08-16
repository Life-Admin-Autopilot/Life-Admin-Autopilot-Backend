"""Console table, last-run.md, history.jsonl, and the baseline gate."""

from __future__ import annotations

import json
import subprocess
import time
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from .asserts import LATENCY, OUTCOME, TRAJECTORY, Check
from .redact import redact_text

COLUMNS = ("case", "result", "reason", "TTFB", "first-tok", "total", "rounds")


# ---------------------------------------------------------------------------
# console
# ---------------------------------------------------------------------------


def render_table(rows: Sequence[Mapping[str, Any]], reason_width: int = 44) -> str:
    body = [
        {
            "case": str(row["name"])[:34],
            "result": (
                "FAIL" if not row["passed"] else ("FLAKY" if row.get("flaky") else "PASS")
            ),
            "reason": _clip(row["reason"], reason_width),
            "TTFB": _ms(row["ttfb_ms"]),
            "first-tok": _ms(row["first_token_ms"]),
            "total": _ms(row["total_ms"]),
            "rounds": str(row["tool_rounds"]),
        }
        for row in rows
    ]
    widths = {
        col: max(len(col), *(len(item[col]) for item in body)) if body else len(col)
        for col in COLUMNS
    }
    sep = "-+-".join("-" * widths[col] for col in COLUMNS)
    lines = [
        " | ".join(col.ljust(widths[col]) for col in COLUMNS),
        sep,
    ]
    lines.extend(
        " | ".join(item[col].ljust(widths[col]) for col in COLUMNS) for item in body
    )
    return "\n".join(lines)


def _clip(text: str, width: int) -> str:
    text = " ".join((text or "").split())
    return text if len(text) <= width else text[: width - 1] + "…"


def _ms(value: float | None) -> str:
    return "n/a" if value is None else f"{value / 1000:.2f}s"


# ---------------------------------------------------------------------------
# aggregation
# ---------------------------------------------------------------------------


def category_stats(checks: Iterable[Check]) -> dict[str, dict[str, int]]:
    stats: dict[str, dict[str, int]] = {
        category: {"passed": 0, "failed": 0}
        for category in (TRAJECTORY, OUTCOME, LATENCY)
    }
    for check in checks:
        bucket = stats.setdefault(check.category, {"passed": 0, "failed": 0})
        bucket["passed" if check.ok else "failed"] += 1
    return stats


def percentile(values: Sequence[float], fraction: float) -> float | None:
    clean = sorted(v for v in values if v is not None)
    if not clean:
        return None
    index = min(len(clean) - 1, max(0, int(round(fraction * (len(clean) - 1)))))
    return clean[index]


def git_sha(repo_root: Path) -> str:
    return _git(repo_root, ["rev-parse", "--short", "HEAD"])


def prompt_sha(repo_root: Path) -> str:
    """The commit that last touched `langflow/` — i.e. which prompt was live.

    Scores are only comparable within one prompt version, so history rows carry
    it. `langflow/` is owned by another agent; this reads it, never writes it.
    """
    return _git(repo_root, ["log", "-1", "--format=%h", "--", "langflow"])


def _git(repo_root: Path, args: list[str]) -> str:
    try:
        return subprocess.run(
            ["git", "-C", str(repo_root), *args],
            capture_output=True,
            text=True,
            timeout=10,
            check=True,
        ).stdout.strip() or "unknown"
    except Exception:
        return "unknown"


# ---------------------------------------------------------------------------
# last-run.md
# ---------------------------------------------------------------------------


def write_last_run(path: Path, run: Mapping[str, Any]) -> None:
    out: list[str] = []
    add = out.append

    add("# AI behaviour eval — last run")
    add("")
    add(f"- **Label** — {run.get('label') or 'unlabelled run'}")
    add(f"- **When** — {run['started_at']}")
    add(f"- **Backend** — `{run['base_url']}` at commit `{run['git_sha']}`")
    add(f"- **Prompt** — `langflow/` at commit `{run['prompt_sha']}`")
    add(f"- **Result** — {run['passed']}/{run['total']} cases passed")
    add(f"- **Samples per case** — {run['samples']}")
    stats = run["category_stats"]
    add(
        "- **By category** — "
        + ", ".join(
            f"{name} {value['passed']}/{value['passed'] + value['failed']}"
            for name, value in stats.items()
            if value["passed"] + value["failed"]
        )
    )
    latency = run["latency"]
    add(
        f"- **Latency** — TTFB p50 {_ms(latency['ttfb_p50'])} / p95 {_ms(latency['ttfb_p95'])}"
        f" · turn total p50 {_ms(latency['total_p50'])} / p95 {_ms(latency['total_p95'])}"
    )
    if run.get("rate_limit_waits"):
        add(f"- **Throttled** — waited out {len(run['rate_limit_waits'])} rate-limit response(s)")
    add("")
    add("```")
    add(run["table"])
    add("```")
    add("")

    failing = [case for case in run["cases"] if not case["passed"]]
    if failing:
        add("## Failures")
        add("")
        for case in failing:
            out.extend(_case_section(case, failures_only=True))
    else:
        add("## Failures")
        add("")
        add("None.")
        add("")

    flaky = [case for case in run["cases"] if case.get("flaky")]
    add("## Flaky — passed by majority, but a sample failed")
    add("")
    if flaky:
        add(
            "These count as passes and do not fail the run, but a sample genuinely "
            "broke. An intermittent defect is still a defect; do not read the PASS "
            "and move on."
        )
        add("")
        for case in flaky:
            add(
                f"### {case['name']} — {case['samples_passed']}/{case['samples_run']} samples passed"
            )
            add("")
            add(f"- failing sample's first broken check: {case['flaky_reason']}")
            add("")
            for index, turn in enumerate(case.get("flaky_turns", []), start=1):
                add(
                    f"- turn {index}{' (seed)' if turn['seed'] else ''}: "
                    f"`{turn['tool_sequence']}` · task delta {turn['task_delta']:+d}"
                    f" · total {_ms(turn['total_ms'])}"
                    f" · reply {turn['reply'][:80]!r}"
                )
            add("")
            for check in case.get("flaky_checks", []):
                if not check["ok"]:
                    add(f"- **FAIL** `{check['category']}` `{check['name']}` — {check['detail']}")
            add("")
    else:
        add("None — every sample of every case agreed with its case verdict.")
        add("")

    add("## Every case, in full")
    add("")
    for case in run["cases"]:
        out.extend(_case_section(case, failures_only=False))

    path.write_text(redact_text("\n".join(out)) + "\n", encoding="utf-8")


def _case_section(case: Mapping[str, Any], *, failures_only: bool) -> list[str]:
    out: list[str] = []
    add = out.append
    verdict = "PASS" if case["passed"] else "FAIL"
    add(f"### {case['name']} — {verdict}")
    add("")
    add(f"- source: `{case.get('source') or 'n/a'}`")
    if case.get("description"):
        add(f"- {case['description']}")
    add(
        f"- samples: {case['samples_passed']}/{case['samples_run']} passed"
        f" · tool rounds: {case['tool_rounds']}"
        f" · TTFB {_ms(case['ttfb_ms'])} · first token {_ms(case['first_token_ms'])}"
        f" · total {_ms(case['total_ms'])}"
    )
    if case.get("error"):
        add(f"- **harness error**: {case['error']}")
    add("")

    for index, turn in enumerate(case.get("turns", []), start=1):
        add(f"**Turn {index}{' (seed)' if turn['seed'] else ''}** — mode `{turn['mode']}`")
        add("")
        add(f"> user: {turn['question']}")
        add("")
        add(f"- tools: `{turn['tool_sequence']}`")
        add(f"- rounds: {turn['tool_rounds']} · task delta: {turn['task_delta']:+d}"
            f" · clarification delta: {turn['clarification_delta']:+d}")
        add(f"- TTFB {_ms(turn['ttfb_ms'])} · first token {_ms(turn['first_token_ms'])}"
            f" · total {_ms(turn['total_ms'])}")
        add(f"- reply: {turn['reply']!r}" if turn["reply"] else "- reply: *(empty)*")
        if turn.get("transport_error"):
            add(f"- **transport error**: {turn['transport_error']}")
        if turn.get("error_frames"):
            add(f"- **error frames**: `{json.dumps(turn['error_frames'], ensure_ascii=False)[:500]}`")
        if turn.get("tool_calls"):
            add("")
            add("```json")
            add(json.dumps(turn["tool_calls"], indent=2, ensure_ascii=False)[:6000])
            add("```")
        add("")

    checks = case.get("checks", [])
    shown = [c for c in checks if not c["ok"]] if failures_only else checks
    if shown:
        add("Checks:")
        add("")
        for check in shown:
            mark = "PASS" if check["ok"] else "**FAIL**"
            add(f"- {mark} `{check['category']}` `{check['name']}` — {check['detail']}")
        add("")
    return out


# ---------------------------------------------------------------------------
# history + baseline
# ---------------------------------------------------------------------------


def append_history(path: Path, run: Mapping[str, Any]) -> None:
    entry = {
        "ts": run["started_at"],
        "label": run.get("label"),
        "git_sha": run["git_sha"],
        "prompt_sha": run["prompt_sha"],
        "base_url": run["base_url"],
        "samples": run["samples"],
        "total": run["total"],
        "passed": run["passed"],
        "failed": run["total"] - run["passed"],
        "category_stats": run["category_stats"],
        "latency": run["latency"],
        "cases": {case["name"]: case["passed"] for case in run["cases"]},
        "flaky": [case["name"] for case in run["cases"] if case.get("flaky")],
        "mode": run.get("mode", "absolute"),
        "regressions": run.get("regressions", []),
    }
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry, ensure_ascii=False) + "\n")


def load_baseline(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def write_baseline(path: Path, run: Mapping[str, Any]) -> None:
    payload = {
        "generated_at": run["started_at"],
        "label": run.get("label"),
        "git_sha": run["git_sha"],
        "prompt_sha": run["prompt_sha"],
        "samples": run["samples"],
        "note": (
            "Last-known state per case. `--gate` fails only on a case that passed "
            "here and fails now. Regenerate with `python3 runner.py --update-baseline` "
            "once a fix has genuinely landed."
        ),
        "cases": {
            case["name"]: {
                "passed": case["passed"],
                "flaky": bool(case.get("flaky")),
                "samples": f"{case['samples_passed']}/{case['samples_run']}",
                "failed_checks": [c["name"] for c in case["checks"] if not c["ok"]],
            }
            for case in run["cases"]
        },
    }
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def find_regressions(baseline: Mapping[str, Any], run: Mapping[str, Any]) -> list[str]:
    known = baseline.get("cases") or {}
    return [
        case["name"]
        for case in run["cases"]
        if known.get(case["name"], {}).get("passed") and not case["passed"]
    ]


def find_new_cases(baseline: Mapping[str, Any], run: Mapping[str, Any]) -> list[str]:
    known = baseline.get("cases") or {}
    return [case["name"] for case in run["cases"] if case["name"] not in known]


def find_fixed(baseline: Mapping[str, Any], run: Mapping[str, Any]) -> list[str]:
    known = baseline.get("cases") or {}
    return [
        case["name"]
        for case in run["cases"]
        if case["name"] in known
        and not known[case["name"]].get("passed")
        and case["passed"]
    ]
