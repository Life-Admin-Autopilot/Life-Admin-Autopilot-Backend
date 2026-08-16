"""Secret scrubbing.

Every tool call the planning agent makes carries the caller's live JWT in
``args.access_token`` (the flow's ``access_token`` field is ``tool_mode: true``,
see PLANNING-AGENT.md §6). That token reaches this harness on the wire and would
otherwise be written verbatim into ``last-run.md`` -- a committed file. Nothing
leaves this process un-scrubbed.
"""

from __future__ import annotations

import re
from typing import Any

#: A three-segment JWT. `eyJ` is base64 for `{"` and anchors this well enough to
#: leave the signature length unbounded — a short or absent signature is still a
#: token, and a redactor that only catches well-formed ones is not a redactor.
JWT_RE = re.compile(r"eyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]*")

#: Keys whose value is a credential regardless of what it looks like.
SECRET_KEYS = frozenset(
    {
        "access_token",
        "accessToken",
        "refresh_token",
        "refreshToken",
        "authorization",
        "Authorization",
        "password",
        "api_key",
        "apiKey",
    }
)

REDACTED = "<redacted>"
REDACTED_JWT = "<redacted-jwt>"


def redact_text(text: str) -> str:
    """Replace anything JWT-shaped inside a free-text string."""
    return JWT_RE.sub(REDACTED_JWT, text)


def redact(value: Any) -> Any:
    """Deep-copy ``value`` with every credential removed.

    Returns a new structure; the input is never mutated.
    """
    if isinstance(value, dict):
        return {
            key: (REDACTED if key in SECRET_KEYS else redact(item))
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact(item) for item in value]
    if isinstance(value, tuple):
        return tuple(redact(item) for item in value)
    if isinstance(value, str):
        return redact_text(value)
    return value
