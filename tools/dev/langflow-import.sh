#!/usr/bin/env bash
# Load the Planning Agent into a fresh Langflow and give it its API key.
#
#   ./tools/dev/langflow-import.sh
#
# A new container comes up EMPTY. The flow and its credential live in Langflow's
# own database, inside a Docker volume — not in this repo — so a teammate who
# only runs `docker compose up` gets a Langflow that accepts every chat request,
# streams a healthy-looking turn, and answers nothing. This closes that gap.
#
# Reads GEMINI_API_KEY, or EMBEDDINGS_API_KEY as a fallback, from .env.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

# .env FIRST, then read from it. The other order looks equivalent and is not:
# LANGFLOW_BASE_URL and LANGFLOW_FLOW_ID both live in .env, so resolving them
# beforehand would quietly ignore a moved port and report success against a
# Langflow that was never touched.
# Through sed, to strip CR from a .env saved with Windows line endings. Left in,
# it rides along on every value: the Gemini key becomes "AIza...\r" and Google
# answers 400 on a key that is visibly correct in the file.
if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . <(sed 's/\r$//' ./.env)
  set +a
fi

BASE="${LANGFLOW_BASE_URL:-http://127.0.0.1:7860}"
FLOW_FILE="langflow/planning-agent.v4.json"
FLOW_ID="${LANGFLOW_FLOW_ID:-6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14}"
KEY="${GEMINI_API_KEY:-${EMBEDDINGS_API_KEY:-}}"

if [ ! -f "$FLOW_FILE" ]; then
  echo "Cannot find $FLOW_FILE — run this from the backend repo."
  exit 1
fi

echo "Waiting for Langflow at $BASE"
for _ in $(seq 1 60); do
  curl -sf -m 2 "$BASE/health" >/dev/null 2>&1 && break
  sleep 2
done

if ! curl -sf -m 2 "$BASE/health" >/dev/null 2>&1; then
  echo "  Langflow never answered. Is the container up? (docker compose ps)"
  exit 1
fi

# LANGFLOW_AUTO_LOGIN=true, set in docker-compose.yml, is what makes this exist.
TOKEN=$(curl -s -m 10 "$BASE/api/v1/auto_login" \
  | python -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))' 2>/dev/null)

if [ -z "$TOKEN" ]; then
  echo "  Could not get an auto-login token — is LANGFLOW_AUTO_LOGIN=true?"
  exit 1
fi

AUTH="Authorization: Bearer $TOKEN"

if curl -s -m 10 -H "$AUTH" "$BASE/api/v1/flows/$FLOW_ID" | grep -q '"id"'; then
  echo "Flow already present — leaving it alone."
else
  echo "Importing $FLOW_FILE"
  # /flows/upload takes the exported file as-is and keeps its id, which matters:
  # LANGFLOW_FLOW_ID in .env names this exact one.
  RESULT=$(curl -s -m 30 -X POST "$BASE/api/v1/flows/upload/" \
    -H "$AUTH" -F "file=@$FLOW_FILE")

  echo "$RESULT" | grep -q '"id"' \
    && echo "  imported" \
    || { echo "  import failed: $(echo "$RESULT" | head -c 300)"; exit 1; }
fi

if [ -z "$KEY" ]; then
  echo
  echo "No GEMINI_API_KEY or EMBEDDINGS_API_KEY in .env — the flow is imported"
  echo "but has no key, so chat will answer nothing. Fill one in and re-run."
  exit 0
fi

# Where the flow's eleven tools call BACK to reach Kitto. From inside the
# container that is host.docker.internal, never localhost — localhost there is
# the Langflow container itself.
#
# Missing, this fails a long way from its cause: Langflow answers 500 while
# building an unrelated tool ("Invalid value type NoneType for MessageTextInput")
# and recommends updating fifteen components, none of which is the problem. The
# flow references this variable 33 times.
STEWARD_URL="${STEWARD_API_BASE_URL:-http://host.docker.internal:4000}"

# Values are WRITE-ONLY for credentials: reading one back returns value:null, and
# a PATCH onto an existing variable answers 422. Delete then recreate is the only
# route that actually replaces one.
set_variable() { # name, value, type, field
  local name="$1" value="$2" vtype="$3" field="$4"

  local existing
  existing=$(curl -s -m 10 -H "$AUTH" "$BASE/api/v1/variables/" \
    | VAR_NAME="$name" python -c '
import json, os, sys
try:
    for v in json.load(sys.stdin):
        if v.get("name") == os.environ["VAR_NAME"]:
            print(v.get("id", ""))
            break
except Exception:
    pass' 2>/dev/null)

  if [ -n "$existing" ]; then
    curl -s -m 10 -X DELETE -H "$AUTH" "$BASE/api/v1/variables/$existing" >/dev/null
  fi

  # Through the ENVIRONMENT rather than argv: a trailing NAME=... on a
  # `python -c` line is just another argument, so an earlier version read
  # nothing and posted an empty body. It also keeps secrets off the process
  # command line, where `ps` would show them.
  local payload
  payload=$(VAR_NAME="$name" VAR_VALUE="$value" VAR_TYPE="$vtype" VAR_FIELD="$field" python -c '
import json, os
print(json.dumps({
    "name": os.environ["VAR_NAME"],
    "value": os.environ["VAR_VALUE"],
    "type": os.environ["VAR_TYPE"],
    "default_fields": [os.environ["VAR_FIELD"]],
}))')

  local created
  created=$(curl -s -m 15 -X POST "$BASE/api/v1/variables/" \
    -H "$AUTH" -H 'Content-Type: application/json' -d "$payload")

  if echo "$created" | grep -q '"id"'; then
    echo "  $name set"
  else
    echo "  $name FAILED: $(echo "$created" | head -c 200)"
    return 1
  fi
}

echo "Setting the flow's variables"
set_variable GEMINI_API_KEY       "$KEY"          Credential api_key
set_variable STEWARD_API_BASE_URL "$STEWARD_URL"  Generic    base_url

echo
echo "Langflow ready. Chat should answer once the backend is up."
