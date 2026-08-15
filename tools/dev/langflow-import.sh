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

BASE="${LANGFLOW_BASE_URL:-http://127.0.0.1:7860}"
FLOW_FILE="langflow/planning-agent.v4.json"
FLOW_ID="6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14"

if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

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

echo "Setting the GEMINI_API_KEY credential"

# Credential values are WRITE-ONLY: reading one back returns value:null, and a
# PATCH onto an existing variable answers 422. Delete then recreate is the only
# route that actually replaces it.
EXISTING=$(curl -s -m 10 -H "$AUTH" "$BASE/api/v1/variables/" \
  | python -c '
import json,sys
try:
    for v in json.load(sys.stdin):
        if v.get("name") == "GEMINI_API_KEY":
            print(v.get("id",""))
            break
except Exception:
    pass' 2>/dev/null)

if [ -n "$EXISTING" ]; then
  curl -s -m 10 -X DELETE -H "$AUTH" "$BASE/api/v1/variables/$EXISTING" >/dev/null
fi

CREATED=$(curl -s -m 15 -X POST "$BASE/api/v1/variables/" \
  -H "$AUTH" -H 'Content-Type: application/json' \
  -d "$(python -c '
import json,os
print(json.dumps({
    "name": "GEMINI_API_KEY",
    "value": os.environ["KEY"],
    "type": "Credential",
    "default_fields": ["api_key"],
}))' KEY="$KEY")")

echo "$CREATED" | grep -q '"id"' \
  && echo "  set" \
  || echo "  could not set it: $(echo "$CREATED" | head -c 300)"

echo
echo "Langflow ready. Chat should answer once the backend is up."
