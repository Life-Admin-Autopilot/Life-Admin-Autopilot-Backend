"""Save Task - persists a confirmed task, and its document if one was staged.

Replaces the original tool, which posted to /api/Planning/commit. That controller does
not exist in the backend (story #35), so every save was a 404 that the tool reported as
a generic "Error saving task". Until #35 lands this writes through the two Test
controllers, which are the only endpoints that actually reach Mongo today.
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
            token, user_id = _login(self.base_url, self.email, self.password, self.verify_tls)
        except (ValueError, requests.RequestException) as error:
            return Data(data={"saved": False, "message": f"Could not authenticate: {error}"})

        headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}

        # UserTask has exactly these fields. Category and priority are NOT among them -
        # see the note in the returned payload.
        task_payload = {
            "userId": user_id,
            "title": title,
            "dueDate": due_date,
            "status": "pending",
            "sourceType": (self.source_type or "voice").strip(),
        }

        try:
            response = requests.post(
                f"{self.base_url}/api/usertaskstest",
                json=task_payload,
                headers=headers,
                timeout=30,
                verify=self.verify_tls,
            )
            response.raise_for_status()
            task = response.json()
        except requests.RequestException as error:
            return Data(data={"saved": False, "message": f"Could not save the task: {error}"})

        task_id = task.get("id")
        result = {
            "saved": True,
            "taskId": task_id,
            "title": title,
            "dueDate": due_date,
            "message": f"Saved '{title}', due {due_date[:10]}.",
        }

        document_path = (self.document_path or "").strip()
        if document_path:
            result["document"] = self._attach_document(
                headers, task_id, user_id, document_path
            )

        # Surfaced to the agent, and to whoever reads the flow output, because a task
        # that silently drops the category the user was just shown is worse than one
        # that admits it.
        if self.category or self.priority:
            result["notPersisted"] = (
                "category and priority were shown to the user but the UserTask document "
                "has no fields for them (see langflow/README.md)."
            )

        self.status = result["message"]
        return Data(data=result)

    def _attach_document(self, headers, task_id, user_id, document_path):
        """Write the Document record that links the staged blob to the task."""
        if not task_id:
            return "skipped: the task was saved but returned no id to attach to."

        extension = os.path.splitext(document_path)[1].lower()
        document_payload = {
            "taskId": task_id,
            "userId": user_id,
            "blobUrl": document_path,
            "category": (self.category or "").strip() or None,
            "sourceType": "pdf" if extension == ".pdf" else "photo",
            "uploadedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        }

        try:
            response = requests.post(
                f"{self.base_url}/api/documentstest",
                json=document_payload,
                headers=headers,
                timeout=30,
                verify=self.verify_tls,
            )
            response.raise_for_status()
        except requests.RequestException as error:
            # The task is already saved; losing the link is worth reporting but not
            # worth pretending the whole save failed.
            return f"task saved, but linking the document failed: {error}"

        if document_path.startswith("documents-staging/"):
            return (
                f"linked {document_path} - WARNING: still in documents-staging, which "
                "Azure deletes after ~24h. Promotion needs /planning/commit (story #35)."
            )
        return f"linked {document_path}"
