"""Stage Document - uploads a photo or PDF to POST /api/documents/staging.

Storing happens before the user confirms anything (SRS 7.1): the file has to exist
so it can be previewed during confirmation. No `documents` record is written here.
If the user never confirms, an Azure lifecycle rule deletes the staged blob in ~24h.
"""

import base64
import json
import mimetypes
import os
import time

import requests
from langflow.custom import Component
from langflow.io import BoolInput, FileInput, MessageTextInput, Output, SecretStrInput

# Plain top-level imports only - see the note in transcribe_audio.py.
from langflow.schema.message import Message

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


class StageDocumentComponent(Component):
    display_name = "Stage Document"
    description = "Uploads a photo or PDF to POST /api/documents/staging and describes it to the agent."
    icon = "file-up"
    name = "StageDocument"

    inputs = [
        FileInput(
            name="document_file",
            display_name="Document or Photo",
            # Exactly what Claude's multimodal API can read, because the Document Agent
            # extracts from these files directly.
            file_types=["pdf", "jpg", "jpeg", "png", "webp", "gif"],
            info="PDF up to 20 MB, or a photo of a letter/bill/ID. Leave empty for a text-only command.",
            temp_file=True,
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

    outputs = [Output(display_name="Document Context", name="document_context", method="stage")]

    def _selected_path(self):
        value = self.document_file
        if isinstance(value, list):
            value = value[0] if value else None
        return value or None

    def stage(self) -> Message:
        path = self._selected_path()

        if not path:
            self.status = "No document attached."
            return Message(text="")

        token, _ = _login(self.base_url, self.email, self.password, self.verify_tls)

        file_name = os.path.basename(path)
        content_type = mimetypes.guess_type(file_name)[0] or "application/octet-stream"

        with open(path, "rb") as handle:
            response = requests.post(
                f"{self.base_url}/api/documents/staging",
                headers={"Authorization": f"Bearer {token}"},
                files={"file": (file_name, handle, content_type)},
                timeout=120,
                verify=self.verify_tls,
            )

        try:
            body = response.json()
        except ValueError:
            msg = f"Upload failed: HTTP {response.status_code} with a non-JSON body."
            raise ValueError(msg) from None

        if not body.get("succeeded"):
            msg = (
                f"Upload failed [{body.get('errorCode')}]: "
                f"{body.get('errorMessage') or 'no detail returned'}"
            )
            raise ValueError(msg)

        stored_path = body.get("path")
        self.status = f"Staged {file_name} -> {stored_path}"

        extracted = self._extract(token, stored_path)

        # A labelled block rather than raw JSON: the agent has to copy storedPath
        # verbatim into save_task, and a fenced key/value list survives paraphrasing
        # far better than a nested object.
        lines = [
            "[ATTACHED DOCUMENT]",
            f"fileName: {file_name}",
            f"storedPath: {stored_path}",
            f"contentType: {body.get('contentType')}",
            f"sizeBytes: {body.get('sizeBytes')}",
            f"previewUrl: {body.get('readUrl')}",
        ]
        lines.extend(extracted)
        lines.append(
            "This file is staged, not saved. Pass storedPath unchanged as the "
            "document_path argument when the user confirms the task."
        )

        return Message(text="\n".join(lines))

    def _extract(self, token, stored_path):
        """Ask the backend what the document actually says.

        Without this the agent only ever sees a filename, so it names the task from
        whatever the user typed and invents a due date - on a bill, the real one is
        printed inside the file.
        """
        try:
            response = requests.post(
                f"{self.base_url}/api/documents/extract",
                json={"path": stored_path},
                headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
                timeout=180,
                verify=self.verify_tls,
            )
        except requests.RequestException as error:
            return [f"contents: could not be read ({error})"]

        # Older API builds have no extract route. Staging still worked, so the flow
        # continues with what it has rather than failing the upload.
        if response.status_code == 404:
            return ["contents: not read - this backend has no /api/documents/extract yet"]

        try:
            body = response.json()
        except ValueError:
            return [f"contents: could not be read (HTTP {response.status_code})"]

        if not body.get("succeeded"):
            return [f"contents: could not be read ({body.get('errorCode')})"]

        lines = []
        if body.get("description"):
            lines.append(f"contents: {body['description']}")
        # Named separately from the description so the agent can lift it directly
        # instead of parsing prose for a date.
        if body.get("dueDate"):
            lines.append(f"dueDateStatedInDocument: {body['dueDate']}")
        if body.get("amount"):
            lines.append(f"amount: {body['amount']}")
        if body.get("issuer"):
            lines.append(f"issuer: {body['issuer']}")
        if body.get("category"):
            lines.append(f"suggestedCategory: {body['category']}")

        if not any(k in body and body[k] for k in ("dueDate",)):
            lines.append("dueDateStatedInDocument: none - the document does not state one")

        return lines or ["contents: nothing could be read from this file"]
