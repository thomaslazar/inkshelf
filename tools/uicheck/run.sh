#!/bin/bash
# Headless-browser UI pass: start the app, drive it with Playwright, stop it.
# Usage: tools/uicheck/run.sh   (run from anywhere inside the repo)
set -u

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)
PORT=${PORT:-5099}

# One-time per container: download the headless browser (no root needed).
if [ ! -d "$HOME/.cache/ms-playwright" ]; then
  echo "Playwright browser not installed. Building harness + installing Chromium (one-time)…"
  dotnet build "$SCRIPT_DIR/uicheck.csproj" -c Debug --nologo -v q || exit 1
  pwsh "$SCRIPT_DIR/bin/Debug/net10.0/playwright.ps1" install chromium || {
    echo "Chromium download failed."; exit 1; }
fi

# If launch later fails for missing system libs, run once (needs root):
#   sudo pwsh tools/uicheck/bin/Debug/net10.0/playwright.ps1 install-deps chromium

export ABS_URL=${ABS_URL:-http://dummy.local}
export ASPNETCORE_URLS="http://127.0.0.1:$PORT"

dotnet run --project "$REPO/src/Inkshelf" -c Debug --no-launch-profile >"$SCRIPT_DIR/app.log" 2>&1 &
APP=$!
trap 'kill $APP 2>/dev/null' EXIT

curl --retry-connrefused --retry 60 --retry-delay 1 -s -o /dev/null "http://127.0.0.1:$PORT/login" \
  || { echo "server never came up:"; tail -20 "$SCRIPT_DIR/app.log"; exit 2; }

BASE_URL="http://127.0.0.1:$PORT" OUT_DIR="$SCRIPT_DIR/shots" \
  dotnet run --project "$SCRIPT_DIR/uicheck.csproj" -c Debug --no-launch-profile
RC=$?
echo "screenshots: $SCRIPT_DIR/shots"
exit $RC
