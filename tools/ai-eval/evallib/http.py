"""Thin stdlib HTTP client for the Steward API.

No third-party packages, by design: this suite is the product's permanent AI
regression gate and must run on a bare `python3` forever.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from typing import Any, Mapping

DEFAULT_BASE_URL = "http://localhost:5080"

#: `authLimiter` is 15 min / 20, keyed on the socket IP (KernelRateLimiters.cs).
#: A full 12-case suite spends 12 of those, so back-to-back runs WILL throttle.
#: The runner waits it out rather than reporting a fake behavioural failure.
DEFAULT_MAX_RATE_LIMIT_WAIT_S = 240.0


class ApiError(RuntimeError):
    """A non-2xx response from the backend, with the body attached."""

    def __init__(self, status: int, url: str, body: str) -> None:
        super().__init__(f"{status} from {url}: {body[:400]}")
        self.status = status
        self.url = url
        self.body = body


@dataclass(frozen=True)
class Response:
    status: int
    headers: Mapping[str, str]
    body: Any


@dataclass
class ApiClient:
    """Stateless-per-call JSON client. One instance per suite run."""

    base_url: str = DEFAULT_BASE_URL
    timeout_s: float = 90.0
    max_rate_limit_wait_s: float = DEFAULT_MAX_RATE_LIMIT_WAIT_S
    #: Every throttle the run absorbed, for the report.
    rate_limit_waits: list[dict[str, Any]] = field(default_factory=list)

    # -- low level ---------------------------------------------------------

    def request(
        self,
        method: str,
        path: str,
        *,
        body: Any = None,
        token: str | None = None,
        retries: int = 3,
    ) -> Response:
        url = f"{self.base_url.rstrip('/')}{path}"
        payload = None if body is None else json.dumps(body).encode("utf-8")

        attempt = 0
        while True:
            attempt += 1
            req = urllib.request.Request(url, data=payload, method=method)
            req.add_header("Accept", "application/json")
            if payload is not None:
                req.add_header("Content-Type", "application/json")
            if token:
                req.add_header("Authorization", f"Bearer {token}")

            try:
                with urllib.request.urlopen(req, timeout=self.timeout_s) as resp:
                    raw = resp.read().decode("utf-8")
                    return Response(
                        status=resp.status,
                        headers=dict(resp.headers.items()),
                        body=json.loads(raw) if raw.strip() else None,
                    )
            except urllib.error.HTTPError as exc:
                raw = exc.read().decode("utf-8", "replace")
                if exc.code == 429 and attempt <= retries:
                    waited = self._wait_for_limiter(exc.headers, url)
                    if waited is not None:
                        continue
                raise ApiError(exc.code, url, raw) from exc

    def _wait_for_limiter(self, headers: Mapping[str, str], url: str) -> float | None:
        """Sleep off a 429 when the reset is close enough. ``None`` = give up."""
        reset = headers.get("Retry-After") or headers.get("RateLimit-Reset") or "0"
        try:
            seconds = float(reset)
        except ValueError:
            seconds = 5.0
        seconds = max(seconds, 1.0) + 1.0
        if seconds > self.max_rate_limit_wait_s:
            return None
        self.rate_limit_waits.append({"url": url, "seconds": seconds})
        time.sleep(seconds)
        return seconds

    # -- convenience -------------------------------------------------------

    def get(self, path: str, *, token: str | None = None) -> Any:
        return self.request("GET", path, token=token).body

    def post(self, path: str, body: Any, *, token: str | None = None) -> Any:
        return self.request("POST", path, body=body, token=token).body

    # -- domain helpers ----------------------------------------------------

    def signup(self, *, label: str = "eval") -> dict[str, Any]:
        """Provision a throwaway user. Fresh per case AND per sample.

        Isolation is not a nicety here: the flow is grounded on a rendered
        MY TASKS block, so one case's leftovers become another case's context.
        """
        email = f"aieval+{label}-{uuid.uuid4().hex[:12]}@example.com"
        payload = self.post(
            "/auth/signup",
            {"email": email, "password": "AiEvalPass123!", "name": "AI Eval"},
        )
        return {
            "email": email,
            "userId": (payload or {}).get("user", {}).get("id"),
            "token": (payload or {}).get("tokens", {}).get("accessToken"),
        }

    def tasks(self, token: str) -> list[dict[str, Any]]:
        payload = self.get("/me/tasks", token=token) or {}
        return list(payload.get("tasks") or [])

    def clarifications(self, token: str) -> list[dict[str, Any]]:
        payload = self.get("/me/clarifications", token=token) or {}
        return list(payload.get("clarifications") or [])

    def notifications(self, token: str) -> list[dict[str, Any]]:
        payload = self.get("/me/notifications", token=token) or {}
        return list(payload.get("notifications") or [])

    def resolve_clarification(
        self,
        token: str,
        clarification_id: str,
        option: int,
        timezone: str | None = None,
    ) -> Any:
        """Tap an answer chip.

        The answer is NESTED under `answer` — `ResolveBodySchema` is
        `z.object({answer: z.discriminatedUnion('type', …), timezone})`. This used
        to post the union bare, which is a 400 every time; the caller swallowed it,
        so every `resolve_clarifications` step in every case was a silent no-op and
        the state it was there to build never existed.
        """
        body: dict[str, Any] = {"answer": {"type": "option", "index": option}}
        if timezone:
            body["timezone"] = timezone
        return self.post(
            f"/me/clarifications/{clarification_id}/resolve", body, token=token
        )

    def health(self) -> bool:
        try:
            return self.request("GET", "/health", retries=0).status == 200
        except Exception:
            return False
