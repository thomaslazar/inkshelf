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

# --- Ebook fixtures (for the future download feature) ---
# Minimal valid EPUB (its embedded metadata is a placeholder; the real
# title/author/series are set via PATCH after the scan, below).
python3 - "$TMP" <<'PY'
import os,sys,zipfile
t=sys.argv[1]; b=os.path.join(t,'e'); os.makedirs(b+'/META-INF',exist_ok=True)
open(b+'/mimetype','w').write('application/epub+zip')
open(b+'/META-INF/container.xml','w').write('<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
open(b+'/content.opf','w').write('<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">x</dc:identifier><dc:title>Placeholder</dc:title><dc:language>en</dc:language></metadata><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/></spine></package>')
open(b+'/c1.xhtml','w').write('<?xml version="1.0"?><!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>c</title></head><body><p>c</p></body></html>')
z=zipfile.ZipFile(t+'/sample.epub','w')
z.write(b+'/mimetype','mimetype',compress_type=zipfile.ZIP_STORED)
z.write(b+'/META-INF/container.xml','META-INF/container.xml')
z.write(b+'/content.opf','content.opf'); z.write(b+'/c1.xhtml','c1.xhtml'); z.close()
PY
# Minimal PDF.
printf '%%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\ntrailer<</Root 1 0 R>>\n%%%%EOF\n' > "$TMP/sample.pdf"
# CBZ = zip of image(s). (CBR needs the proprietary `rar` tool, unavailable
# here — drop a real .cbr into the library folder if you need to test cbr.)
cp "$TMP/cover.png" "$TMP/page-01.png"
(cd "$TMP" && zip -jq sample.cbz page-01.png)
# An oversized CBZ: one ~300 KiB incompressible page, stored (no compression),
# so ABS reports a file size well over the uicheck run's tiny archive ceiling.
# Exercises the TooLarge failure-reason path end to end.
python3 -c "import os;open('$TMP/big.jpg','wb').write(b'\xff\xd8'+os.urandom(300000))"
(cd "$TMP" && zip -j0q big.cbz big.jpg)
# A CBZ that is NOT a real archive (plain bytes named .cbz). ABS still indexes it
# by extension, but the converter's ArchiveFactory can't open it → BadArchive.
printf 'this is not a zip or rar archive, just plain text\n' > "$TMP/bad-archive.cbz"
# A VALID zip whose only page is not a real image: opens as an archive (so it
# clears the BadArchive stage), but the image decode throws → ConvertError.
printf 'GARBAGE not-a-real-jpeg bytes 12345\n' > "$TMP/page-bad.jpg"
(cd "$TMP" && zip -jq bad-image.cbz page-bad.jpg)
# CBR = RAR archive; only if the `rar` tool is installed (see the devcontainer
# Dockerfile, or `sudo apt-get install -y rar`). Skipped otherwise.
if command -v rar >/dev/null 2>&1; then
    cp "$TMP/cover.png" "$TMP/page-02.png"
    (cd "$TMP" && rar a -inul sample.cbr page-02.png)
fi

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

uploadf() { # title author series filepath
    curl -sf -X POST "$ABS_URL/api/upload" -H "$AUTH" \
        -F "title=$1" -F "author=$2" ${3:+-F "series=$3"} \
        -F "library=$LIBRARY_ID" -F "folder=$FOLDER_ID" \
        -F "0=@$4" >/dev/null
    echo "  + $1 [ebook fixture]"
}
echo "Uploading ebook fixtures (epub / pdf / cbz)..."
uploadf "The Silent Sea"    "Ada Ebook"  "Deep Space" "$TMP/sample.epub"
uploadf "Field Manual"      "Pete PDF"   ""           "$TMP/sample.pdf"
uploadf "Neon Blade Vol. 1" "Mika Manga" "Neon Blade" "$TMP/sample.cbz"
uploadf "Big Comic Vol. 1" "Mika Manga" "Neon Blade" "$TMP/big.cbz"
uploadf "Corrupt Archive"  "Broken Comics" ""        "$TMP/bad-archive.cbz"
uploadf "Broken Page"      "Broken Comics" ""        "$TMP/bad-image.cbz"
EXPECT=21
[ -f "$TMP/sample.cbr" ] && { uploadf "Neon Blade Vol. 2" "Mika Manga" "Neon Blade" "$TMP/sample.cbr"; EXPECT=22; }

echo "Scanning..."
curl -sf -X POST "$ABS_URL/api/libraries/$LIBRARY_ID/scan" -H "$AUTH" >/dev/null
for i in $(seq 1 40); do
    N=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=0" -H "$AUTH" \
        | python3 -c "import sys,json; print(json.load(sys.stdin).get('total',0))" 2>/dev/null || echo 0)
    [ "${N:-0}" -ge "$EXPECT" ] && { echo "Indexed $N items."; break; }
    sleep 1
done

# The scan reads each ebook's embedded metadata (e.g. the EPUB's placeholder
# title), so set the intended title/author/series per format via PATCH.
TOKEN="$TOKEN" ABS_URL="$ABS_URL" LIBRARY_ID="$LIBRARY_ID" python3 <<'PY'
import os, json, urllib.request
absu, tok, lib = os.environ['ABS_URL'], os.environ['TOKEN'], os.environ['LIBRARY_ID']
def req(method, path, data=None):
    body = json.dumps(data).encode() if data is not None else None
    r = urllib.request.Request(absu + path, data=body, method=method,
        headers={'Authorization': 'Bearer ' + tok, 'Content-Type': 'application/json'})
    return json.load(urllib.request.urlopen(r))
meta = {
    # The epub carries genres + narrators (and tags below) so the item-detail
    # Genres/Narrators/Tags labels render — the pages that exercise localisation.
    'epub': {'title': 'The Silent Sea', 'authors': [{'name': 'Ada Ebook'}], 'series': [{'name': 'Deep Space', 'sequence': '1'}],
             'genres': ['Science Fiction', 'Adventure'], 'narrators': ['Sam Sample']},
    'pdf':  {'title': 'Field Manual', 'authors': [{'name': 'Pete PDF'}]},
    'cbz':  {'title': 'Neon Blade Vol. 1', 'authors': [{'name': 'Mika Manga'}], 'series': [{'name': 'Neon Blade', 'sequence': '1'}]},
    'cbr':  {'title': 'Neon Blade Vol. 2', 'authors': [{'name': 'Mika Manga'}], 'series': [{'name': 'Neon Blade', 'sequence': '2'}]},
}
tags = {'epub': ['favorite', 'sci-fi']}  # media-level (sibling of metadata)
for it in req('GET', '/api/libraries/%s/items?limit=200' % lib)['results']:
    media = it.get('media') or {}
    fmt = media.get('ebookFormat')
    if fmt == 'cbz':
        # Several CBZ fixtures share the format. Disambiguate by the upload title,
        # which survives the scan for CBZ (no embedded metadata to override). The
        # two "real" comics get series metadata; the corrupt fixtures
        # (Corrupt Archive / Broken Page) keep their upload title/author untouched.
        title = (media.get('metadata') or {}).get('title') or ''
        if title == 'Big Comic Vol. 1':
            m = {'title': 'Big Comic Vol. 1', 'authors': [{'name': 'Mika Manga'}], 'series': [{'name': 'Neon Blade', 'sequence': '1'}]}
        elif title == 'Neon Blade Vol. 1':
            m = meta['cbz']
        else:
            continue  # corrupt fixtures — leave as uploaded
        req('PATCH', '/api/items/%s/media' % it['id'], {'metadata': m})
        print('  patched cbz -> %s' % m['title'])
    elif fmt in meta:
        body = {'metadata': meta[fmt]}
        if fmt in tags:
            body['tags'] = tags[fmt]
        req('PATCH', '/api/items/%s/media' % it['id'], body)
        print('  patched %s ebook -> %s' % (fmt, meta[fmt]['title']))
PY

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
if [ "$EXPECT" = 22 ]; then
    echo "Ebook fixtures: epub, pdf, cbz, cbz (oversized), cbz (corrupt), cbz (bad page), cbr."
else
    echo "Ebook fixtures: epub, pdf, cbz, cbz (oversized), cbz (corrupt), cbz (bad page) (cbr seeded only when the rar tool is present)."
fi
