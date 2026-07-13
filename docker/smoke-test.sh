#!/usr/bin/env bash
set -euo pipefail
# Drives a running Inkshelf against a seeded ABS.
ABS_URL="${ABS_URL:?set ABS_URL to the seeded ABS}"
INKSHELF_URL="${INKSHELF_URL:-http://localhost:5099}"
JAR=$(mktemp)
fail() { echo "SMOKE FAIL: $1"; exit 1; }

# Discover library id + an item id via ABS (root token).
TOKEN=$(curl -sf -X POST "$ABS_URL/login" -H 'Content-Type: application/json' -H 'X-Return-Tokens: true' \
    -d '{"username":"root","password":"root"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['user']['accessToken'])")
LIBRARY_ID=$(curl -sf "$ABS_URL/api/libraries" -H "Authorization: Bearer $TOKEN" \
    | python3 -c "import sys,json;print(json.load(sys.stdin)['libraries'][0]['id'])")
ITEM_ID=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=1" -H "Authorization: Bearer $TOKEN" \
    | python3 -c "import sys,json;print(json.load(sys.stdin)['results'][0]['id'])")

# Login through Inkshelf.
curl -sf -c "$JAR" "$INKSHELF_URL/login" -o /tmp/s_login.html
TOK=$(grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' /tmp/s_login.html | sed 's/.*value="//;s/"//')
code=$(curl -s -o /dev/null -w "%{http_code}" -b "$JAR" -c "$JAR" -X POST "$INKSHELF_URL/login" \
    --data-urlencode "__RequestVerificationToken=$TOK" --data-urlencode "Username=root" --data-urlencode "Password=root")
[ "$code" = "302" ] || fail "login expected 302 got $code"

check() { # path substring
    local body; body=$(curl -sf -b "$JAR" "$INKSHELF_URL$1") || fail "GET $1 failed"
    echo "$body" | grep -q "$2" || fail "GET $1 missing '$2'"
    echo "  ok: $1"
}
check "/?all=1" "Test Library"
check "/library/$LIBRARY_ID" "Page "
check "/library/$LIBRARY_ID?q=Dune" "Dune"

# Real series filter link: fetch a series id, build group.<b64>, url-encode.
SERIES_FILTER=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/search?q=Mistborn&limit=5" -H "Authorization: Bearer $TOKEN" \
    | python3 -c "import sys,json,base64,urllib.parse;s=json.load(sys.stdin)['series'][0]['series']['id'];print(urllib.parse.quote('series.'+base64.b64encode(s.encode()).decode()))")
check "/library/$LIBRARY_ID?filter=$SERIES_FILTER" "clear"

code=$(curl -s -o /dev/null -w "%{http_code}" -b "$JAR" "$INKSHELF_URL/cover/$ITEM_ID")
# 200 (has cover) or 404 (no cover) are both fine; a 500 is a failure.
{ [ "$code" = "200" ] || [ "$code" = "404" ]; } || fail "GET /cover/$ITEM_ID expected 200/404 got $code"
echo "  ok: /cover/$ITEM_ID ($code)"

rm -f "$JAR"
echo "SMOKE PASS"
