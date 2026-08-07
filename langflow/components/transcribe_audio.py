"""Transcribe Audio - turns an uploaded recording into text via POST /api/speech/transcribe."""

import base64
import json
import time

import requests
from langflow.custom import Component
from langflow.io import BoolInput, FileInput, MessageTextInput, Output, SecretStrInput

# Plain top-level imports only. Langflow builds a component by walking its AST and
# executing the import statements it finds; a try/except ImportError fallback is skipped
# entirely, leaving the name undefined at class-definition time.
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


class TranscribeAudioComponent(Component):
    display_name = "Transcribe Audio"
    description = "Uploads a recording to POST /api/speech/transcribe and outputs the transcript."
    icon = "mic"
    name = "TranscribeAudio"

    inputs = [
        FileInput(
            name="audio_file",
            display_name="Audio Recording",
            file_types=["wav", "mp3"],
            info=(
                "WAV or MP3. The provider rejects AAC/M4A, so a voice note recorded on "
                "iOS must be converted first. Leave empty to type the command instead."
            ),
            temp_file=True,
        ),
        MessageTextInput(
            name="language",
            display_name="Language",
            value="ar-EG",
            info=(
                "The speaker's locale, e.g. ar-EG or en-US. Send it. Auto-detection fails "
                "badly on Arabic - it returns Latin transliteration."
            ),
        ),
        MessageTextInput(
            name="base_url",
            display_name="Backend Base URL",
            value="https://localhost:7276",
            advanced=True,
        ),
        MessageTextInput(name="email", display_name="Login Email", advanced=True),
        SecretStrInput(name="password", display_name="Login Password", advanced=True),
        BoolInput(
            name="verify_tls",
            display_name="Verify TLS",
            value=False,
            advanced=True,
            info="Off for the localhost dev certificate. Turn on against a deployed API.",
        ),
    ]

    outputs = [Output(display_name="Transcript", name="transcript", method="transcribe")]

    def _selected_path(self):
        value = self.audio_file
        if isinstance(value, list):
            value = value[0] if value else None
        return value or None

    def transcribe(self) -> Message:
        path = self._selected_path()

        # No recording is a normal state, not an error - the user typed their command
        # instead. An empty transcript lets the prompt fall through to the chat text.
        if not path:
            self.status = "No audio uploaded - using typed input."
            return Message(text="")

        token, _ = _login(self.base_url, self.email, self.password, self.verify_tls)

        with open(path, "rb") as handle:
            response = requests.post(
                f"{self.base_url}/api/speech/transcribe",
                headers={"Authorization": f"Bearer {token}"},
                files={"audio": (path.split("/")[-1].split("\\")[-1], handle, "audio/wav")},
                data={"language": self.language or ""},
                timeout=90,
                verify=self.verify_tls,
            )

        # The endpoint returns the same JSON shape on success and failure, so parse
        # before branching on the status code.
        try:
            body = response.json()
        except ValueError:
            msg = f"Transcription failed: HTTP {response.status_code} with a non-JSON body."
            raise ValueError(msg) from None

        if not body.get("succeeded"):
            # A recording was supplied and could not be transcribed. Failing loudly is
            # the point: returning "" here would look identical to "no audio given" and
            # the agent would invent a task from nothing.
            msg = (
                f"Transcription failed [{body.get('errorCode')}]: "
                f"{body.get('errorMessage') or 'no detail returned'}"
            )
            raise ValueError(msg)

        transcript = body.get("transcript") or ""
        self.status = (
            f"{len(transcript)} chars in {body.get('latencyMs')} ms "
            f"({body.get('detectedLanguage')})"
        )

        # The locale is stated rather than left for the model to infer. Asked to detect
        # it, the model follows the language of the conversation so far instead - an
        # Arabic command in a session that started in English came back answered in
        # English, which is the one thing a user notices immediately.
        language = (self.language or body.get("detectedLanguage") or "").strip()
        if language:
            return Message(text=f"(spoken in {language})\n{transcript}")

        return Message(text=transcript)
