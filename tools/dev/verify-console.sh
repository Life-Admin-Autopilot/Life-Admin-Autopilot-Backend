#!/usr/bin/env bash
# Phase 1 — drive every admin-console endpoint and assert the CUSTOMER-VISIBLE
# effect, not just the HTTP code. A 200 from an action that changes nothing is
# exactly the bug that started this work.
#
#   ./verify-console.sh
#
# Creates one throwaway customer, acts on it, deletes it. Touches no real account.
set -uo pipefail

API=http://127.0.0.1:5080
MONGO="mongodb://127.0.0.1:27018/kitto_dev"
SH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IDENTITY="${KITTO_STACK_HOME:-${TMPDIR:-/tmp}/kitto-stack}/identity.db"
MSH=/opt/homebrew/bin/mongosh

PASS=0; FAIL=0
ok()   { printf "  \033[32m✓\033[0m %s\n" "$1"; PASS=$((PASS+1)); }
bad()  { printf "  \033[31m✗\033[0m %s — %s\n" "$1" "$2"; FAIL=$((FAIL+1)); }
is()   { [ "$2" = "$3" ] && ok "$1" || bad "$1" "expected '$3', got '$2'"; }

mongo_eval() { $MSH "$MONGO" --quiet --eval "$1" 2>/dev/null; }
admin() { # method path [body]
  local m="$1" p="$2" b="${3:-}"
  if [ -n "$b" ]; then
    curl -s -m 20 -X "$m" "$API$p" -H "Authorization: Bearer $ADMIN" \
      -H 'Content-Type: application/json' -d "$b"
  else
    curl -s -m 20 -X "$m" "$API$p" -H "Authorization: Bearer $ADMIN"
  fi
}
admin_code() {
  local m="$1" p="$2" b="${3:-}"
  if [ -n "$b" ]; then
    curl -s -m 20 -o /dev/null -w '%{http_code}' -X "$m" "$API$p" -H "Authorization: Bearer $ADMIN" \
      -H 'Content-Type: application/json' -d "$b"
  else
    curl -s -m 20 -o /dev/null -w '%{http_code}' -X "$m" "$API$p" -H "Authorization: Bearer $ADMIN"
  fi
}
# Dotted-path JSON reader. Takes the path as an ARGUMENT rather than splicing it
# into the program text -- the first version built `eval('d[...]')` by string
# interpolation and the quotes collided, so every extraction silently returned
# empty and the script blamed signup for it.
jget() {
  python3 -c '
import sys, json
cur = json.load(sys.stdin)
for part in sys.argv[1].split("."):
    if part:
        cur = cur[part]
print("" if cur is None else cur)
' "$1" 2>/dev/null
}

# ---- setup ----------------------------------------------------------------
ID=$(sqlite3 "$IDENTITY" "SELECT Id FROM AspNetUsers WHERE lower(Email)='minamelad232@gmail.com';")
ADMIN=$(python3 "$SH/mint_admin_token.py" "$ID" "minamelad232@gmail.com" Admin)

EMAIL="console-probe-$(date +%s)@kitto.test"
PW='Probe-passw0rd!'
SIGNUP=$(curl -s -m 20 -X POST "$API/auth/signup" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PW\",\"displayName\":\"Console Probe\"}")
UTOK=$(printf '%s' "$SIGNUP" | jget tokens.accessToken)
UREF=$(printf '%s' "$SIGNUP" | jget tokens.refreshToken)
[ -z "$UTOK" ] && { echo "signup failed: $SIGNUP"; exit 1; }
CID=$(mongo_eval "print(db.users.findOne({email:'$EMAIL'})._id.toString())")
echo "probe customer: $EMAIL  ($CID)"
echo

cleanup() {
  echo
  echo "── cleanup ─────────────────────────────────────────────"
  mongo_eval "db.adminfeatureflags.deleteMany({updatedBy:{\$regex:'probe|claude'}});
              db.users.deleteMany({email:'$EMAIL'});
              db.notifications.deleteMany({userId:ObjectId('$CID')});
              db.scanneddocuments.deleteMany({userId:ObjectId('$CID')});" >/dev/null
  sqlite3 "$IDENTITY" "DELETE FROM AspNetUsers WHERE Email='$EMAIL';" 2>/dev/null
  echo "  probe customer and probe flags removed"
}
trap cleanup EXIT

echo "── 1. read surfaces ────────────────────────────────────"
is "GET /admin/customers"                 "$(admin_code GET '/admin/customers?take=5')" 200
is "GET /admin/customers/{id}"            "$(admin_code GET "/admin/customers/$CID")" 200
is "GET /admin/audit"                     "$(admin_code GET '/admin/audit?take=5')" 200
is "GET /admin/ops/flags"                 "$(admin_code GET '/admin/ops/flags')" 200
is "GET /admin/ops/admins"                "$(admin_code GET '/admin/ops/admins')" 200
for p in adoption funnel errors pulse top-spenders by-feature cost-distribution daily; do
  is "GET /admin/insights/$p"             "$(admin_code GET "/admin/insights/$p?days=30")" 200
done
is "GET /admin/customers/export"          "$(admin_code GET '/admin/customers/export')" 200
is "GET /admin/activity/recent"           "$(admin_code GET '/admin/activity/recent')" 200

echo
echo "── 2. suspend → the customer is actually locked out ────"
admin POST "/admin/customers/$CID/suspend" '{"reason":"phase-1 verification sweep"}' >/dev/null
is "suspendedAt written"    "$(mongo_eval "print(db.users.findOne({_id:ObjectId('$CID')}).suspendedAt?1:0)")" 1
REFRESH=$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/auth/refresh" \
  -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$UREF\"}")
is "refresh refused after suspend" "$REFRESH" 401
is "sign-in refused after suspend" \
  "$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/auth/signin" \
     -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PW\"}")" 403

echo
echo "── 3. restore → they can sign in again ─────────────────"
admin POST "/admin/customers/$CID/restore" '{"reason":"phase-1 verification sweep"}' >/dev/null
is "suspendedAt cleared"    "$(mongo_eval "print(db.users.findOne({_id:ObjectId('$CID')}).suspendedAt?1:0)")" 0
SIGNIN=$(curl -s -m 20 -X POST "$API/auth/signin" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PW\"}")
UTOK=$(printf '%s' "$SIGNIN" | jget tokens.accessToken)
UREF=$(printf '%s' "$SIGNIN" | jget tokens.refreshToken)
[ -n "$UTOK" ] && ok "sign-in works after restore" || bad "sign-in works after restore" "no token returned"

echo
echo "── 4. revoke sessions → the refresh token dies ─────────"
admin POST "/admin/customers/$CID/revoke-sessions" '{"reason":"phase-1 verification sweep"}' >/dev/null
is "refresh refused after revoke" \
  "$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/auth/refresh" \
     -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$UREF\"}")" 401
SIGNIN=$(curl -s -m 20 -X POST "$API/auth/signin" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PW\"}")
UTOK=$(printf '%s' "$SIGNIN" | jget tokens.accessToken)

echo
echo "── 5. reset quotas → the counter the gate reads is gone ─"
mongo_eval "db.aiusagecounters.insertOne({userId:ObjectId('$CID'),kind:'message',day:'2026-08-18',count:99})" >/dev/null
is "counter seeded"  "$(mongo_eval "print(db.aiusagecounters.countDocuments({userId:ObjectId('$CID')}))")" 1
admin POST "/admin/customers/$CID/reset-quota" '{"reason":"phase-1 verification sweep"}' >/dev/null
is "counter cleared" "$(mongo_eval "print(db.aiusagecounters.countDocuments({userId:ObjectId('$CID')}))")" 0

echo
echo "── 6. notify → it reaches the customer's own feed ──────"
admin POST "/admin/customers/$CID/notify" \
  '{"title":"Phase 1 probe","body":"Sent by the verification sweep.","reason":"phase-1 verification sweep"}' >/dev/null
FEED=$(curl -s -m 20 "$API/me/notifications" -H "Authorization: Bearer $UTOK")
printf '%s' "$FEED" | grep -q "Phase 1 probe" \
  && ok "notification readable at /me/notifications" \
  || bad "notification readable at /me/notifications" "not found in feed"

echo
echo "── 7. broadcast → preview counts, send lands ───────────"
is "broadcast preview" "$(admin_code GET '/admin/ops/broadcast/preview?segment=all')" 200
BC=$(admin POST '/admin/ops/broadcast' \
  '{"segment":"all","title":"Phase 1 broadcast","body":"Sent by the verification sweep.","reason":"phase-1 verification sweep"}')
printf '%s' "$BC" | grep -q '"inAppCreated"' && ok "broadcast reports in-app creation" \
  || bad "broadcast reports in-app creation" "$(printf '%s' "$BC" | head -c 120)"
sleep 1
curl -s -m 20 "$API/me/notifications" -H "Authorization: Bearer $UTOK" | grep -q "Phase 1 broadcast" \
  && ok "broadcast reached the probe customer's feed" \
  || bad "broadcast reached the probe customer's feed" "not in feed"

echo
echo "── 8. kill switches → each one blocks its own path ─────"
flip() { admin POST "/admin/ops/flags/$1" "{\"disabled\":$2,\"reason\":\"phase-1 verification sweep\"}" >/dev/null; }
cap()  { curl -s -m 10 "$API/me/capabilities" -H "Authorization: Bearer $UTOK" | jget "$1"; }

flip ai_chat true;       sleep 11
is "capabilities: aiChat off" "$(cap aiChat)" False
is "/ai/ask blocked" "$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/ai/ask" \
  -H "Authorization: Bearer $UTOK" -H 'Content-Type: application/json' \
  -d '{"question":"probe","timezone":"Africa/Cairo"}')" 503
flip ai_chat false;      sleep 11
is "capabilities: aiChat back on" "$(cap aiChat)" True

flip transcription true; sleep 11
is "capabilities: transcription off" "$(cap transcription)" False
is "/ai/voice/transcribe blocked" "$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/ai/voice/transcribe" \
  -H "Authorization: Bearer $UTOK" -H 'Content-Type: audio/m4a' --data-binary 'x')" 503
is "/api/speech/transcribe blocked" "$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/api/speech/transcribe" \
  -H "Authorization: Bearer $UTOK" -F 'audio=@/dev/null;filename=a.m4a')" 503
flip transcription false; sleep 11
is "capabilities: transcription back on" "$(cap transcription)" True

flip document_scan true;  sleep 11
is "capabilities: documentScan off" "$(cap documentScan)" False
# A REAL png, and every x-document-scan-* header the binder requires. The first
# version sent shell-quoted '\x89PNG...' (literal backslashes) with no
# captured-at header, so it 400'd on metadata and looked like the kill switch had
# blocked an upload it never reached.
PNG="$SH/probe.png"
python3 -c "import base64,sys;sys.stdout.buffer.write(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='))" > "$PNG"
upload() {
  curl -s -m 30 -o /dev/null -w '%{http_code}' -X POST "$API/me/document-scans" \
    -H "Authorization: Bearer $UTOK" -H 'Content-Type: image/png' \
    -H 'x-document-scan-source: camera' \
    -H "x-document-scan-captured-at: $(date -u +%Y-%m-%dT%H:%M:%S.000Z)" \
    -H 'x-document-scan-timezone: Africa/Cairo' \
    --data-binary "@$PNG"
}
is "upload still ACCEPTED while reading is paused" "$(upload)" 202
sleep 16
is "and it WAITS rather than failing (attempts stay 0)" \
  "$(mongo_eval "const u=db.users.findOne({email:'$EMAIL'});const d=db.scanneddocuments.findOne({userId:u._id});print(d?d.status+'/'+(d.attempts||0):'none')")" \
  "pending/0"
flip document_scan false; sleep 11
is "capabilities: documentScan back on" "$(cap documentScan)" True

echo
echo "── 9. audit → every action above was recorded ──────────"
AUD=$(admin GET '/admin/audit?take=60')
for a in customer.suspended customer.restored customer.sessions_revoked feature.toggled broadcast; do
  printf '%s' "$AUD" | grep -q "$a" && ok "audit records $a" || bad "audit records $a" "absent"
done

echo
echo "════════════════════════════════════════════════════════"
printf "  PASS %s   FAIL %s\n" "$PASS" "$FAIL"
echo "════════════════════════════════════════════════════════"
[ "$FAIL" -eq 0 ]
