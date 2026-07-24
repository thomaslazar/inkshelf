#!/bin/bash
# Headless-browser UI pass: bring up the seeded local ABS, start Inkshelf against
# it, drive both no-auth and authenticated pages with Playwright, then stop.
# Usage: tools/uicheck/run.sh   (run from anywhere inside the repo)
set -u

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
PORT=${PORT:-5099}
ABS=${ABS_URL:-http://host.docker.internal:13379}

# One-time per container: download the headless browser (no root needed).
if [ ! -d "$HOME/.cache/ms-playwright" ]; then
  echo "Installing Chromium (one-time)…"
  dotnet build "$SCRIPT_DIR/uicheck.csproj" -c Debug --nologo -v q || exit 1
  pwsh "$SCRIPT_DIR/bin/Debug/net10.0/playwright.ps1" install chromium || exit 1
fi
# If Chromium later fails for missing libs (works out-of-box on dotnet:10):
#   sudo pwsh tools/uicheck/bin/Debug/net10.0/playwright.ps1 install-deps chromium

# Bring up the seeded ABS (idempotent). Seed only if it has no library yet, so
# repeat runs don't pile up duplicate libraries in the persistent volume.
echo "Ensuring seeded ABS at $ABS …"
docker compose -f "$REPO/docker/docker-compose.yml" up -d >/dev/null 2>&1
curl --retry-connrefused --retry 60 --retry-delay 1 -sf "$ABS/healthcheck" >/dev/null 2>&1 \
  || { echo "ABS did not become healthy at $ABS"; exit 2; }
TOK=$(curl -sf -X POST "$ABS/login" -H 'Content-Type: application/json' -H 'X-Return-Tokens: true' \
  -d '{"username":"root","password":"root"}' 2>/dev/null \
  | python3 -c "import sys,json;print(json.load(sys.stdin)['user']['accessToken'])" 2>/dev/null || true)
LIBS=$(curl -sf "$ABS/api/libraries" -H "Authorization: Bearer ${TOK:-}" 2>/dev/null \
  | python3 -c "import sys,json;print(len(json.load(sys.stdin).get('libraries',[])))" 2>/dev/null || echo 0)
if [ "${LIBS:-0}" -lt 1 ]; then
  echo "Seeding ABS…"; ABS_URL="$ABS" bash "$REPO/docker/seed.sh" || { echo "seed failed"; exit 2; }
fi

# Start Inkshelf against the seeded ABS. Fresh cache each run so the live
# Convert-button click always starts from an unconverted (data-warm) state.
export ABS_URL="$ABS"
export CachePath="$(mktemp -d)"
export ASPNETCORE_URLS="http://127.0.0.1:$PORT"
dotnet run --project "$REPO/src/Inkshelf" -c Debug --no-launch-profile >"$SCRIPT_DIR/app.log" 2>&1 &
APP=$!
trap 'kill $APP 2>/dev/null' EXIT
curl --retry-connrefused --retry 60 --retry-delay 1 -s -o /dev/null "http://127.0.0.1:$PORT/login" \
  || { echo "server never came up:"; tail -20 "$SCRIPT_DIR/app.log"; exit 2; }

# Run the checks (no-auth + authenticated).
BASE_URL="http://127.0.0.1:$PORT" OUT_DIR="$SCRIPT_DIR/shots" UICHECK_AUTHED=1 \
  dotnet run --project "$SCRIPT_DIR/uicheck.csproj" -c Debug --no-launch-profile
RC=$?
echo "screenshots: $SCRIPT_DIR/shots"
exit $RC
