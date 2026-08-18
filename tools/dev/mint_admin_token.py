#!/usr/bin/env python3
"""Mint a console token with the dev ADMIN_JWT_SECRET from tools/dev/stack.sh.

Signs the same claim set AdminTokenService.Issue writes. Uses the dev secret only
-- it never touches a user's password, and the token it produces is worth exactly
as much as that secret, which is a literal in a shell script.
"""
import base64, hashlib, hmac, json, sys, time, uuid

SECRET = "dev-only-admin-console-secret-000000000000000000000000000000"
ISSUER, AUDIENCE = "kitto-admin", "kitto-admin-console"
ROLE_CLAIM = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"

def b64(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

def mint(identity_id: str, email: str, roles) -> str:
    now = int(time.time())
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "sub": identity_id, "email": email, "jti": str(uuid.uuid4()),
        "nbf": now, "exp": now + 8 * 3600, "iat": now,
        "iss": ISSUER, "aud": AUDIENCE,
        ROLE_CLAIM: roles[0] if len(roles) == 1 else roles,
    }
    signing_input = f"{b64(json.dumps(header).encode())}.{b64(json.dumps(payload).encode())}"
    sig = hmac.new(SECRET.encode(), signing_input.encode(), hashlib.sha256).digest()
    return f"{signing_input}.{b64(sig)}"

if __name__ == "__main__":
    print(mint(sys.argv[1], sys.argv[2], sys.argv[3:] or ["Admin"]))
