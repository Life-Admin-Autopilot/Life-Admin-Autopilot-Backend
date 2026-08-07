"""Reference copy of the helper block that every Life Admin custom component embeds.

This file is documentation, not an import target. Langflow custom components are
pasted into the UI as a single self-contained script and are exec'd in isolation -
a component cannot `import` a sibling file. So each component repeats this block
verbatim. Change it here first, then propagate.
"""

import base64
import json
import time

import requests

# Access tokens live 15 minutes. Caching them keeps a five-tool conversation from
# performing five logins, and keyed by (base_url, email) so switching either one
# in the UI does not silently reuse the wrong identity.
_TOKEN_CACHE: dict[tuple[str, str], dict] = {}


def _login(base_url: str, email: str, password: str, verify_tls: bool) -> tuple[str, str]:
    """Return (access_token, user_id), reusing a cached token while it is still valid."""
    key = (base_url, email)
    cached = _TOKEN_CACHE.get(key)
    # 60s of slack so a token cannot expire between this check and the call that uses it.
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

    user_id = _user_id_from_token(token)
    _TOKEN_CACHE[key] = {
        "token": token,
        "user_id": user_id,
        # Trust the token's own exp rather than assuming the configured 15 minutes.
        "expires_at": _expiry_from_token(token),
    }
    return token, user_id


def _decode_claims(token: str) -> dict:
    """Read the JWT payload without verifying it.

    Verification is the API's job - it re-checks the signature on every request.
    Here the claims are only used to label outgoing data with the same user id the
    API will derive from the token itself, so a forged token buys nothing.
    """
    payload = token.split(".")[1]
    payload += "=" * (-len(payload) % 4)  # restore base64url padding
    return json.loads(base64.urlsafe_b64decode(payload))


def _user_id_from_token(token: str) -> str:
    claims = _decode_claims(token)
    # `sub` is what JwtTokenService writes; ASP.NET may surface it as nameid instead.
    return claims.get("sub") or claims.get("nameid") or ""


def _expiry_from_token(token: str) -> float:
    return float(_decode_claims(token).get("exp", time.time() + 600))


def _auth_headers(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"}
