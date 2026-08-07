"""Save Task - commits a confirmed task, and its document if one was staged.

Posts to /api/Planning/commit with the nested {task, document} payload. That endpoint is
NOT in this repository - no PlanningController exists on any branch - but it is live on
the backend build the team runs, and it is what has been writing the `tasks` collection,
including the Category and Priority fields the C# UserTask entity has no room for. It
also produces the embeddings in `contentChunks` ("committed and embedded").

So the endpoint stays. What changed from the original version of this component is only
what it does around the call: it no longer swallows every failure into one generic
string, it refuses a task with no due date, and it can attach a staged document.
"""

import base64
import json
import os
import time
from datetime import datetime, timezone

import requests
from langflow.custom import Component
from langflow.io import BoolInput, MessageTextInput, Output, SecretStrInput
from langflow.schema import Data

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


class SaveTaskToolComponent(Component):
    display_name = "Save Task"
    description = (
        "Saves a confirmed task to the database. Pass document_path only if the "
        "conversation contains an ATTACHED DOCUMENT block."
    )
    icon = "check-circle"
    name = "SaveTaskTool"

    inputs = [
        MessageTextInput(name="title", display_name="Task Title", tool_mode=True),
        MessageTextInput(
            name="due_date",
            display_name="Due Date",
            tool_mode=True,
            info="ISO-8601, e.g. 2026-08-05T00:00:00.000Z. Required - the save is refused without it.",
        ),
        MessageTextInput(name="category", display_name="Category", tool_mode=True),
        MessageTextInput(name="priority", display_name="Priority", tool_mode=True),
        MessageTextInput(name="source_type", display_name="Source Type", value="voice", tool_mode=True),
        MessageTextInput(
            name="document_path",
            display_name="Document Path",
            tool_mode=True,
            info="The storedPath from an ATTACHED DOCUMENT block. Leave empty when there is no document.",
        ),
        MessageTextInput(
            name="base_url",
            display_name="Backend Base URL",
            value="https://localhost:7276",
            advanced=True,
        ),
        MessageTextInput(
            name="commit_path",
            display_name="Commit Endpoint",
            value="/api/Planning/commit",
            advanced=True,
            info="Where a confirmed task is committed. Change this if the route moves.",
        ),
        MessageTextInput(
            name="user_id",
            display_name="User Id Override",
            advanced=True,
            info=(
                "Leave empty to use the id from Login Email below. Set it only to write "
                "as a specific user without logging in."
            ),
        ),
        MessageTextInput(name="email", display_name="Login Email", advanced=True),
        SecretStrInput(name="password", display_name="Login Password", advanced=True),
        BoolInput(name="verify_tls", display_name="Verify TLS", value=False, advanced=True),
    ]

    outputs = [Output(display_name="Result", name="output", method="save_task")]

    @staticmethod
    def _normalise_due_date(raw: str) -> str:
        raw = (raw or "").strip()
        # The agent is told to send a full timestamp but often sends a bare date.
        if len(raw) == 10:
            return f"{raw}T00:00:00.000Z"
        return raw

    def _identify(self):
        """Return (user_id, auth_headers). Logging in is optional."""
        override = (self.user_id or "").strip()
        headers = {"Content-Type": "application/json"}

        if not (self.email or "").strip():
            # The commit endpoint has never required a bearer token, so no credentials
            # is a supported configuration rather than an error.
            return override, headers

        token, token_user_id = _login(self.base_url, self.email, self.password, self.verify_tls)
        headers["Authorization"] = f"Bearer {token}"
        return override or token_user_id, headers

    def save_task(self) -> Data:
        title = (self.title or "").strip()
        if not title:
            return Data(data={"saved": False, "message": "Refused: the task has no title."})

        due_date = self._normalise_due_date(self.due_date)
        if not due_date:
            # Mirrors the agent instructions rather than trusting them - the model will
            # eventually try to save a dateless task, and a task with no due date can
            # never produce a reminder, which is the whole point of the product.
            return Data(
                data={
                    "saved": False,
                    "message": (
                        "Refused: no due date. Ask the user when this is due, then call "
                        "save_task again."
                    ),
                }
            )

        try:
            user_id, headers = self._identify()
        except (ValueError, requests.RequestException) as error:
            return Data(data={"saved": False, "message": f"Could not authenticate: {error}"})

        task = {
            "userId": user_id,
            "title": title,
            "dueDate": due_date,
            "category": (self.category or "").strip() or "General",
            "priority": (self.priority or "").strip() or "normal",
            "sourceType": (self.source_type or "voice").strip(),
        }

        document_path = (self.document_path or "").strip()
        document = self._document_payload(document_path, user_id) if document_path else None

        self._request_headers = headers
        result, note = self._commit(task, document)
        if document and note:
            result["document"] = note
        elif document_path:
            result["document"] = f"linked {document_path}"

        self.status = result.get("message", "")
        return Data(data=result)

    def _document_payload(self, document_path, user_id):
        extension = os.path.splitext(document_path)[1].lower()
        return {
            "userId": user_id,
            "blobUrl": document_path,
            "category": (self.category or "").strip() or None,
            "sourceType": "pdf" if extension == ".pdf" else "photo",
            "uploadedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        }

    def _commit(self, task, document):
        """POST the commit payload, falling back to a task-only commit if needed.

        The exact shape of the `document` half of the DTO is not verifiable from this
        repository, so a rejected document must never cost the user their task. If the
        server refuses the payload, the task is committed on its own and the caller is
        told the document was not attached.
        """
        response, error = self._post({"task": task, "document": document})

        if document is not None and response is not None and response.status_code in (400, 422):
            detail = response.text[:200]
            retry, retry_error = self._post({"task": task, "document": None})
            if retry is not None and retry.ok:
                return self._describe(retry, task), (
                    "NOT attached - the server rejected the document half of the payload "
                    f"({detail}). The task was saved without it."
                )
            response, error = retry, retry_error

        if error is not None:
            return {"saved": False, "message": f"Could not save the task: {error}"}, None
        if not response.ok:
            return {
                "saved": False,
                "message": f"Could not save the task: HTTP {response.status_code} {response.text[:200]}",
            }, None

        return self._describe(response, task), None

    def _post(self, payload):
        try:
            return requests.post(
                f"{self.base_url}{self.commit_path}",
                json=payload,
                headers=self._request_headers,
                timeout=60,
                verify=self.verify_tls,
            ), None
        except requests.RequestException as error:
            return None, error

    @staticmethod
    def _describe(response, task):
        body = {}
        try:
            body = response.json()
        except ValueError:
            pass
        if not isinstance(body, dict):
            body = {}

        return {
            "saved": True,
            "taskId": body.get("taskId") or body.get("id"),
            "title": task["title"],
            "dueDate": task["dueDate"],
            "category": task["category"],
            "priority": task["priority"],
            "message": f"Saved '{task['title']}', due {task['dueDate'][:10]}.",
        }
