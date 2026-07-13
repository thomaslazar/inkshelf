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
# Minimal valid EPUB (mimetype stored first, uncompressed).
python3 - "$TMP" <<'PY'
import os,sys,zipfile
t=sys.argv[1]; b=os.path.join(t,'e'); os.makedirs(b+'/META-INF',exist_ok=True)
open(b+'/mimetype','w').write('application/epub+zip')
open(b+'/META-INF/container.xml','w').write('<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
open(b+'/content.opf','w').write('<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>x</dc:title><dc:language>en</dc:language></metadata><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>')
open(b+'/c1.xhtml','w').write('<?xml version="1.0"?><!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>c</title></head><body><p>c</p></body></html>')
z=zipfile.ZipFile(t+'/book.epub','w')
z.write(b+'/mimetype','mimetype',compress_type=zipfile.ZIP_STORED)
z.write(b+'/META-INF/container.xml','META-INF/container.xml')
z.write(b+'/content.opf','content.opf'); z.write(b+'/c1.xhtml','c1.xhtml'); z.close()
PY

upload() { # title author series
    curl -sf -X POST "$ABS_URL/api/upload" -H "$AUTH" \
        -F "title=$1" -F "author=$2" ${3:+-F "series=$3"} \
        -F "library=$LIBRARY_ID" -F "folder=$FOLDER_ID" \
        -F "0=@$TMP/book.epub;filename=book.epub" >/dev/null
    echo "  + $2 — $1${3:+ ($3)}"
}

echo "Uploading items (EPUB media; the file just makes ABS create a book item)..."
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
rm -rf "$TMP"

echo "Scanning..."
curl -sf -X POST "$ABS_URL/api/libraries/$LIBRARY_ID/scan" -H "$AUTH" >/dev/null
for i in $(seq 1 40); do
    N=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=0" -H "$AUTH" \
        | python3 -c "import sys,json; print(json.load(sys.stdin).get('total',0))" 2>/dev/null || echo 0)
    [ "${N:-0}" -ge 15 ] && { echo "Indexed $N items."; break; }
    sleep 1
done

echo ""
echo "Seed complete. ABS_URL=$ABS_URL  LIBRARY_ID=$LIBRARY_ID  root/root"
