#!/usr/bin/env bash
set -euo pipefail

ABS_URL="${ABS_URL:-http://host.docker.internal:13379}"
MAX_WAIT=40

echo "Waiting for ABS at $ABS_URL ..."
for i in $(seq 1 $MAX_WAIT); do
    curl -sf "$ABS_URL/healthcheck" >/dev/null 2>&1 && { echo "ABS ready."; break; }
    [ "$i" -eq "$MAX_WAIT" ] && { echo "ABS did not start"; exit 1; }
    sleep 1
done

curl -sf -X POST "$ABS_URL/init" -H 'Content-Type: application/json' \
    -d '{"newRoot":{"username":"root","password":"root"}}' >/dev/null 2>&1 || true

TOKEN=$(curl -sf -X POST "$ABS_URL/login" \
    -H 'Content-Type: application/json' -H 'X-Return-Tokens: true' \
    -d '{"username":"root","password":"root"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['user']['accessToken'])")
AUTH="Authorization: Bearer $TOKEN"

LIB=$(curl -sf -X POST "$ABS_URL/api/libraries" -H "$AUTH" -H 'Content-Type: application/json' \
    -d '{"name":"Test Library","folders":[{"fullPath":"/books"}],"mediaType":"book","provider":"google"}')
LIBRARY_ID=$(echo "$LIB" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])")
FOLDER_ID=$(echo "$LIB" | python3 -c "import sys,json; print(json.load(sys.stdin)['folders'][0]['id'])")
echo "Library: $LIBRARY_ID"

TMP=$(mktemp -d)
# Silent MP3 (no embedded metadata, so the upload form's title/author/series
# stick — an EPUB's OPF metadata would override them). ~1s of valid frames.
python3 -c "open('$TMP/a.mp3','wb').write((bytes([0xFF,0xFB,0x90,0x00])+b'\x00'*413)*38)"
# A small solid-colour PNG cover (no image libs needed).
python3 - "$TMP/cover.png" <<'PY'
import sys,zlib,struct
w,h,rgb=120,180,(60,60,90)
raw=b''.join(b'\x00'+bytes(rgb)*w for _ in range(h))
def chunk(t,d):
    c=t+d
    return struct.pack('>I',len(d))+c+struct.pack('>I',zlib.crc32(c)&0xffffffff)
open(sys.argv[1],'wb').write(
    b'\x89PNG\r\n\x1a\n'
    + chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,2,0,0,0))
    + chunk(b'IDAT',zlib.compress(raw))
    + chunk(b'IEND',b''))
PY

upload() { # title author series
    curl -sf -X POST "$ABS_URL/api/upload" -H "$AUTH" \
        -F "title=$1" -F "author=$2" ${3:+-F "series=$3"} \
        -F "library=$LIBRARY_ID" -F "folder=$FOLDER_ID" \
        -F "0=@$TMP/a.mp3;filename=audiobook.mp3" >/dev/null
    echo "  + $2 — $1${3:+ ($3)}"
}

echo "Uploading items (silent MP3 media so form metadata sticks)..."
upload "The Final Empire"      "Brandon Sanderson" "Mistborn"
upload "The Well of Ascension" "Brandon Sanderson" "Mistborn"
upload "The Hero of Ages"      "Brandon Sanderson" "Mistborn"
upload "Storm Front"           "Jim Butcher"       "The Dresden Files"
upload "Fool Moon"             "Jim Butcher"       "The Dresden Files"
upload "Grave Peril"           "Jim Butcher"       "The Dresden Files"
upload "Rivers of London"      "Ben Aaronovitch"   "Rivers of London"
upload "Moon Over Soho"        "Ben Aaronovitch"   "Rivers of London"
upload "Uprooted"              "Naomi Novik"       ""
upload "Redshirts"             "John Scalzi"       ""
upload "Dune"                  "Frank Herbert"     "Dune"
upload "Dune Messiah"          "Frank Herbert"     "Dune"
upload "Little Brother"        "Cory Doctorow"     ""
upload "Old Man's War"         "John Scalzi"       ""
upload "Spinning Silver"       "Naomi Novik"       ""

echo "Scanning..."
curl -sf -X POST "$ABS_URL/api/libraries/$LIBRARY_ID/scan" -H "$AUTH" >/dev/null
for i in $(seq 1 40); do
    N=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=0" -H "$AUTH" \
        | python3 -c "import sys,json; print(json.load(sys.stdin).get('total',0))" 2>/dev/null || echo 0)
    [ "${N:-0}" -ge 15 ] && { echo "Indexed $N items."; break; }
    sleep 1
done

# Give exactly one item a cover so both the cover-present and cover-absent
# paths are exercised. The rest stay coverless.
COVER_ITEM=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=1" -H "$AUTH" \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['results'][0]['id'])")
curl -sf -X POST "$ABS_URL/api/items/$COVER_ITEM/cover" -H "$AUTH" \
    -F "cover=@$TMP/cover.png;filename=cover.png" >/dev/null && \
    echo "Cover set on item $COVER_ITEM"

rm -rf "$TMP"
echo ""
echo "Seed complete. ABS_URL=$ABS_URL  LIBRARY_ID=$LIBRARY_ID  root/root"
echo "One item has a cover ($COVER_ITEM); the rest are coverless."
