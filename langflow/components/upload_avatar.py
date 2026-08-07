"""Upload Avatar - profile picture upload (FR-1.6).

Deliberately not wired into the main planning flow: setting a profile picture is a
settings action, not part of turning a spoken command into a task. Import this
component on its own when you need to demonstrate FR-1.6.
"""

import base64
import json
import mimetypes
import os
import time

import requests
from langflow.custom import Component
from langflow.io import BoolInput, FileInput, MessageTextInput, Output, SecretStrInput
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


class UploadAvatarComponent(Component):
    display_name = "Upload Avatar"
    description = "Uploads a profile picture to POST /api/users/me/avatar (FR-1.6)."
    icon = "user-round"
    name = "UploadAvatar"

    inputs = [
        FileInput(
            name="avatar_file",
            display_name="Profile Picture",
            file_types=["jpg", "jpeg", "png", "webp"],
            info="JPEG, PNG or WebP, up to 5 MB. The previous avatar is deleted on replace.",
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

    outputs = [Output(display_name="Result", name="output", method="upload")]

    def upload(self) -> Data:
        value = self.avatar_file
        if isinstance(value, list):
            value = value[0] if value else None
        if not value:
            return Data(data={"succeeded": False, "message": "No image selected."})

        token, _ = _login(self.base_url, self.email, self.password, self.verify_tls)

        file_name = os.path.basename(value)
        content_type = mimetypes.guess_type(file_name)[0] or "image/jpeg"

        with open(value, "rb") as handle:
            response = requests.post(
                f"{self.base_url}/api/users/me/avatar",
                headers={"Authorization": f"Bearer {token}"},
                files={"file": (file_name, handle, content_type)},
                timeout=120,
                verify=self.verify_tls,
            )

        try:
            body = response.json()
        except ValueError:
            return Data(
                data={"succeeded": False, "message": f"Unexpected HTTP {response.status_code}."}
            )

        if not body.get("succeeded"):
            return Data(
                data={
                    "succeeded": False,
                    "message": f"[{body.get('errorCode')}] {body.get('errorMessage')}",
                }
            )

        self.status = f"Avatar set to {body.get('path')}"
        return Data(
            data={
                "succeeded": True,
                "path": body.get("path"),
                "previewUrl": body.get("readUrl"),
                "sizeBytes": body.get("sizeBytes"),
            }
        )
