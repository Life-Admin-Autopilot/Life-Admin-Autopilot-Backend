#!/usr/bin/env bash
# Bring up the whole stack for testing the frontend against the .NET backend.
#
#   ./tools/dev/stack.sh up      start everything, print status
#   ./tools/dev/stack.sh down    stop everything this script started
#   ./tools/dev/stack.sh status  what is listening, and is AI wired
#
# Ports, all localhost:
#   27018  mongo    isolated parity instance (NEVER the shared Atlas cluster)
#    7860  langflow the planning + document agents
#    5080  .NET     the backend the frontend talks to
#    4100  node     the reference server, only needed to re-run parity
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STEWARD="$(cd "$ROOT/../Steward" && pwd)"
# NOT $TMPDIR. macOS deletes files there, and on 2026-08-18 it took a WiredTiger
# index file out from under a RUNNING mongod: fatal assertion, server gone, every
# endpoint answering 500 with no code having changed. It happened three times in
# one afternoon, helped along by a full disk, and each time it looked like an
# application bug.
#
# The database was rebuilt here from a mongodump on 2026-08-18 — a fresh dbpath
# rather than a file copy, so no corrupted index came across. The old directory
# under $TMPDIR was left in place as a fallback and can be deleted once you trust
# this one:
#
#   rm -rf "$TMPDIR/kitto-stack"
#
# Override with KITTO_STACK_HOME to run against a different copy.
SCRATCH="${KITTO_STACK_HOME:-$HOME/.kitto-stack}"
mkdir -p "$SCRATCH/mongo/data" "$SCRATCH/mongo/log" "$SCRATCH/log"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"

# The Planning Agent v4 flow, as imported into Langflow.
LANGFLOW_FLOW="${LANGFLOW_FLOW:-6b0f1c2e-9a41-4d3f-8c77-91a1f10a9e14}"

port_pid() { lsof -ti "tcp:$1" 2>/dev/null | head -1; }
up()       { curl -sf -m 2 "$1" >/dev/null 2>&1; }

wait_for() { # url, label, tries
  for _ in $(seq 1 "${3:-60}"); do up "$1" && { echo "  ✓ $2"; return 0; }; sleep 1; done
  echo "  ✗ $2 did not come up — see $SCRATCH/log/"; return 1
}

start_mongo() {
  [ -n "$(port_pid 27018)" ] && { echo "  ✓ mongo (already running)"; return; }
  /opt/homebrew/bin/mongod --dbpath "$SCRATCH/mongo/data" --port 27018 --bind_ip 127.0.0.1 \
    --logpath "$SCRATCH/mongo/log/mongod.log" --fork >/dev/null 2>&1
  sleep 1; nc -z localhost 27018 && echo "  ✓ mongo" || echo "  ✗ mongo"
}

start_langflow() {
  up http://127.0.0.1:7860/health && { echo "  ✓ langflow (already running)"; return; }
  ( export LANGFLOW_AUTO_LOGIN=true DO_NOT_TRACK=true
    nohup langflow run --host 127.0.0.1 --port 7860 > "$SCRATCH/log/langflow.log" 2>&1 & )
  wait_for http://127.0.0.1:7860/health langflow 90
}

langflow_token() {
  curl -s -m 10 http://127.0.0.1:7860/api/v1/auto_login \
    | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>{try{console.log(JSON.parse(d).access_token)}catch{console.log('')}})"
}

start_dotnet() {
  local pid; pid="$(port_pid 5080)"; [ -n "$pid" ] && kill -9 "$pid" 2>/dev/null; sleep 1
  ( cd "$ROOT"
    export ASPNETCORE_URLS="http://[::]:5080"          # dual-stack: 127.0.0.1 alone breaks req.ip parity
    export ASPNETCORE_ENVIRONMENT=Development
    export Database__Provider=Sqlite
    export ConnectionStrings__DefaultConnection="Data Source=$SCRATCH/identity.db"
    export MongoDbSettings__ConnectionString="mongodb://127.0.0.1:27018"
    export MongoDbSettings__DatabaseName="kitto_dev"
    export Jwt__Key="${JWT_KEY:-dev-only-not-a-real-secret-0000000000000000000000000000000000}"

    # The admin console, served by THIS process rather than a second backend.
    #
    # Without ADMIN_JWT_SECRET the console is switched off — /admin/auth/signin
    # answers 403 admin_console_disabled — which is the safe default for a
    # deployment that has no console, and was why the dashboard was pointed at a
    # separate backend on :5099. That backend read a DIFFERENT database
    # (kitto_admin_demo), so a kill switch flipped in the console wrote somewhere
    # the app never looks: the switch appeared to do nothing, twice over. One
    # process serving both is what makes that divergence impossible rather than
    # merely fixed.
    export ADMIN_JWT_SECRET="${ADMIN_JWT_SECRET:-dev-only-admin-console-secret-000000000000000000000000000000}"

    # Grants Admin to an account that ALREADY EXISTS — it never creates
    # credentials. Sign up in the app first, then name that address here:
    #
    #   ADMIN_BOOTSTRAP_EMAIL=you@example.com ./tools/dev/stack.sh up
    export ADMIN_BOOTSTRAP_EMAIL="${ADMIN_BOOTSTRAP_EMAIL:-}"

    # Firebase push. Without a service account the send path answers
    # PUSH_NOT_CONFIGURED per device — which the console renders as a SERVER
    # problem, not a broken handset, so an operator sees the real cause. That is
    # a correct degraded mode, not a failure, and it stays the default here
    # because the key must never live in the repo.
    #
    # To turn real delivery on, drop the service-account JSON for the Firebase
    # project at the path below (it is gitignored), or point at it explicitly:
    #
    #   PUSH_SERVICE_ACCOUNT=/path/to/sa.json ./tools/dev/stack.sh up
    #
    # NOTE: the account must belong to the SAME Firebase project the mobile app
    # registers its tokens with, or every send fails PUSH_NOT_AUTHORIZED while
    # looking perfectly configured.
    PUSH_SA="${PUSH_SERVICE_ACCOUNT:-$ROOT/secrets/firebase-service-account.json}"
    if [ -f "$PUSH_SA" ]; then
      export PushNotifications__ServiceAccountFilePath="$PUSH_SA"
      echo "  ✓ push: service account found"
    else
      echo "  · push: no service account — sends will report PUSH_NOT_CONFIGURED"
    fi
    # The frontend's origins. Capacitor's webview sends capacitor://localhost.
    #
    # EXTRA_CORS_ORIGINS is how `Steward: npm run app` adds this machine's LAN
    # origin (http://<LAN-IP>:3000). Under Capacitor live-reload the webview
    # loads the whole app from there, so THAT — not capacitor://localhost — is
    # the Origin the backend sees. The allowlist is read once at startup, so it
    # has to arrive here; a missing entry surfaces on the phone as a bare
    # network error with no console to read it in.
    export Kernel__Cors__Origins="http://localhost:3000,http://127.0.0.1:3000,capacitor://localhost,http://localhost${EXTRA_CORS_ORIGINS:+,$EXTRA_CORS_ORIGINS}"
    export Kernel__Workers__Enabled=true
    export LANGFLOW_BASE_URL="http://127.0.0.1:7860"
    export LANGFLOW_FLOW_ID="$LANGFLOW_FLOW"
    # The Planning Agent has NO ChatInput node, so input_value alone reaches
    # nothing: Langflow accepts the request, streams a healthy-looking turn, and
    # hands the agent an empty prompt. Without this the whole AI surface answers
    # with an empty envelope and nothing anywhere reports an error.
    export LANGFLOW_INPUT_NODE="PlanningInput-v4"
    [ -f "$SCRATCH/langflow_api_key" ] && export LANGFLOW_API_KEY="$(cat "$SCRATCH/langflow_api_key")"
    # The node name contains a hyphen, so it is not a valid shell identifier and
    # cannot be `export`ed. `env` takes arbitrary names.
    nohup env 'Ai__Langflow__Tweaks__PlanningInput-v4__mode=chat' \
      dotnet run --project Life-Admin-Autopilot-Backend --no-launch-profile \
      > "$SCRATCH/log/dotnet.log" 2>&1 & )
  wait_for http://localhost:5080/health ".NET backend" 90
}

case "${1:-up}" in
  up)
    echo "starting the stack…"
    start_mongo
    start_langflow

    # Mint an API key once so the backend is not relying on AUTO_LOGIN.
    if [ ! -f "$SCRATCH/langflow_api_key" ]; then
      T="$(langflow_token)"
      [ -n "$T" ] && curl -s -m 20 -X POST http://127.0.0.1:7860/api/v1/api_key/ \
        -H "Authorization: Bearer $T" -H 'Content-Type: application/json' \
        -d '{"name":"kitto-dotnet-backend"}' \
        | node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>{try{const k=JSON.parse(d).api_key;if(k)require('fs').writeFileSync(process.argv[1],k)}catch{}})" \
          "$SCRATCH/langflow_api_key"
    fi

    start_dotnet
    echo
    "$0" status
    ;;

  down)
    for p in 5080 7860; do
      pid="$(port_pid "$p")"; [ -n "$pid" ] && { kill -9 "$pid" 2>/dev/null; echo "  stopped :$p"; }
    done
    pid="$(port_pid 27018)"; [ -n "$pid" ] && { kill "$pid" 2>/dev/null; echo "  stopped mongo"; }
    echo "note: the Node reference on :4100 and your own server on :4000 were left alone."
    ;;

  status)
    echo "stack:"
    nc -z localhost 27018 2>/dev/null && echo "  mongo     :27018  up" || echo "  mongo     :27018  DOWN"
    up http://127.0.0.1:7860/health   && echo "  langflow  :7860   up" || echo "  langflow  :7860   DOWN"
    up http://localhost:5080/health   && echo "  .NET      :5080   up" || echo "  .NET      :5080   DOWN"
    up http://localhost:4100/health   && echo "  node ref  :4100   up (parity only)" || echo "  node ref  :4100   down (only needed for parity runs)"
    echo
    echo "AI wiring — is the Langflow provider actually selected?"
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 5 -X POST http://localhost:5080/ai/ask \
      -H 'Content-Type: application/json' -d '{"question":"ping"}' 2>/dev/null)"
    case "$code" in
      401) echo "  ✓ /ai/ask answers 401 unauthenticated — the route is live." ;;
      503) echo "  ✗ /ai/ask is 503: Langflow NOT configured. Check LANGFLOW_BASE_URL / LANGFLOW_FLOW_ID." ;;
      *)   echo "  ? /ai/ask returned $code" ;;
    esac
    echo
    echo "frontend:  cd $STEWARD && npm run dev     → http://localhost:3000"
    echo "logs:      $SCRATCH/log/"
    ;;

  *) echo "usage: $0 {up|down|status}"; exit 1 ;;
esac
