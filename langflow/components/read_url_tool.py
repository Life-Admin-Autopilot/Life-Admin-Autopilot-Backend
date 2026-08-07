"""Get File URL - exchanges a stored path for a short-lived preview URL.

Stored paths are not fetchable on their own. The database holds a path, never a URL,
so that records cannot rot into dead links and no live credential is persisted; a
signed 15-minute URL is minted only when something actually needs to display the file.
"""

import base64
import json
import time

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


class GetFileUrlToolComponent(Component):
    display_name = "Get File URL"
    description = (
        "Turns a stored file path into a link the user can open. The link expires in "
        "15 minutes, so fetch a fresh one each time rather than reusing an old link."
    )
    icon = "link"
    name = "GetFileUrlTool"

    inputs = [
        MessageTextInput(
            name="path",
            display_name="Stored Path",
            tool_mode=True,
            info="e.g. documents/<userId>/<guid>.pdf",
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

    outputs = [Output(display_name="Result", name="output", method="get_file_url")]

    def get_file_url(self) -> Data:
        path = (self.path or "").strip()
        if not path:
            return Data(data={"succeeded": False, "message": "No path given."})

        try:
            token, _ = _login(self.base_url, self.email, self.password, self.verify_tls)
        except (ValueError, requests.RequestException) as error:
            return Data(data={"succeeded": False, "message": f"Could not authenticate: {error}"})

        response = requests.get(
            f"{self.base_url}/api/files/read-url",
            params={"path": path},
            headers={"Authorization": f"Bearer {token}"},
            timeout=30,
            verify=self.verify_tls,
        )

        # 403 means the path belongs to a different user. Say so plainly instead of
        # letting the agent guess that the file is missing and offer to re-upload it.
        if response.status_code == 403:
            return Data(data={"succeeded": False, "message": "That file belongs to another user."})
        if response.status_code == 404:
            return Data(
                data={
                    "succeeded": False,
                    "message": "That file no longer exists - a staged file expires after about a day.",
                }
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

        self.status = f"Minted a 15-minute URL for {path}"
        return Data(data={"succeeded": True, "url": body.get("readUrl"), "expiresInMinutes": 15})
