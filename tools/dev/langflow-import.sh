#!/usr/bin/env bash
# Load this repo's Langflow flows into a fresh Langflow and give them their keys.
#
#   ./tools/dev/langflow-import.sh            import what is missing, touch nothing else
#   ./tools/dev/langflow-import.sh --replace   ALSO replace flows that already exist
#
# --replace is how a teammate picks up flow changes after a pull: flows live in
# Langflow's own database, not in this repo, so `git pull` alone changes nothing
# they run. Plain mode stays the safe default — it cannot overwrite local
# experiments — which is exactly why it cannot deliver updates either.
#
# A new container comes up EMPTY — or rather, it comes up with Langflow's own 30
# starter examples and none of ours, which is worse, because it looks populated.
# The flows and their credentials live in Langflow's own database, inside a Docker
# volume — not in this repo — so a teammate who only runs `docker compose up` gets
# a Langflow that accepts every chat request, streams a healthy-looking turn, and
# answers nothing. This closes that gap.
#
# Two flows, and they do NOT share a provider:
#
#   planning-agent.v4.json  the chat/planning agent the backend runs on every
#                           turn (LANGFLOW_FLOW_ID). Gemini, via GEMINI_API_KEY
#                           or EMBEDDINGS_API_KEY from .env.
#   document-agent.json     reads a scanned document into task CANDIDATES. Talks
#                           to the ITI student gateway, not Gemini, so it needs
#                           SBG_API_KEY. Nothing in the backend calls it yet —
#                           it is imported so it can be run and evaluated in the
#                           Langflow UI at all, which it never has been.
#
# Idempotent: a flow that is already present is left alone. Variables are always
# rewritten, because that is the only way to correct a wrong one (see below).
set -uo pipefail

REPLACE=0
for arg in "$@"; do
  case "$arg" in
    --replace) REPLACE=1 ;;
    *) echo "unknown option: $arg"; exit 2 ;;
  esac
done

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

# Which Python. `python` is a Git Bash / macOS-ism: Ubuntu 24.04 — the OS this
# is deployed on — ships python3 and NO `python` alias at all, so the bare name
# resolves to nothing and every JSON parse below silently yields an empty
# string. The failure surfaced as "Could not get an auto-login token — is
# LANGFLOW_AUTO_LOGIN=true?" on a Langflow whose auto_login endpoint was
# returning a perfectly good token, which is a long way from the cause.
PY_BIN="$(command -v python3 || command -v python || true)"
if [ -z "$PY_BIN" ]; then
  echo "Need python3 (or python) on PATH to read Langflow's JSON responses."
  exit 1
fi

BASE="${LANGFLOW_BASE_URL:-http://127.0.0.1:7860}"
FLOW_FILE="langflow/planning-agent.v4.json"
FLOW_ID="${LANGFLOW_FLOW_ID:-6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14}"
KEY="${GEMINI_API_KEY:-${EMBEDDINGS_API_KEY:-}}"

# The document agent's id is NOT configurable: no backend setting names it, so
# there is no .env value that could disagree with the file. It is pinned here to
# the id inside the export, which is what /flows/upload preserves.
DOC_FLOW_FILE="langflow/document-agent.json"
DOC_FLOW_ID="7c1f0a52-3d84-4f6e-9a10-2b5c8e41d7a3"
DOC_KEY="${DOCUMENT_AGENT_API_KEY:-${SBG_API_KEY:-}}"
DOC_BASE_URL="${DOCUMENT_AGENT_BASE_URL:-http://apiaccess.iti.net.eg}"
DOC_MODEL_ID="${DOCUMENT_AGENT_MODEL_ID:-global.anthropic.claude-haiku-4-5-20251001-v1:0}"

for f in "$FLOW_FILE" "$DOC_FLOW_FILE"; do
  if [ ! -f "$f" ]; then
    echo "Cannot find $f — run this from the backend repo."
    exit 1
  fi
done

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
  | "$PY_BIN" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))' 2>/dev/null)

if [ -z "$TOKEN" ]; then
  echo "  Could not get an auto-login token — is LANGFLOW_AUTO_LOGIN=true?"
  exit 1
fi

AUTH="Authorization: Bearer $TOKEN"

import_flow() { # file, id, label
  local file="$1" id="$2" label="$3"

  if curl -s -m 10 -H "$AUTH" "$BASE/api/v1/flows/$id" | grep -q '"id"'; then
    if [ "$REPLACE" -eq 0 ]; then
      echo "$label already present — leaving it alone (--replace to update it)."
      return 0
    fi
    # Delete-then-upload, because upload alone answers 409 for an id that exists
    # and a PATCH would not replace the graph. The id survives: it comes from
    # the file, and the file is what names it in .env.
    echo "$label present — replacing with the repo's version."
    curl -s -m 10 -X DELETE -H "$AUTH" "$BASE/api/v1/flows/$id" >/dev/null
  fi

  echo "Importing $file"
  # /flows/upload takes the exported file as-is and KEEPS ITS ID, which matters:
  # a drag-and-drop import through the UI mints a new one, and the backend goes on
  # addressing the id from .env — so chat 404s while the UI shows a healthy flow.
  local result
  result=$(curl -s -m 30 -X POST "$BASE/api/v1/flows/upload/" \
    -H "$AUTH" -F "file=@$file")

  echo "$result" | grep -q '"id"' \
    && echo "  imported" \
    || { echo "  import failed: $(echo "$result" | head -c 300)"; return 1; }
}

import_flow "$FLOW_FILE"     "$FLOW_ID"     "Planning agent" || exit 1
import_flow "$DOC_FLOW_FILE" "$DOC_FLOW_ID" "Document agent" || exit 1

if [ -z "$KEY" ]; then
  echo
  echo "No GEMINI_API_KEY or EMBEDDINGS_API_KEY in .env — the flows are imported"
  echo "but chat has no key, so it will answer nothing. Fill one in and re-run."
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
    | VAR_NAME="$name" "$PY_BIN" -c '
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
  # `"$PY_BIN" -c` line is just another argument, so an earlier version read
  # nothing and posted an empty body. It also keeps secrets off the process
  # command line, where `ps` would show them.
  local payload
  payload=$(VAR_NAME="$name" VAR_VALUE="$value" VAR_TYPE="$vtype" VAR_FIELD="$field" "$PY_BIN" -c '
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

echo "Setting the planning agent's variables"
set_variable GEMINI_API_KEY       "$KEY"          Credential api_key
set_variable STEWARD_API_BASE_URL "$STEWARD_URL"  Generic    base_url

# The document agent reads all three of its settings from globals — every field is
# load_from_db, and the component RAISES rather than defaulting to a host, so a
# missing one fails a long way from its cause.
echo "Setting the document agent's variables"
if [ -z "$DOC_KEY" ]; then
  echo "  no SBG_API_KEY in .env — skipping DOCUMENT_AGENT_API_KEY."
  echo "  The document agent will not run, but nothing in the backend calls it yet."
else
  set_variable DOCUMENT_AGENT_API_KEY "$DOC_KEY" Credential api_key
fi
set_variable DOCUMENT_AGENT_BASE_URL "$DOC_BASE_URL" Generic api_base_url
set_variable DOCUMENT_AGENT_MODEL_ID "$DOC_MODEL_ID" Generic model_id

echo
echo "Langflow ready. Chat should answer once the backend is up."
