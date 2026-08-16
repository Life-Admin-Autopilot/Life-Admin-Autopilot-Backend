"""One chat turn: POST /ai/ask, read the SSE stream, time it, record it.

Frame vocabulary (AiStreamEvents.cs):
    conversation -> sources -> (tool_call | tool_result)* -> token* -> done -> quota

`tool_call.callId` is `"<roundId>~<index>"`. That prefix is the only reliable
signal for how many *rounds* of tool use the model took, because results stream
back interleaved with tokens and cannot be grouped positionally.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Any

from .redact import redact


@dataclass
class ToolCall:
    call_id: str
    name: str
    args: dict[str, Any]
    needs_confirmation: bool
    result: Any = None
    error: str | None = None
    resolved: bool = False

    @property
    def round_id(self) -> str:
        return self.call_id.split("~", 1)[0]

    @property
    def succeeded(self) -> bool:
        return self.resolved and self.error is None

    def to_json(self) -> dict[str, Any]:
        return redact(
            {
                "callId": self.call_id,
                "name": self.name,
                "args": self.args,
                "needsConfirmation": self.needs_confirmation,
                "result": self.result,
                "error": self.error,
            }
        )


@dataclass
class Turn:
    """Everything one `/ai/ask` produced, plus the state around it."""

    question: str
    mode: str
    conversation_id: str | None = None
    reply: str = ""
    tool_calls: list[ToolCall] = field(default_factory=list)
    error_frames: list[dict[str, Any]] = field(default_factory=list)
    frames: list[dict[str, Any]] = field(default_factory=list)

    headers_ms: float | None = None
    ttfb_ms: float | None = None
    first_token_ms: float | None = None
    total_ms: float | None = None

    transport_error: str | None = None

    # state snapshots, filled by the runner
    tasks_before: list[dict[str, Any]] = field(default_factory=list)
    tasks_after: list[dict[str, Any]] = field(default_factory=list)
    clarifications_before: list[dict[str, Any]] = field(default_factory=list)
    clarifications_after: list[dict[str, Any]] = field(default_factory=list)

    @property
    def tool_rounds(self) -> int:
        return len({call.round_id for call in self.tool_calls})

    @property
    def tool_names(self) -> list[str]:
        return [call.name for call in self.tool_calls]

    @property
    def held(self) -> bool:
        """A clarification was actually written this turn."""
        return any(
            call.name == "holdForClarification" and call.succeeded
            for call in self.tool_calls
        )

    @property
    def task_delta(self) -> int:
        return len(self.tasks_after) - len(self.tasks_before)

    @property
    def clarification_delta(self) -> int:
        return len(self.clarifications_after) - len(self.clarifications_before)

    def new_clarifications(self) -> list[dict[str, Any]]:
        seen = {c.get("id") for c in self.clarifications_before}
        return [c for c in self.clarifications_after if c.get("id") not in seen]

    def new_tasks(self) -> list[dict[str, Any]]:
        seen = {t.get("id") for t in self.tasks_before}
        return [t for t in self.tasks_after if t.get("id") not in seen]

    def tool_sequence(self) -> str:
        """Human-readable trace for the failure report."""
        return " -> ".join(
            f"{call.name}"
            f"{'!' if call.needs_confirmation else ''}"
            f"{'[err]' if call.error else ''}"
            for call in self.tool_calls
        ) or "(no tool calls)"


def ask(
    base_url: str,
    token: str,
    question: str,
    *,
    timezone: str,
    mode: str = "chat",
    conversation_id: str | None = None,
    timeout_s: float = 180.0,
) -> Turn:
    """Run one turn and return the full record. Never raises on model behaviour."""
    turn = Turn(question=question, mode=mode)

    body: dict[str, Any] = {"question": question, "timezone": timezone}
    if mode and mode != "chat":
        body["mode"] = mode
    if conversation_id:
        body["conversationId"] = conversation_id

    req = urllib.request.Request(
        f"{base_url.rstrip('/')}/ai/ask",
        data=json.dumps(body).encode("utf-8"),
        method="POST",
    )
    req.add_header("Content-Type", "application/json")
    req.add_header("Accept", "text/event-stream")
    req.add_header("Authorization", f"Bearer {token}")

    started = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=timeout_s) as response:
            turn.headers_ms = (time.monotonic() - started) * 1000.0
            _consume(response, turn, started)
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", "replace")[:400]
        turn.transport_error = f"HTTP {exc.code}: {detail}"
    except Exception as exc:  # timeouts, resets — a real failure, recorded not raised
        turn.transport_error = f"{type(exc).__name__}: {exc}"

    if turn.total_ms is None:
        turn.total_ms = (time.monotonic() - started) * 1000.0
    return turn


def _consume(response: Any, turn: Turn, started: float) -> None:
    pending: dict[str, ToolCall] = {}

    for raw in response:
        line = raw.decode("utf-8", "replace").strip()
        if not line.startswith("data:"):
            continue

        now = (time.monotonic() - started) * 1000.0
        if turn.ttfb_ms is None:
            turn.ttfb_ms = now

        try:
            frame = json.loads(line[5:].strip())
        except json.JSONDecodeError:
            turn.error_frames.append({"type": "malformed_frame", "raw": line[:200]})
            continue

        turn.frames.append(redact(frame))
        kind = frame.get("type")

        if kind == "conversation":
            turn.conversation_id = frame.get("conversationId")
        elif kind == "token":
            if turn.first_token_ms is None:
                turn.first_token_ms = now
            turn.reply += frame.get("text") or ""
        elif kind == "tool_call":
            call = ToolCall(
                call_id=frame.get("callId") or "",
                name=frame.get("name") or "",
                args=frame.get("args") or {},
                needs_confirmation=bool(frame.get("needsConfirmation")),
            )
            turn.tool_calls.append(call)
            pending[call.call_id] = call
        elif kind == "tool_result":
            call = pending.get(frame.get("callId") or "")
            if call is not None:
                call.resolved = True
                call.result = frame.get("result")
                error = frame.get("error")
                call.error = None if error is None else str(error)
        elif kind == "error":
            turn.error_frames.append(frame)
        elif kind == "done":
            turn.total_ms = now

    if turn.total_ms is None:
        turn.total_ms = (time.monotonic() - started) * 1000.0
