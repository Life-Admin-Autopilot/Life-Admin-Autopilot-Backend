"""Find Conflicting Tasks - checks a draft against what the user already has.

Story #30 requires each draft task to carry its own conflict details before anything is
saved. The agent cannot do that on its own: it has no memory of tasks from previous
sessions, so without this tool `conflicts` is always an empty list and the requirement
is only cosmetically met.

Conflict rule, as agreed:
  - same calendar day (Cairo local, since that is the day the user means), AND
  - both tasks have a real time set, within 60 minutes of each other
  A date-only task is "sometime that day", not a booking, so it never clashes on time.
  A near-identical title on the same day is reported separately as a likely duplicate -
  Mongo already holds "Pay the electricity bill" five times.
Plus an overdue sweep, so the user is warned when work is piling up behind them.
"""

import base64
import json
import re
import time
import unicodedata
from datetime import datetime, timedelta, timezone

import requests
from langflow.custom import Component
from langflow.io import BoolInput, IntInput, MessageTextInput, Output, SecretStrInput
from langflow.schema import Data

# The user's day is a Cairo day. Comparing UTC dates would put anything after 22:00
# local on the wrong side of midnight.
CAIRO = timezone(timedelta(hours=2))

# A task in either of these states cannot be double-booked with anything.
CLOSED_STATUSES = {"completed", "cancelled", "canceled"}

_TOKEN_CACHE: dict = {}


def _decode_claims(token):
    payload = token.split(".")[1]
    payload += "=" * (-len(payload) % 4)
    return json.loads(base64.urlsafe_b64decode(payload))


def _login(base_url, email, password, verify_tls):
    key = (base_url, email)
    cached = _TOKEN_CACHE.get(key)
    if cached and cached["expires_at"] > time.time() + 60:
        return cached["token"], cached["user_id"]

    response = requests.post(
        f"{base_url}/api/auth/login",
        json={"email": email, "password": password},
        timeout=30,
        verify=verify_tls,
    )
    if response.status_code == 401:
        msg = "Login failed: wrong email or password."
        raise ValueError(msg)
    response.raise_for_status()

    token = response.json().get("accessToken")
    if not token:
        msg = "Login succeeded but returned no accessToken."
        raise ValueError(msg)

    claims = _decode_claims(token)
    _TOKEN_CACHE[key] = {
        "token": token,
        "user_id": claims.get("sub") or claims.get("nameid") or "",
        "expires_at": float(claims.get("exp", time.time() + 600)),
    }
    return token, _TOKEN_CACHE[key]["user_id"]


def _normalise(text):
    """Fold a title down to comparable tokens, in Arabic as well as English.

    Arabic writes the same word several ways - alef comes as ا أ إ آ, ta marbuta and ha
    are used interchangeably, and diacritics are optional. Without folding these,
    "تجديد رخصة العربية" and "تجديد رخصه العربيه" look like different tasks.
    """
    text = unicodedata.normalize("NFKD", (text or "").lower())
    text = "".join(c for c in text if not unicodedata.combining(c))
    for source, target in (("أإآا", "ا"), ("ة", "ه"), ("ى", "ي"), ("ؤ", "و"), ("ئ", "ي")):
        for character in source:
            text = text.replace(character, target)
    text = re.sub(r"[^\w\s]", " ", text)
    # Arabic articles and English filler carry no meaning for a duplicate check.
    stop = {"the", "a", "an", "my", "to", "for", "of", "ال", "في", "على", "من"}
    return {w for w in text.split() if w and w not in stop}


def _similar(first, second):
    """Jaccard overlap of the two token sets, 0.0 to 1.0."""
    a, b = _normalise(first), _normalise(second)
    if not a or not b:
        return 0.0
    return len(a & b) / len(a | b)


def _parse(value):
    """Read the several date shapes the API and Mongo hand back."""
    if not value:
        return None
    if isinstance(value, datetime):
        parsed = value
    else:
        text = str(value).strip().replace("Z", "+00:00")
        try:
            parsed = datetime.fromisoformat(text)
        except ValueError:
            for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d"):
                try:
                    parsed = datetime.strptime(text[:19], fmt)
                    break
                except ValueError:
                    continue
            else:
                return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(CAIRO)


def _has_time(moment):
    """True when the task is an appointment rather than just a day.

    The test has to run in UTC. A date-only task is written as T00:00:00.000Z, and
    converting that to Cairo gives 02:00 - which would look like a 2am appointment and
    make every date-only task collide with every other one on the same day.
    """
    if moment is None:
        return False
    utc = moment.astimezone(timezone.utc)
    return (utc.hour, utc.minute) != (0, 0)


class FindConflictingTasksComponent(Component):
    display_name = "Find Conflicting Tasks"
    description = (
        "Checks one draft task against the user's existing tasks. Call this for every "
        "draft before showing it, and report what comes back."
    )
    icon = "calendar-clock"
    name = "FindConflictingTasks"

    inputs = [
        MessageTextInput(name="title", display_name="Draft Title", tool_mode=True),
        MessageTextInput(
            name="due_date",
            display_name="Draft Due Date",
            tool_mode=True,
            info="ISO-8601. Pass it even when it is a date with no time.",
        ),
        MessageTextInput(
            name="base_url",
            display_name="Backend Base URL",
            value="https://localhost:7276",
            advanced=True,
        ),
        MessageTextInput(
            name="tasks_path",
            display_name="Tasks Endpoint",
            value="/api/UserTasksTest/user",
            advanced=True,
            info="The user id is appended to this path.",
        ),
        MessageTextInput(name="user_id", display_name="User Id Override", advanced=True),
        MessageTextInput(name="email", display_name="Login Email", advanced=True),
        SecretStrInput(name="password", display_name="Login Password", advanced=True),
        IntInput(
            name="window_minutes",
            display_name="Clash Window (minutes)",
            value=60,
            advanced=True,
            info="Two timed tasks this close together on the same day are a conflict.",
        ),
        BoolInput(name="verify_tls", display_name="Verify TLS", value=False, advanced=True),
    ]

    # The method name IS the tool name the agent sees - Langflow derives it from the
    # method, not from the component or the output. It has to match what the system
    # prompt tells the agent to call, or the agent silently reports "no conflicts found"
    # for a tool it never had.
    outputs = [Output(display_name="Result", name="output", method="find_conflicting_tasks")]

    def _identify(self):
        override = (self.user_id or "").strip()
        headers = {}
        if not (self.email or "").strip():
            return override, headers
        token, token_user = _login(self.base_url, self.email, self.password, self.verify_tls)
        headers["Authorization"] = f"Bearer {token}"
        return override or token_user, headers

    def find_conflicting_tasks(self) -> Data:
        title = (self.title or "").strip()
        if not title:
            return Data(data={"checked": False, "message": "No draft title to check."})

        try:
            user_id, headers = self._identify()
        except (ValueError, requests.RequestException) as error:
            return Data(data={"checked": False, "message": f"Could not authenticate: {error}"})

        if not user_id:
            return Data(data={"checked": False, "message": "No user id, so nothing to compare against."})

        try:
            response = requests.get(
                f"{self.base_url}{self.tasks_path}/{user_id}",
                headers=headers,
                timeout=30,
                verify=self.verify_tls,
            )
            response.raise_for_status()
            existing = response.json()
        except (requests.RequestException, ValueError) as error:
            # Returning "unknown" rather than "no conflicts" matters: the agent must not
            # tell the user their calendar is clear when nothing was actually checked.
            return Data(
                data={
                    "checked": False,
                    "message": f"Could not read existing tasks, so conflicts are unknown: {error}",
                }
            )

        draft_due = _parse(self.due_date)
        window = timedelta(minutes=int(self.window_minutes or 60))
        now = datetime.now(CAIRO)

        conflicts, overdue = [], []
        for task in existing if isinstance(existing, list) else []:
            status = str(task.get("status") or task.get("Status") or "").lower()
            existing_title = task.get("title") or task.get("Title") or ""
            existing_due = _parse(task.get("dueDate") or task.get("DueDate"))

            if status in CLOSED_STATUSES:
                continue

            if existing_due and existing_due < now:
                overdue.append({"title": existing_title, "dueDate": existing_due.strftime("%Y-%m-%d")})

            same_day = (
                draft_due is not None
                and existing_due is not None
                and draft_due.date() == existing_due.date()
            )

            if (
                same_day
                and _has_time(draft_due)
                and _has_time(existing_due)
                and abs(draft_due - existing_due) <= window
            ):
                conflicts.append({
                    "type": "time_clash",
                    "with": existing_title,
                    "when": existing_due.strftime("%Y-%m-%d %H:%M"),
                    "detail": (
                        f"'{existing_title}' is already at {existing_due.strftime('%H:%M')} "
                        f"on {existing_due.strftime('%Y-%m-%d')}, within "
                        f"{window.seconds // 60} minutes of this one."
                    ),
                })
                continue

            # A duplicate is a duplicate whatever date it carries. This used to be gated
            # behind the same-day test above, so a draft with no due date was never
            # compared with anything - even though the agent is told this tool reports
            # duplicates in exactly that case.
            if _similar(title, existing_title) >= 0.7:
                when = existing_due.strftime("%Y-%m-%d") if existing_due else "no due date"
                conflicts.append({
                    "type": "possible_duplicate",
                    "with": existing_title,
                    "when": when,
                    "detail": (
                        f"'{existing_title}' is already saved ({when}) and looks like "
                        "the same thing."
                    ),
                })

        result = {
            "checked": True,
            "title": title,
            "conflicts": conflicts,
            "conflictCount": len(conflicts),
            "overdueCount": len(overdue),
            # A few examples are enough for the agent to say something useful without
            # dragging the user's whole backlog into the prompt.
            "overdueExamples": sorted(overdue, key=lambda o: o["dueDate"])[:3],
        }
        self.status = f"{len(conflicts)} conflict(s), {len(overdue)} overdue"
        return Data(data=result)
