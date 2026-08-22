#!/usr/bin/env bash
# Phase 1 — drive every admin-console endpoint and assert the CUSTOMER-VISIBLE
# effect, not just the HTTP code. A 200 from an action that changes nothing is
# exactly the bug that started this work.
#
#   ./verify-console.sh
#
# Creates one throwaway customer, acts on it, deletes it. Touches no real account.
set -uo pipefail

# Every one of these was a hardcoded literal from the machine this was written
# on -- port 5080, Mongo 27018/kitto_dev, a Homebrew mongosh path, and a SQLite
# identity file under $TMPDIR. None of them exist on a stack started by up.sh,
# so the script died on line 57 with `sqlite3: command not found` before it
# reached a single assertion. Defaults now match up.sh; override any of them.
API="${KITTO_API:-http://127.0.0.1:4000}"
MONGO="${KITTO_MONGO:-mongodb://127.0.0.1:27017/LifeAdminAutopilotDB}"
SH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# mongosh on the PATH if there is one, otherwise the copy inside the container
# compose already runs -- which is the only one present on a Docker-only setup.
if command -v mongosh >/dev/null 2>&1; then
  MSH=(mongosh)
elif [ -x /opt/homebrew/bin/mongosh ]; then
  MSH=(/opt/homebrew/bin/mongosh)
else
  MSH=(docker exec -i "${KITTO_MONGO_CONTAINER:-kitto-mongo}" mongosh)
  # Reached from inside the container, so the host port and hostname do not apply.
  MONGO="${KITTO_MONGO_IN_CONTAINER:-mongodb://127.0.0.1:27017/LifeAdminAutopilotDB}"
fi

PASS=0; FAIL=0
ok()   { printf "  \033[32m✓\033[0m %s\n" "$1"; PASS=$((PASS+1)); }
bad()  { printf "  \033[31m✗\033[0m %s — %s\n" "$1" "$2"; FAIL=$((FAIL+1)); }
is()   { [ "$2" = "$3" ] && ok "$1" || bad "$1" "expected '$3', got '$2'"; }

mongo_eval() { "${MSH[@]}" "$MONGO" --quiet --eval "$1" 2>/dev/null; }

# A path curl can actually open. Git Bash hands POSIX paths to a WINDOWS curl.exe,
# which cannot resolve them: `-F audio=@/dev/null` failed with `curl: (26) Failed
# to open/read local data`, curl wrote no request, and the assertion recorded 000
# — read for weeks as "the endpoint did not return 503" when no request had left
# the machine. /dev/null is not a file curl can upload on Windows at all, so the
# caller supplies a real one and this makes the path native where it has to be.
curlpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}
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
# No identity lookup. The API validates the token's signature and role claim and
# ignores `sub` entirely (checked against the running server), so reading a real
# GUID out of SQLite bought nothing and cost the script every machine that does
# not use the SQLite provider -- this stack runs Identity on hosted SQL Server.
ADMIN_EMAIL="${ADMIN_BOOTSTRAP_EMAIL:-admin@kitto.com}"
ADMIN=$(python3 "$SH/mint_admin_token.py" \
  "${KITTO_ADMIN_SUB:-00000000-0000-0000-0000-000000000000}" "$ADMIN_EMAIL" Admin)

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
              db.scanneddocuments.deleteMany({userId:ObjectId('$CID')});
              // Belt to the suspended-segment brace: any sweep notification that
              // reached an account other than the probe is this script's litter,
              // and leaving it means the next person sees a real-looking alert.
              db.notifications.deleteMany({title:'Phase 1 broadcast'});" >/dev/null
  # The Identity row, when Identity is the SQLite file this was written against.
  # Guarded on BOTH the tool and the file: `set -u` turned a bare reference to a
  # now-removed variable into an unbound-variable error INSIDE the EXIT trap, so
  # the trap died on its last line and every Mongo delete above it — which had
  # already run — was reported as never having happened. A cleanup that aborts
  # silently is worse than no cleanup, because the next run inherits the mess.
  IDENTITY="${KITTO_IDENTITY_DB:-${KITTO_STACK_HOME:-${TMPDIR:-/tmp}/kitto-stack}/identity.db}"
  if command -v sqlite3 >/dev/null 2>&1 && [ -f "$IDENTITY" ]; then
    sqlite3 "$IDENTITY" "DELETE FROM AspNetUsers WHERE Email='$EMAIL';" 2>/dev/null
    echo "  probe customer and probe flags removed"
  else
    # Hosted SQL Server, so the sign-in row outlives the probe. Harmless — it is
    # a throwaway address with no Mongo document behind it any more — but say so
    # rather than claim a clean sweep.
    echo "  probe flags and Mongo data removed; Identity row for $EMAIL left behind"
    echo "  (no local sqlite3 identity.db — this stack runs Identity elsewhere)"
  fi
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
# SEGMENT 'all' IS A REAL BROADCAST TO REAL PEOPLE, and this script used to send
# one on every run. On a shared dev database that is 27 accounts including the
# team's own: "Phase 1 broadcast / Sent by the verification sweep." landed in
# Omar's notification feed four times before anyone connected it to a test.
# Preview is safe -- it only counts -- so the reach of `all` is still asserted
# there. The SEND goes to a segment engineered to hold exactly the probe:
# nothing else is suspended, and the probe is suspended for the length of one
# call and restored immediately. A test may not write to accounts it did not make.
is "broadcast preview (counts only, sends nothing)" \
  "$(admin_code GET '/admin/ops/broadcast/preview?segment=all')" 200

admin POST "/admin/customers/$CID/suspend" '{"reason":"phase-1 verification sweep"}' >/dev/null
SUSPENDED_TOTAL=$(mongo_eval "print(db.users.countDocuments({suspendedAt:{\$ne:null,\$exists:true}}))" | tr -d '\r')
is "probe is the only suspended account" "$SUSPENDED_TOTAL" 1

BC=$(admin POST '/admin/ops/broadcast' \
  '{"segment":"suspended","title":"Phase 1 broadcast","body":"Sent by the verification sweep.","reason":"phase-1 verification sweep"}')
admin POST "/admin/customers/$CID/restore" '{"reason":"phase-1 verification sweep"}' >/dev/null

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
PROBE_AUDIO="${TMPDIR:-/tmp}/kitto-probe-$$.m4a"
: > "$PROBE_AUDIO"
is "/api/speech/transcribe blocked" "$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$API/api/speech/transcribe" \
  -H "Authorization: Bearer $UTOK" -F "audio=@$(curlpath "$PROBE_AUDIO");filename=a.m4a")" 503
rm -f "$PROBE_AUDIO"
flip transcription false; sleep 11
is "capabilities: transcription back on" "$(cap transcription)" True

flip document_scan true;  sleep 11
is "capabilities: documentScan off" "$(cap documentScan)" False
# A REAL png, and every x-document-scan-* header the binder requires. The first
# version sent shell-quoted '\x89PNG...' (literal backslashes) with no
# captured-at header, so it 400'd on metadata and looked like the kill switch had
# blocked an upload it never reached.
# In TMPDIR, not next to the script: $SH is tools/dev/ inside the repo, so the
# old path left an untracked binary behind after every run. Matches PROBE_AUDIO
# above, which already does this.
PNG="${TMPDIR:-/tmp}/kitto-probe-$$.png"
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
rm -f "$PNG"
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
