#!/usr/bin/env python3
"""Offline tests for the harness itself.

    python3 tools/ai-eval/selftest.py

No network, no backend, no model. These exist because an eval suite that
silently mis-scores is worse than no eval suite: every assertion primitive is
exercised against a synthetic turn in both its passing and its failing
direction, and every committed case file is validated against the schema the
runner actually reads.
"""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from evallib import asserts, redact, report  # noqa: E402
from evallib.sse import ToolCall, Turn  # noqa: E402

CASES_DIR = HERE / "cases"

KNOWN_TOOLS = {
    "createTask", "updateTask", "completeTask", "deleteTask", "deleteAllTasks",
    "snoozeTask", "queryTasks", "addSubtask", "toggleSubtask", "removeSubtask",
    "holdForClarification",
}

STEP_KEYS = {"say", "mode", "seed", "timezone", "budgets", "trajectory", "outcome",
             "resolve_clarifications"}
TRAJECTORY_KEYS = {
    "tools_allowed", "tools_denied", "max_tool_calls", "min_tool_calls",
    "max_tool_rounds", "tool_call_counts", "expected_tool_calls",
    "expected_tool_calls_mode", "needs_confirmation", "no_unconfirmed_execution",
    "no_confirmations_pending", "all_tool_calls_resolved", "task_delta",
    "clarification_delta", "clarification_options", "tasks_matching",
    "clarifications_matching",
}
OUTCOME_KEYS = {"reply_non_empty", "must_match", "must_not_match",
                "no_question_in_prose", "has_arabic"}
FINAL_KEYS = {"tools_denied", "task_count", "clarification_count",
              "tasks_matching", "clarifications_matching"}


def make_turn(**kwargs) -> Turn:
    turn = Turn(question=kwargs.pop("question", "q"), mode=kwargs.pop("mode", "chat"))
    for key, value in kwargs.items():
        setattr(turn, key, value)
    return turn


def call(name: str, *, args=None, needs_confirmation=False, error=None,
         resolved=True, call_id="r1~0") -> ToolCall:
    return ToolCall(
        call_id=call_id,
        name=name,
        args=args or {},
        needs_confirmation=needs_confirmation,
        error=error,
        resolved=resolved,
    )


def verdict(checks, name: str) -> bool:
    matches = [c for c in checks if c.name == name or c.name.startswith(f"{name}[")]
    assert matches, f"no check named {name}: {[c.name for c in checks]}"
    return all(c.ok for c in matches)


class TrajectoryTests(unittest.TestCase):
    def test_tools_allowed_and_denied(self):
        turn = make_turn(tool_calls=[call("queryTasks"), call("deleteTask")])
        checks = asserts.check_trajectory(
            {"tools_allowed": ["queryTasks"], "tools_denied": ["deleteTask"]}, turn
        )
        self.assertFalse(verdict(checks, "tools_allowed"))
        self.assertFalse(verdict(checks, "tools_denied"))

        clean = make_turn(tool_calls=[call("queryTasks")])
        checks = asserts.check_trajectory(
            {"tools_allowed": ["queryTasks"], "tools_denied": ["deleteTask"]}, clean
        )
        self.assertTrue(verdict(checks, "tools_allowed"))
        self.assertTrue(verdict(checks, "tools_denied"))

    def test_zero_tool_calls(self):
        empty = make_turn(tool_calls=[])
        self.assertTrue(verdict(asserts.check_trajectory({"max_tool_calls": 0}, empty),
                                "max_tool_calls"))
        busy = make_turn(tool_calls=[call("addSubtask")])
        self.assertFalse(verdict(asserts.check_trajectory({"max_tool_calls": 0}, busy),
                                 "max_tool_calls"))

    def test_tool_rounds_come_from_the_callId_prefix(self):
        turn = make_turn(tool_calls=[
            call("createTask", call_id="a~0"),
            call("createTask", call_id="a~1"),
            call("updateTask", call_id="b~0"),
        ])
        self.assertEqual(turn.tool_rounds, 2)
        self.assertTrue(verdict(asserts.check_trajectory({"max_tool_rounds": 2}, turn),
                                "max_tool_rounds"))
        self.assertFalse(verdict(asserts.check_trajectory({"max_tool_rounds": 1}, turn),
                                 "max_tool_rounds"))

    def test_expected_tool_calls_catches_the_wrong_verb(self):
        """An allowlist cannot catch createTask-instead-of-updateTask. This can."""
        turn = make_turn(tool_calls=[call("createTask", args={"title": "dentist"})])
        spec = {
            "expected_tool_calls": [{"name": "updateTask", "args_subset": {"title": "dentist"}}],
            "expected_tool_calls_mode": "exact",
        }
        checks = asserts.check_trajectory(spec, turn)
        self.assertFalse(verdict(checks, "expected_tool_calls"))
        self.assertFalse(verdict(checks, "no_unexpected_tool_calls"))

    def test_expected_tool_calls_args_subset(self):
        turn = make_turn(tool_calls=[call("createTask", args={"kind": "list", "title": "bread"})])
        spec = {"expected_tool_calls": [{"name": "createTask", "args_subset": {"kind": "list"}}]}
        self.assertTrue(verdict(asserts.check_trajectory(spec, turn), "expected_tool_calls"))

        spec = {"expected_tool_calls": [{"name": "createTask", "args_subset": {"kind": "reminder"}}]}
        self.assertFalse(verdict(asserts.check_trajectory(spec, turn), "expected_tool_calls"))

    def test_expected_tool_calls_ignores_the_bearer_token(self):
        turn = make_turn(tool_calls=[call("createTask", args={"access_token": "eyJ.a.b", "kind": "list"})])
        spec = {
            "expected_tool_calls": [{"name": "createTask", "args_subset": {"kind": "list"}}],
            "expected_tool_calls_mode": "exact",
        }
        checks = asserts.check_trajectory(spec, turn)
        self.assertTrue(verdict(checks, "expected_tool_calls"))
        self.assertTrue(verdict(checks, "no_unexpected_tool_calls"))

    def test_args_present_comparator(self):
        turn = make_turn(tool_calls=[call("holdForClarification", args={"options": "[]"})])
        spec = {"expected_tool_calls": [
            {"name": "holdForClarification", "args_subset": {"options": {"$present": True}}}]}
        self.assertTrue(verdict(asserts.check_trajectory(spec, turn), "expected_tool_calls"))

        bare = make_turn(tool_calls=[call("holdForClarification", args={})])
        self.assertFalse(verdict(asserts.check_trajectory(spec, bare), "expected_tool_calls"))

    def test_task_delta_uses_real_state(self):
        turn = make_turn(
            tasks_before=[{"id": "1", "title": "old"}],
            tasks_after=[{"id": "1", "title": "old"}, {"id": "2", "title": "new"}],
        )
        self.assertEqual(turn.task_delta, 1)
        self.assertTrue(verdict(asserts.check_trajectory({"task_delta": 1}, turn), "task_delta"))
        self.assertFalse(verdict(asserts.check_trajectory({"task_delta": 0}, turn), "task_delta"))
        self.assertEqual([t["id"] for t in turn.new_tasks()], ["2"])

    def test_clarification_options_need_dates(self):
        turn = make_turn(clarifications_after=[{
            "id": "c1", "question": "When?",
            "options": [{"label": "9am", "dueAt": "2026-08-17T06:00:00Z"}, {"label": "later"}],
        }])
        spec = {"clarification_options": {"min": 2, "max": 4, "each_has_due_at": True}}
        checks = asserts.check_trajectory(spec, turn)
        self.assertTrue(verdict(checks, "clarification_options"))
        self.assertFalse(verdict(checks, "clarification_options_dueAt"))

    def test_clarification_options_absent_is_a_failure(self):
        turn = make_turn()
        spec = {"clarification_options": {"min": 2, "max": 4}}
        self.assertFalse(verdict(asserts.check_trajectory(spec, turn), "clarification_options"))

    def test_needs_confirmation(self):
        gated = make_turn(tool_calls=[call("deleteAllTasks", needs_confirmation=True)])
        spec = {"needs_confirmation": {"deleteAllTasks": True}}
        self.assertTrue(verdict(asserts.check_trajectory(spec, gated), "needs_confirmation"))

        ungated = make_turn(tool_calls=[call("deleteAllTasks", needs_confirmation=False)])
        self.assertFalse(verdict(asserts.check_trajectory(spec, ungated), "needs_confirmation"))

        absent = make_turn(tool_calls=[])
        self.assertFalse(verdict(asserts.check_trajectory(spec, absent), "needs_confirmation"))

    def test_no_confirmations_pending(self):
        turn = make_turn(tool_calls=[call("deleteAllTasks", needs_confirmation=True)])
        spec = {"no_confirmations_pending": True}
        self.assertFalse(verdict(asserts.check_trajectory(spec, turn), "no_confirmations_pending"))

    def test_dangling_tool_call(self):
        turn = make_turn(tool_calls=[call("createTask", resolved=False)])
        spec = {"all_tool_calls_resolved": True}
        self.assertFalse(verdict(asserts.check_trajectory(spec, turn), "all_tool_calls_resolved"))

    def test_error_frames_always_fail(self):
        turn = make_turn(error_frames=[{"type": "error", "code": "boom"}])
        self.assertFalse(verdict(asserts.check_trajectory({}, turn), "no_error_frames"))
        self.assertTrue(verdict(asserts.check_trajectory({}, make_turn()), "no_error_frames"))

    def test_transport_error_fails(self):
        turn = make_turn(transport_error="timeout")
        self.assertFalse(verdict(asserts.check_trajectory({}, turn), "no_error_frames"))

    def test_a_turn_that_neither_speaks_nor_acts_fails(self):
        """Observed 2026-08-16: 'Call the bank about the loan on Friday' returned
        no reply, no tool call and no error frame after 219.9 seconds."""
        silent = make_turn(reply="", tool_calls=[], total_ms=219920.0)
        self.assertFalse(verdict(asserts.check_trajectory({}, silent), "turn_not_silent"))

    def test_acting_without_speaking_is_not_silent(self):
        turn = make_turn(reply="", tool_calls=[call("createTask")])
        self.assertTrue(verdict(asserts.check_trajectory({}, turn), "turn_not_silent"))

    def test_speaking_without_acting_is_not_silent(self):
        turn = make_turn(reply="You have one thing on your list.", tool_calls=[])
        self.assertTrue(verdict(asserts.check_trajectory({}, turn), "turn_not_silent"))

    def test_seed_turns_get_the_always_on_invariants(self):
        """A seed is setup, but a silent seed builds the wrong state and reports
        as a confusing mismatch several turns later."""
        silent = make_turn(reply="", tool_calls=[], total_ms=219920.0)
        seed_checks = asserts.check_trajectory({}, silent) + asserts.check_latency({}, silent)
        names = {c.name for c in seed_checks}
        self.assertIn("turn_not_silent", names)
        self.assertIn("no_error_frames", names)
        self.assertIn("total_ms", names)
        self.assertFalse(all(c.ok for c in seed_checks))

    def test_tasks_matching_counts_duplicates(self):
        turn = make_turn(tasks_after=[
            {"id": "1", "title": "Pay rent"}, {"id": "2", "title": "Pay the rent"},
        ])
        spec = {"tasks_matching": [{"pattern": "rent", "min": 1, "max": 1}]}
        self.assertFalse(verdict(asserts.check_trajectory(spec, turn), "tasks_matching"))

    def test_held_requires_a_successful_hold(self):
        failed = make_turn(tool_calls=[call("holdForClarification", error="boom")])
        self.assertFalse(failed.held)
        good = make_turn(tool_calls=[call("holdForClarification")])
        self.assertTrue(good.held)


class OutcomeTests(unittest.TestCase):
    def test_question_in_prose_without_a_hold_is_interrogation(self):
        turn = make_turn(reply="Who's the doctor, and what's the visit for?")
        checks = asserts.check_outcome({"no_question_in_prose": {}}, turn)
        self.assertFalse(verdict(checks, "no_question_in_prose"))

    def test_one_short_lead_in_is_allowed_when_a_hold_was_created(self):
        turn = make_turn(
            reply="Filed. What time is your appointment on Monday?",
            tool_calls=[call("holdForClarification")],
        )
        checks = asserts.check_outcome(
            {"no_question_in_prose": {"max_with_hold": 1, "lead_in_max_chars": 90}}, turn
        )
        self.assertTrue(verdict(checks, "no_question_in_prose"))

    def test_a_long_lead_in_is_not_a_lead_in(self):
        turn = make_turn(
            reply="Filed. " + "Which of these would you prefer for the appointment " * 3 + "?",
            tool_calls=[call("holdForClarification")],
        )
        checks = asserts.check_outcome(
            {"no_question_in_prose": {"max_with_hold": 1, "lead_in_max_chars": 90}}, turn
        )
        self.assertFalse(verdict(checks, "no_question_in_prose"))

    def test_two_questions_fail_even_with_a_hold(self):
        turn = make_turn(reply="What time? And who is it with?",
                         tool_calls=[call("holdForClarification")])
        checks = asserts.check_outcome({"no_question_in_prose": {"max_with_hold": 1}}, turn)
        self.assertFalse(verdict(checks, "no_question_in_prose"))

    def test_arabic_question_mark_counts(self):
        turn = make_turn(reply="تم الحفظ. ما الوقت المناسب؟")
        checks = asserts.check_outcome({"no_question_in_prose": {"max_without_hold": 0}}, turn)
        self.assertFalse(verdict(checks, "no_question_in_prose"))

    def test_has_arabic(self):
        self.assertTrue(verdict(
            asserts.check_outcome({"has_arabic": True}, make_turn(reply="تم حفظ الموعد.")),
            "has_arabic"))
        self.assertFalse(verdict(
            asserts.check_outcome({"has_arabic": True}, make_turn(reply="Filed it.")),
            "has_arabic"))

    def test_reply_non_empty(self):
        self.assertFalse(verdict(
            asserts.check_outcome({"reply_non_empty": True}, make_turn(reply="   ")),
            "reply_non_empty"))

    def test_must_and_must_not_match(self):
        turn = make_turn(reply="Your bank call is on Friday.")
        checks = asserts.check_outcome(
            {"must_match": ["bank"], "must_not_match": ["deleted"]}, turn)
        self.assertTrue(all(c.ok for c in checks))


class LatencyTests(unittest.TestCase):
    def test_budgets(self):
        fast = make_turn(ttfb_ms=90.0, total_ms=12000.0)
        self.assertTrue(all(c.ok for c in asserts.check_latency({}, fast)))
        slow = make_turn(ttfb_ms=9000.0, total_ms=60000.0)
        self.assertFalse(any(c.ok for c in asserts.check_latency({}, slow)))

    def test_missing_timing_is_a_failure_not_a_crash(self):
        checks = asserts.check_latency({}, make_turn())
        self.assertFalse(any(c.ok for c in checks))


class FinalTests(unittest.TestCase):
    def test_denied_tool_across_every_turn_including_seeds(self):
        turns = [
            make_turn(tool_calls=[call("createTask")]),
            make_turn(tool_calls=[call("deleteTask")]),
        ]
        checks = asserts.check_final({"tools_denied": ["deleteTask"]}, turns, [], [])
        self.assertFalse(verdict(checks, "final.tools_denied"))

    def test_task_count(self):
        checks = asserts.check_final({"task_count": {"min": 2, "max": 2}}, [],
                                     [{"title": "a"}, {"title": "b"}], [])
        self.assertTrue(verdict(checks, "final.task_count"))


class RedactionTests(unittest.TestCase):
    def test_bearer_token_never_survives(self):
        payload = {
            "args": {
                "access_token": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig",
                "title": "Go to the doctor",
            },
            "note": "token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig inline",
        }
        cleaned = redact.redact(payload)
        blob = json.dumps(cleaned)
        self.assertNotIn("eyJhbGciOiJIUzI1NiJ9", blob)
        self.assertIn("Go to the doctor", blob)

    def test_input_is_not_mutated(self):
        original = {"access_token": "secret"}
        redact.redact(original)
        self.assertEqual(original["access_token"], "secret")


class GateTests(unittest.TestCase):
    def test_regression_is_a_pass_that_became_a_fail(self):
        baseline = {"cases": {"a": {"passed": True}, "b": {"passed": False}}}
        run = {"cases": [
            {"name": "a", "passed": False},
            {"name": "b", "passed": True},
            {"name": "c", "passed": False},
        ]}
        self.assertEqual(report.find_regressions(baseline, run), ["a"])
        self.assertEqual(report.find_fixed(baseline, run), ["b"])
        self.assertEqual(report.find_new_cases(baseline, run), ["c"])


class CaseFileTests(unittest.TestCase):
    """Every committed case must be readable by the runner, not just by JSON."""

    def setUp(self):
        self.cases = [json.loads(p.read_text(encoding="utf-8"))
                      for p in sorted(CASES_DIR.glob("*.json"))]

    def test_there_are_cases(self):
        self.assertGreaterEqual(len(self.cases), 12)

    def test_names_are_unique_and_sourced(self):
        names = [case["name"] for case in self.cases]
        self.assertEqual(len(names), len(set(names)))
        for case in self.cases:
            self.assertTrue(case.get("source"), f"{case['name']} has no source")
            self.assertTrue(case.get("description"), f"{case['name']} has no description")

    def test_schema_keys_are_known(self):
        for case in self.cases:
            for step in case["steps"]:
                unknown = set(step) - STEP_KEYS
                self.assertFalse(unknown, f"{case['name']}: unknown step keys {unknown}")
                self.assertTrue(
                    "say" in step or "resolve_clarifications" in step,
                    f"{case['name']}: a step must either say something or resolve something",
                )
                unknown = set(step.get("trajectory") or {}) - TRAJECTORY_KEYS
                self.assertFalse(unknown, f"{case['name']}: unknown trajectory keys {unknown}")
                unknown = set(step.get("outcome") or {}) - OUTCOME_KEYS
                self.assertFalse(unknown, f"{case['name']}: unknown outcome keys {unknown}")
            unknown = set(case.get("final") or {}) - FINAL_KEYS
            self.assertFalse(unknown, f"{case['name']}: unknown final keys {unknown}")

    def test_every_named_tool_exists_in_the_contract(self):
        for case in self.cases:
            for step in case["steps"]:
                traj = step.get("trajectory") or {}
                named = set(traj.get("tools_allowed") or []) | set(traj.get("tools_denied") or [])
                named |= set(traj.get("tool_call_counts") or {})
                named |= set(traj.get("needs_confirmation") or {})
                named |= {c["name"] for c in traj.get("expected_tool_calls") or []}
                unknown = named - KNOWN_TOOLS
                self.assertFalse(unknown, f"{case['name']}: unknown tool(s) {unknown}")
            named = set((case.get("final") or {}).get("tools_denied") or [])
            self.assertFalse(named - KNOWN_TOOLS, f"{case['name']}: unknown tool in final")

    def test_every_regex_compiles(self):
        import re
        for case in self.cases:
            for step in case["steps"]:
                outcome = step.get("outcome") or {}
                for pattern in list(outcome.get("must_match") or []) + list(
                    outcome.get("must_not_match") or []
                ):
                    re.compile(pattern)
                for rule in (step.get("trajectory") or {}).get("tasks_matching") or []:
                    re.compile(rule["pattern"])

    def test_graded_steps_assert_something(self):
        for case in self.cases:
            for step in case["steps"]:
                if step.get("seed") or "resolve_clarifications" in step:
                    continue
                self.assertTrue(
                    step.get("trajectory") or step.get("outcome"),
                    f"{case['name']}: a graded step with no assertions is dead weight",
                )


class TableTests(unittest.TestCase):
    def row(self, **kwargs):
        base = {
            "name": "demo", "passed": False, "reason": "tools_denied: deleteTask fired",
            "ttfb_ms": 88.0, "first_token_ms": 4100.0, "total_ms": 21000.0, "tool_rounds": 2,
        }
        base.update(kwargs)
        return base

    def test_renders_without_a_backend(self):
        table = report.render_table([self.row()])
        self.assertIn("demo", table)
        self.assertIn("FAIL", table)

    def test_a_majority_pass_with_a_broken_sample_reads_as_flaky(self):
        """A bare PASS here would bury a real intermittent defect."""
        table = report.render_table([self.row(passed=True, flaky=True, reason="passed 2/3")])
        self.assertIn("FLAKY", table)
        self.assertNotIn("PASS", table)

    def test_a_clean_pass_still_reads_as_pass(self):
        table = report.render_table([self.row(passed=True, flaky=False, reason="ok")])
        self.assertIn("PASS", table)
        self.assertNotIn("FLAKY", table)


if __name__ == "__main__":
    unittest.main(verbosity=2)
