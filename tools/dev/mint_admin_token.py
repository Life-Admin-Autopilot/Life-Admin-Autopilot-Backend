#!/usr/bin/env python3
"""Mint a console token with the dev ADMIN_JWT_SECRET.

Signs the same claim set AdminTokenService.Issue writes. Uses the dev secret only
-- it never touches a user's password, and the token it produces is worth exactly
as much as that secret, which is a literal in a shell script.

The secret comes from $ADMIN_JWT_SECRET when set, and only falls back to the
literal below. Hardcoding it alone meant the script silently minted tokens the
server rejected on any machine whose .env had chosen a different value -- and a
401 from a signature mismatch is indistinguishable from a 401 for being denied,
so the failure read as "the console rejected you" rather than "wrong key".

The SUBJECT is not checked against the identity store. Verified against the
running server 2026-08-22: a token whose `sub` is an all-zero GUID is accepted on
/admin/ops/*, because the API validates the signature and the role claim and
nothing else. That is why callers may pass any stable id here, and why
verify-console.sh no longer needs to read one out of a SQLite file that exists
only on the machine the script was written on.
"""
import base64, hashlib, hmac, json, os, sys, time, uuid

SECRET = os.environ.get(
    "ADMIN_JWT_SECRET",
    "dev-only-admin-console-secret-000000000000000000000000000000",
)
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
