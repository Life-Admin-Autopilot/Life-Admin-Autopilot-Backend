#!/usr/bin/env bash
# Bring the backend up on :4000, reading secrets from .env.
#
#   ./tools/dev/up.sh              containers + backend
#   ./tools/dev/up.sh --no-docker  backend only, if the containers are already up
#   ./tools/dev/up.sh --check      report what is configured, start nothing
#
# Replaces tools/dev/stack.sh for everyday work. That one launches Homebrew's
# mongod from a hard-coded /opt/homebrew path and expects a sibling Steward
# checkout, so it only ever ran on one macOS machine; this runs anywhere with
# Docker and the .NET SDK, Git Bash included.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

DO_DOCKER=1
CHECK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --no-docker) DO_DOCKER=0 ;;
    --check)     CHECK_ONLY=1; DO_DOCKER=0 ;;
    *) echo "unknown option: $arg"; exit 2 ;;
  esac
done

if [ ! -f .env ]; then
  echo "No .env found. Start from the template:"
  echo "    cp .env.example .env"
  echo "Then fill it in — docs/RUNNING.md says which values you actually need."
  exit 1
fi

# Export every KEY=VALUE in .env. `set -a` marks assignments for export, so the
# file needs no `export` prefixes and stays readable.
#
# Sourced through sed rather than directly, to strip CR from a .env saved with
# Windows line endings — Notepad, or a file that travelled as a .txt. Bash on
# macOS and Linux keeps that CR as part of the VALUE, so a correct 64-character
# key measures 65 and this script rejects it two checks below, blaming the key.
# Git Bash happens to strip it, which is why the fault only appears on someone
# else's machine.
set -a
# shellcheck disable=SC1091
. <(sed 's/\r$//' ./.env)
set +a

# A relative SQLite path resolves against the process's working directory, which
# is the PL project rather than the repo root — so the same setting would make a
# different file depending on how it was launched.
case "${ConnectionStrings__DefaultConnection:-}" in
  *"./"*) export ConnectionStrings__DefaultConnection="Data Source=$ROOT/kitto-dev.db" ;;
esac

have() { [ -n "${1:-}" ] && echo "configured" || echo "MISSING"; }

echo "Configuration"
echo "  signing key       $(have "${JWT_ACCESS_SECRET:-}")"
echo "  gemini            $(have "${EMBEDDINGS_API_KEY:-}")   planning, RAG, document reading"
echo "  hugging face      $(have "${HF_TOKEN:-}")   voice capture"
echo "  google oauth      $(have "${GOOGLE_CLIENT_ID:-}")   calendar + tasks"
echo "  token encryption  $(have "${INTEGRATION_ENCRYPTION_KEY:-}")   required WITH google oauth"
echo "  firebase          $(have "${FCM_SERVICE_ACCOUNT_FILE:-}${FCM_SERVICE_ACCOUNT_JSON:-}")   push to a phone"

if [ -z "${JWT_ACCESS_SECRET:-}" ]; then
  echo
  echo "JWT_ACCESS_SECRET is empty — sign-in cannot work. Generate one:"
  echo "    openssl rand -hex 32"
  exit 1
fi

# A key present but not 64 hex characters throws deep inside the first Google
# connect rather than at boot, which reads as the integration being broken.
if [ -n "${INTEGRATION_ENCRYPTION_KEY:-}" ] && ! printf '%s' "$INTEGRATION_ENCRYPTION_KEY" | grep -qE '^[0-9a-fA-F]{64}$'; then
  echo
  echo "INTEGRATION_ENCRYPTION_KEY must be exactly 64 hex characters."
  exit 1
fi

if [ -n "${FCM_SERVICE_ACCOUNT_FILE:-}" ] && [ ! -f "$FCM_SERVICE_ACCOUNT_FILE" ]; then
  echo
  echo "FCM_SERVICE_ACCOUNT_FILE points at a file that does not exist:"
  echo "    $FCM_SERVICE_ACCOUNT_FILE"
  exit 1
fi

[ "$CHECK_ONLY" = "1" ] && exit 0

if [ "$DO_DOCKER" = "1" ]; then
  echo
  echo "Starting containers"
  docker compose up -d || {
    echo "  docker compose failed — is Docker Desktop running?"
    exit 1
  }

  # Mongo accepts a TCP connection before it will answer a query, and the app's
  # index creation on boot is what trips over that gap.
  printf "  waiting for mongo"
  for _ in $(seq 1 30); do
    if docker exec kitto-mongo mongosh --quiet --eval 'db.adminCommand({ping:1}).ok' >/dev/null 2>&1; then
      echo " — ready"
      break
    fi
    printf "."
    sleep 1
  done
fi

export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://[::]:4000}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

echo
echo "Backend on ${ASPNETCORE_URLS}  (Ctrl+C to stop)"

# The Langflow tweak has to travel through `env`, not through .env.
#
# Its name contains a hyphen — PlanningInput-v4 — and a shell variable cannot,
# so a line for it in .env would abort the `. ./.env` above rather than set
# anything. Passing it here is not a workaround; it is the only route.
#
# Without it the flow's input node receives nothing, and the failure surfaces
# far away from the cause: Langflow answers 500 while building an unrelated tool
# component ("Invalid value type NoneType for MessageTextInput") and recommends
# updating fifteen components, none of which is the problem. Measured on a clean
# import against the pinned version.
exec env 'Ai__Langflow__Tweaks__PlanningInput-v4__mode=chat' \
  dotnet run --project Life-Admin-Autopilot-Backend --no-launch-profile
