# UI polish + convenience features — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Integrate the logo/favicon assets, add a favorite-library shortcut, tighten the item listing (10/page, top pager, clickable author/series), and add per-library grouped search.

**Architecture:** The library page (`/library/{id}`) gains three modes chosen by query params — default paged listing, `?q=` grouped search, `?filter=<group>.<b64>` filtered listing. Author/series become filter links everywhere. Favorite library is a plain cookie with an auto-redirect from `/`. Logo/favicon are static assets wired into the layout and README.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 10), static assets, `System.Text.Json`. Near-zero JS: `<form>`/`<a>` only.

## Global Constraints

- Near-zero JavaScript — plain `<form>`/`<a>`/`<img>` only.
- Page size **10**; pager at the **top** only.
- Search limit **25**, not paged; per-library (`GET /api/libraries/{id}/search?q=`).
- Filter encoding: `<group>.<base64(id)>` where base64 = `Convert.ToBase64String(Encoding.UTF8.GetBytes(id))`; URL-encode the whole value when it goes into a link href or the ABS query.
- Items list drops `minified=1` (to get author/series ids). No download/ebook features in this iteration (scrapped earlier; out of scope).
- E-ink uses the **black** logo/icon; inverted variants only for GitHub dark mode.
- No private-infra references. Conventional Commits; no attribution trailers.

## ABS shapes (verified @ v2.35.1)

- Search `GET /api/libraries/{id}/search?q=&limit=` →
  `{ "book": [{ "libraryItem": <expanded item> }], "series": [{ "series": { "id","name" } }], "authors": [{ "id","name","numBooks" }], ... }`.
- Full item (no `minified`): `media.metadata.authors: [{id,name}]`,
  `media.metadata.series: [{id,name,sequence}]` (other full-item fields ignored).

---

## Task 0: Transient seeded ABS (integration harness) — do this first

**Files:**
- Create: `docker/docker-compose.yml`, `docker/seed.sh`, `docker/smoke-test.sh`
- Modify: `.gitignore` is unaffected (compose/seed/smoke are committed; `temp/` already ignored).

**Interfaces:**
- Produces a disposable ABS at host port **13379** with a seeded book library
  (~15 items across several authors/series) and root `root`/`root`; and
  `docker/smoke-test.sh` driving Inkshelf's routes.
- **Isolation:** compose project `inkshelf-it`, port 13379, prefixed volumes —
  never touches abs-cli's stack (project `docker`, port 13378).

- [ ] **Step 1: `docker/docker-compose.yml`**

```yaml
name: inkshelf-it
services:
  audiobookshelf:
    image: advplyr/audiobookshelf:2.35.1
    ports:
      - "13379:80"
    volumes:
      - abs-config:/config
      - abs-metadata:/metadata
    environment:
      - PORT=80
      # Disposable test stack: disable auth rate limiting so repeated logins
      # during dev/smoke don't hit ABS's 40-logins/10min cap.
      - RATE_LIMIT_AUTH_MAX=0

volumes:
  abs-config:
  abs-metadata:
```

- [ ] **Step 2: `docker/seed.sh`** (uploads items via EPUB media; adapted from abs-cli's seed)

```bash
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
```

- [ ] **Step 3: `docker/smoke-test.sh`** (drives Inkshelf's routes)

```bash
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
check "/library/$LIBRARY_ID" "Items"
check "/library/$LIBRARY_ID?q=Dune" "Dune"

FILTER=$(python3 -c "import base64;print('series.'+base64.b64encode(b'x').decode())")  # placeholder; real id below
# real series filter: fetch a series id
SERIES_FILTER=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/search?q=Mistborn&limit=5" -H "Authorization: Bearer $TOKEN" \
    | python3 -c "import sys,json,base64,urllib.parse;s=json.load(sys.stdin)['series'][0]['series']['id'];print(urllib.parse.quote('series.'+base64.b64encode(s.encode()).decode()))")
check "/library/$LIBRARY_ID?filter=$SERIES_FILTER" "clear"

code=$(curl -s -o /dev/null -w "%{http_code}" -b "$JAR" "$INKSHELF_URL/cover/$ITEM_ID")
[ "$code" = "200" ] || fail "GET /cover/$ITEM_ID expected 200 got $code"
echo "  ok: /cover/$ITEM_ID ($code)"
rm -f "$JAR"
echo "SMOKE PASS"
```

- [ ] **Step 4: Bring it up and seed** (from the devcontainer)

```bash
cd /workspaces/inkshelf
chmod +x docker/seed.sh docker/smoke-test.sh
docker compose -f docker/docker-compose.yml up -d
# host.docker.internal is unreliable in the dev container — resolve the IP:
ABS_IP=$(docker inspect inkshelf-it-audiobookshelf-1 -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')
echo "seeded ABS at http://$ABS_IP:80"
ABS_URL="http://$ABS_IP:80" bash docker/seed.sh
```

Record `ABS_IP` — subsequent tasks run Inkshelf with `ABS_URL=http://$ABS_IP:80`.

- [ ] **Step 5: Commit**

```bash
git add docker/
git commit -m "test: transient seeded ABS + smoke harness (isolated from abs-cli)"
```

---

## Task 1: Static assets + favicon + manifest

**Files:**
- Create under `src/Inkshelf/wwwroot/`: `favicon.ico`, `favicon-16x16.png`, `favicon-32x32.png`, `apple-touch-icon.png`, `android-chrome-192x192.png`, `android-chrome-512x512.png`, `site.webmanifest`.
- Create under `src/Inkshelf/wwwroot/img/`: `logo-black.png`, `logo-inverted.png`, `icon-black.png`, `icon-inverted.png`.
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml` (head links).

- [ ] **Step 1: Copy assets from `temp/logo`**

```bash
cd /workspaces/inkshelf
mkdir -p src/Inkshelf/wwwroot/img
cp temp/logo/favicon_io/favicon.ico src/Inkshelf/wwwroot/favicon.ico
cp temp/logo/favicon_io/favicon-16x16.png src/Inkshelf/wwwroot/
cp temp/logo/favicon_io/favicon-32x32.png src/Inkshelf/wwwroot/
cp temp/logo/favicon_io/apple-touch-icon.png src/Inkshelf/wwwroot/
cp temp/logo/favicon_io/android-chrome-192x192.png src/Inkshelf/wwwroot/
cp temp/logo/favicon_io/android-chrome-512x512.png src/Inkshelf/wwwroot/
cp temp/logo/favicon_io/site.webmanifest src/Inkshelf/wwwroot/
cp "temp/logo/inkshelf logo black.png" src/Inkshelf/wwwroot/img/logo-black.png
cp "temp/logo/inkshelf logo inverted.png" src/Inkshelf/wwwroot/img/logo-inverted.png
cp "temp/logo/inkshelf icon black.png" src/Inkshelf/wwwroot/img/icon-black.png
cp "temp/logo/inkshelf icon inverted.png" src/Inkshelf/wwwroot/img/icon-inverted.png
```

- [ ] **Step 2: Fill the manifest name**

Edit `src/Inkshelf/wwwroot/site.webmanifest` so `"name"` and `"short_name"` are `"Inkshelf"` (they are empty from the generator). Leave icons/colors as-is.

- [ ] **Step 3: Wire `<head>` in `_Layout.cshtml`**

Add inside `<head>` (after the existing `<title>`/stylesheet):

```html
<link rel="icon" type="image/png" sizes="32x32" href="~/favicon-32x32.png" />
<link rel="icon" type="image/png" sizes="16x16" href="~/favicon-16x16.png" />
<link rel="icon" href="~/favicon.ico" />
<link rel="apple-touch-icon" href="~/apple-touch-icon.png" />
<link rel="manifest" href="~/site.webmanifest" />
<meta name="theme-color" content="#ffffff" />
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Inkshelf`
Expected: builds clean. (Static files served from `wwwroot`; already have `UseStaticFiles`.)

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/wwwroot
git commit -m "feat: add logo, favicon set, and web manifest"
```

---

## Task 2: Layout header + login wordmark + CSS

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml`
- Modify: `src/Inkshelf/Pages/Login.cshtml`
- Modify: `src/Inkshelf/wwwroot/app.css`

**Interfaces:**
- Produces: a header partial rendered on authenticated pages with `[icon] Libraries`; login page shows the wordmark. Individual pages add their own page-specific header bits (favorite star, search) — see later tasks.

- [ ] **Step 1: Header markup in `_Layout.cshtml`**

Wrap `@RenderBody()` so a header shows when a section opts in. Simplest: add a header block that every authenticated page includes via the layout, but the login page hides it. Use a `RenderSection("header", required: false)` OR a simple convention: put the icon+Libraries link directly in the layout, shown always except login. Since the login page is the only anonymous page, render the header unconditionally in the layout EXCEPT wrap it in a check on a `ViewData["HideChrome"]` flag that Login sets.

In `_Layout.cshtml`, before `@RenderBody()`:

```html
@if (!(ViewData["HideChrome"] as bool? ?? false))
{
    <header class="topbar">
        <a href="/?all=1" class="brand"><img src="~/img/icon-black.png" alt="Inkshelf" height="24" /> Libraries</a>
        @await RenderSectionAsync("topbar", required: false)
    </header>
}
```

- [ ] **Step 2: Login wordmark + hide chrome**

In `Login.cshtml`, at the top:

```html
@{ ViewData["HideChrome"] = true; }
<p class="logo"><img src="~/img/logo-black.png" alt="Inkshelf" /></p>
```

(Place the `<img>` above the existing `<h1>`/form; you may drop the `<h1>Inkshelf</h1>` now that the wordmark is shown.)

- [ ] **Step 3: CSS**

Append to `app.css`:

```css
.topbar { display: flex; align-items: center; gap: 1rem; padding-bottom: .5rem; border-bottom: 1px solid #ccc; margin-bottom: 1rem; flex-wrap: wrap; }
.topbar .brand { display: inline-flex; align-items: center; gap: .4rem; font-weight: bold; text-decoration: none; }
.logo { text-align: center; }
.logo img { max-width: 320px; width: 60%; height: auto; }
.searchbar { margin-left: auto; }
.searchbar input { padding: .2rem; }
```

- [ ] **Step 4: Build + suite**

Run: `dotnet build src/Inkshelf` then `dotnet test tests/Inkshelf.Tests`
Expected: clean; 23 pass.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_Layout.cshtml src/Inkshelf/Pages/Login.cshtml src/Inkshelf/wwwroot/app.css
git commit -m "feat: header with logo and login wordmark"
```

---

## Task 3: README logo

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Centered themed logo at the top**

Insert directly under the `# Inkshelf` heading (or replacing it):

```html
<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/Inkshelf/wwwroot/img/logo-inverted.png">
    <img src="src/Inkshelf/wwwroot/img/logo-black.png" alt="Inkshelf" width="360">
  </picture>
</p>
```

Keep the existing description paragraph below it.

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add logo to the README"
```

---

## Task 4: Models + AbsClient (full items, filter, search, encode)

**Files:**
- Modify: `src/Inkshelf/Abs/AbsModels.cs`
- Modify: `src/Inkshelf/Abs/AbsClient.cs`
- Create: `src/Inkshelf/Abs/AbsFilter.cs`
- Modify: `tests/Inkshelf.Tests/AbsClientTests.cs`
- Create: `tests/Inkshelf.Tests/AbsFilterTests.cs`

**Interfaces:**
- Produces:
  - `AbsFilter.Encode(string group, string id)` → `"<group>.<base64>"`.
  - `AbsMetadata` gains `Authors: List<AbsRef>` and `Series: List<AbsSeriesRef>`.
  - `AbsClient.GetItemsAsync(token, libId, page, limit, string? filter = null)`.
  - `AbsClient.SearchAsync(token, libId, q, limit)` → `AbsSearchResults`.
- Consumes: existing `SendAuthedAsync`.

- [ ] **Step 1: Write failing tests (AbsFilterTests)**

`tests/Inkshelf.Tests/AbsFilterTests.cs`:

```csharp
using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsFilterTests
{
    [Fact]
    public void Encode_produces_group_dot_base64()
    {
        // base64("auth-1") = "YXV0aC0x"
        Assert.Equal("authors.YXV0aC0x", AbsFilter.Encode("authors", "auth-1"));
    }
}
```

- [ ] **Step 2: Write failing tests (append to AbsClientTests)**

```csharp
[Fact]
public async Task GetItemsAsync_full_metadata_parses_authors_and_series()
{
    var h = new StubHandler(_ => StubHandler.Json(
        """{"results":[{"id":"i1","media":{"metadata":{"title":"Dune","authors":[{"id":"a1","name":"Herbert"}],"series":[{"id":"s1","name":"Dune","sequence":"1"}]}}}],"total":1,"limit":10,"page":0}"""));
    var page = await Client(h).GetItemsAsync("acc", "lib1", 0, 10);
    var m = page.Results[0].Media!.Metadata!;
    Assert.Equal("a1", m.Authors![0].Id);
    Assert.Equal("s1", m.Series![0].Id);
    // no minified param
    Assert.DoesNotContain("minified", h.Last!.RequestUri!.Query);
}

[Fact]
public async Task GetItemsAsync_appends_filter_when_set()
{
    var h = new StubHandler(_ => StubHandler.Json("""{"results":[],"total":0,"limit":10,"page":0}"""));
    await Client(h).GetItemsAsync("acc", "lib1", 0, 10, filter: "series.czE=");
    var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
    Assert.Equal("series.czE=", q["filter"]);
}

[Fact]
public async Task SearchAsync_parses_groups()
{
    var h = new StubHandler(_ => StubHandler.Json(
        """{"book":[{"libraryItem":{"id":"i1","media":{"metadata":{"title":"Dune"}}}}],"series":[{"series":{"id":"s1","name":"Dune"}}],"authors":[{"id":"a1","name":"Herbert","numBooks":6}]}"""));
    var r = await Client(h).SearchAsync("acc", "lib1", "dune", 25);
    Assert.Equal("Dune", r.Book[0].LibraryItem.Media!.Metadata!.Title);
    Assert.Equal("s1", r.Series[0].Series.Id);
    Assert.Equal("Herbert", r.Authors[0].Name);
    Assert.Equal("/api/libraries/lib1/search", h.Last!.RequestUri!.AbsolutePath);
    var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
    Assert.Equal("dune", q["q"]);
    Assert.Equal("25", q["limit"]);
}
```

- [ ] **Step 3: Run, verify fail**

Run: `dotnet test tests/Inkshelf.Tests --filter "AbsFilterTests|AbsClientTests"`
Expected: FAIL (types/members missing).

- [ ] **Step 4: Implement `AbsFilter.cs`**

```csharp
using System.Text;

namespace Inkshelf.Abs;

public static class AbsFilter
{
    // ABS decodes filterBy as "<group>.<base64(value)>".
    public static string Encode(string group, string id) =>
        $"{group}.{Convert.ToBase64String(Encoding.UTF8.GetBytes(id))}";
}
```

- [ ] **Step 5: Extend `AbsModels.cs`**

Replace the `AbsMedia`/`AbsMetadata` records and add refs + search DTOs:

```csharp
public record AbsItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("media")] AbsMedia? Media);

public record AbsMedia(
    [property: JsonPropertyName("metadata")] AbsMetadata? Metadata);

public record AbsMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName,
    [property: JsonPropertyName("authors")] List<AbsRef>? Authors = null,
    [property: JsonPropertyName("series")] List<AbsSeriesRef>? Series = null);

public record AbsRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public record AbsSeriesRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sequence")] string? Sequence = null);

// Search
public record AbsSearchResults(
    [property: JsonPropertyName("book")] List<AbsBookMatch> Book,
    [property: JsonPropertyName("series")] List<AbsSeriesMatch> Series,
    [property: JsonPropertyName("authors")] List<AbsRef> Authors);
public record AbsBookMatch(
    [property: JsonPropertyName("libraryItem")] AbsItem LibraryItem);
public record AbsSeriesMatch(
    [property: JsonPropertyName("series")] AbsSeriesRef Series);
```

(No ebook/download DTOs — that feature is not part of this iteration. Unmapped
full-item JSON fields like `ebookFile`/`audioFiles` are ignored by
`System.Text.Json`.)

- [ ] **Step 6: Extend `AbsClient.cs`**

Change items to drop `minified` and accept a filter; add search:

```csharp
public async Task<AbsItemsPage> GetItemsAsync(string accessToken, string libraryId,
    int page, int limit, string? filter = null, CancellationToken ct = default)
{
    var url = $"/api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit={limit}&page={page}";
    if (!string.IsNullOrEmpty(filter))
        url += $"&filter={Uri.EscapeDataString(filter)}";
    using var res = await SendAuthedAsync(HttpMethod.Get, url, accessToken, ct);
    return await res.Content.ReadFromJsonAsync<AbsItemsPage>(ct)
        ?? new AbsItemsPage(new(), 0, limit, page);
}

public async Task<AbsSearchResults> SearchAsync(string accessToken, string libraryId,
    string query, int limit, CancellationToken ct = default)
{
    var url = $"/api/libraries/{Uri.EscapeDataString(libraryId)}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
    using var res = await SendAuthedAsync(HttpMethod.Get, url, accessToken, ct);
    return await res.Content.ReadFromJsonAsync<AbsSearchResults>(ct)
        ?? new AbsSearchResults(new(), new(), new());
}
```

Note: `GetItemsAsync`'s signature adds `filter` before `ct`. Update the existing
call site in `Library.cshtml.cs` (Task 6 rewrites it anyway) and any test.

- [ ] **Step 7: Run tests, verify pass**

Run: `dotnet test tests/Inkshelf.Tests`
Expected: PASS. Fix any call-site/signature breaks (e.g. the existing
`GetItemsAsync_builds_query_and_parses` test asserted `minified=1` — update it
to assert no `minified` and keep `filter` absent).

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Abs tests/Inkshelf.Tests/AbsFilterTests.cs tests/Inkshelf.Tests/AbsClientTests.cs
git commit -m "feat: full item metadata, filter param, and library search in AbsClient"
```

---

## Task 5: Favorite library (cookie + endpoint + redirect)

**Files:**
- Create: `src/Inkshelf/Favorites.cs` (small helper reading/writing the cookie)
- Modify: `src/Inkshelf/Program.cs` (POST `/favorite` endpoint)
- Modify: `src/Inkshelf/Pages/Index.cshtml.cs` (auto-redirect)
- Modify: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Produces:
  - `Favorites` static helpers: `string? Read(HttpRequest)`, `void Set(HttpResponse, string id)`, `void Clear(HttpResponse)`; cookie `inkshelf_fav_library`.
  - `POST /favorite` (antiforgery-validated) toggles the favorite for a posted `libraryId`, redirects to `/library/{id}`.
  - `GET /` redirects to `/library/{fav}` when the cookie is set and `all` query is absent.

- [ ] **Step 1: Write failing endpoint tests**

```csharp
[Fact]
public async Task Index_without_favorite_shows_list_or_login()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    var res = await client.GetAsync("/");
    // No fav cookie, no session -> AbsAuthException -> redirect to /login
    Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
    Assert.Equal("/login", res.Headers.Location?.OriginalString);
}

[Fact]
public async Task Index_with_favorite_redirects_to_that_library()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    var req = new HttpRequestMessage(HttpMethod.Get, "/");
    req.Headers.Add("Cookie", "inkshelf_fav_library=lib9");
    var res = await client.SendAsync(req);
    Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
    Assert.Equal("/library/lib9", res.Headers.Location?.OriginalString);
}

[Fact]
public async Task Index_with_favorite_and_all_bypasses_redirect()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    var req = new HttpRequestMessage(HttpMethod.Get, "/?all=1");
    req.Headers.Add("Cookie", "inkshelf_fav_library=lib9");
    var res = await client.SendAsync(req);
    // Bypasses fav redirect; with no session it falls through to /login
    Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
    Assert.Equal("/login", res.Headers.Location?.OriginalString);
}
```

- [ ] **Step 2: Run, verify fail** (`--filter EndpointTests`) — the favorite cases fail.

- [ ] **Step 3: Implement `Favorites.cs`**

```csharp
namespace Inkshelf;

public static class Favorites
{
    public const string Cookie = "inkshelf_fav_library";

    public static string? Read(HttpRequest req) =>
        req.Cookies.TryGetValue(Cookie, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static void Set(HttpResponse res, string id) =>
        res.Cookies.Append(Cookie, id, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = res.HttpContext.Request.IsHttps,
            IsEssential = true, Path = "/", MaxAge = TimeSpan.FromDays(365)
        });

    public static void Clear(HttpResponse res) =>
        res.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
}
```

- [ ] **Step 4: Index auto-redirect**

In `Index.cshtml.cs`, change `OnGetAsync` to redirect when a favorite is set and `all` is absent:

```csharp
public async Task<IActionResult> OnGetAsync([FromQuery] string? all, CancellationToken ct)
{
    var fav = Favorites.Read(Request);
    if (fav is not null && string.IsNullOrEmpty(all))
        return Redirect($"/library/{fav}");
    Libraries = await _session.ExecuteAsync((tok, c) => _client.GetLibrariesAsync(tok, c), ct);
    return Page();
}
```

(Add `using Inkshelf;` and `using Microsoft.AspNetCore.Mvc;` as needed. `Libraries` stays a property.)

- [ ] **Step 5: `POST /favorite` endpoint in `Program.cs`**

After the `/logout` endpoint:

```csharp
app.MapPost("/favorite", async (HttpContext ctx, IAntiforgery antiforgery, string libraryId) =>
{
    try { await antiforgery.ValidateRequestAsync(ctx); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    if (Favorites.Read(ctx.Request) == libraryId) Favorites.Clear(ctx.Response);
    else Favorites.Set(ctx.Response, libraryId);
    return Results.Redirect($"/library/{libraryId}");
});
```

`libraryId` binds from the form post. Add `using Inkshelf;` to `Program.cs` if needed.

- [ ] **Step 6: Run tests, verify pass** (`--filter EndpointTests`), then full suite.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Favorites.cs src/Inkshelf/Program.cs src/Inkshelf/Pages/Index.cshtml.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: favorite library cookie with auto-redirect"
```

---

## Task 6: Library page — 10/page, top pager, clickable author/series, search modes, favorite star

**Files:**
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml`

**Interfaces:**
- Consumes: `AbsClient.GetItemsAsync(...,filter)`, `AbsClient.SearchAsync`, `AbsFilter.Encode`, `Favorites`.
- Produces: three-mode library page.

- [ ] **Step 1: Rewrite `Library.cshtml.cs`**

```csharp
using Inkshelf;
using Inkshelf.Abs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class LibraryModel : PageModel
{
    public const int PageSize = 10;
    public const int SearchLimit = 25;
    private readonly AbsSession _session;
    private readonly AbsClient _client;
    public LibraryModel(AbsSession session, AbsClient client) { _session = session; _client = client; }

    [FromRoute] public string Id { get; set; } = "";
    [FromQuery] public string? Q { get; set; }
    [FromQuery] public string? Filter { get; set; }

    public bool IsFavorite { get; private set; }
    public bool IsSearch => !string.IsNullOrWhiteSpace(Q);

    public List<AbsItem> Items { get; private set; } = new();
    public Pager Pager { get; private set; } = new(0, PageSize, 0);
    public AbsSearchResults? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        IsFavorite = Favorites.Read(Request) == Id;

        if (IsSearch)
        {
            Search = await _session.ExecuteAsync((tok, c) => _client.SearchAsync(tok, Id, Q!.Trim(), SearchLimit, c), ct);
        }
        else
        {
            var zeroPage = Math.Max(0, page - 1);
            var result = await _session.ExecuteAsync(
                (tok, c) => _client.GetItemsAsync(tok, Id, zeroPage, PageSize, Filter, c), ct);
            Items = result.Results;
            Pager = new Pager(result.Page, result.Limit <= 0 ? PageSize : result.Limit, result.Total);
        }
        return Page();
    }

    public string AuthorHref(string authorId) => $"/library/{Id}?filter={Uri.EscapeDataString(AbsFilter.Encode("authors", authorId))}";
    public string SeriesHref(string seriesId) => $"/library/{Id}?filter={Uri.EscapeDataString(AbsFilter.Encode("series", seriesId))}";
}
```

- [ ] **Step 2: Rewrite `Library.cshtml`**

```html
@page
@model Inkshelf.Pages.LibraryModel
@section topbar {
    <form class="searchbar" method="get" action="/library/@Model.Id">
        <input type="search" name="q" value="@Model.Q" placeholder="Search…" />
        <button type="submit">Search</button>
    </form>
}

@{
    void Row(Inkshelf.Abs.AbsItem item)
    {
        var m = item.Media?.Metadata;
        <div class="item">
            <img src="/cover/@item.Id?w=120" alt="" width="60" />
            <div>
                <strong>@(m?.Title ?? "(untitled)")</strong><br />
                @if (m?.Authors is { Count: > 0 })
                {
                    @for (var i = 0; i < m.Authors.Count; i++)
                    {
                        if (i > 0) { <text>, </text> }
                        <a href="@Model.AuthorHref(m.Authors[i].Id)">@m.Authors[i].Name</a>
                    }
                }
                @if (m?.Series is { Count: > 0 })
                {
                    foreach (var s in m.Series)
                    {
                        <text> · </text><a href="@Model.SeriesHref(s.Id)">@s.Name@(string.IsNullOrEmpty(s.Sequence) ? "" : $" #{s.Sequence}")</a>
                    }
                }
            </div>
        </div>
    }
}

<h1>Items</h1>

@if (Model.IsSearch)
{
    <p>Results for "<strong>@Model.Q</strong>" · <a href="/library/@Model.Id">clear</a></p>
    var books = Model.Search?.Book ?? new();
    var series = Model.Search?.Series ?? new();
    var authors = Model.Search?.Authors ?? new();
    if (books.Count == 0 && series.Count == 0 && authors.Count == 0) { <p>No matches.</p> }
    @if (books.Count > 0)
    {
        <h2>Books</h2>
        foreach (var b in books) { Row(b.LibraryItem); }
    }
    @if (series.Count > 0)
    {
        <h2>Series</h2>
        <ul>@foreach (var s in series) { <li><a href="@Model.SeriesHref(s.Series.Id)">@s.Series.Name</a></li> }</ul>
    }
    @if (authors.Count > 0)
    {
        <h2>Authors</h2>
        <ul>@foreach (var a in authors) { <li><a href="@Model.AuthorHref(a.Id)">@a.Name</a></li> }</ul>
    }
}
else
{
    @if (!string.IsNullOrEmpty(Model.Filter))
    {
        <p>Filtered · <a href="/library/@Model.Id">clear</a></p>
    }
    <nav class="pager">
        @if (Model.Pager.HasPrev) { <a href="/library/@Model.Id?page=@(Model.Pager.DisplayPage - 1)@(string.IsNullOrEmpty(Model.Filter) ? "" : $"&filter={System.Uri.EscapeDataString(Model.Filter)}")">&larr; Prev</a> }
        <span>Page @Model.Pager.DisplayPage of @Math.Max(1, Model.Pager.TotalPages)</span>
        @if (Model.Pager.HasNext) { <a href="/library/@Model.Id?page=@(Model.Pager.DisplayPage + 1)@(string.IsNullOrEmpty(Model.Filter) ? "" : $"&filter={System.Uri.EscapeDataString(Model.Filter)}")">Next &rarr;</a> }
    </nav>
    @if (Model.Items.Count == 0) { <p>No items.</p> }
    @foreach (var item in Model.Items) { Row(item); }
    <form method="post" action="/favorite">
        <input type="hidden" name="libraryId" value="@Model.Id" />
        <button type="submit">@(Model.IsFavorite ? "★ Favorited (unset)" : "☆ Set favorite")</button>
    </form>
}
```

(Note: local functions in Razor with markup use the `@{ void Row(...) { ... } }`
pattern; if the compiler is unhappy with markup inside a `@{}` local function,
convert `Row` into a partial `_ItemRow.cshtml` taking `AbsItem` and render with
`<partial name="_ItemRow" model="item" />`. Prefer the partial if it compiles
more cleanly.)

- [ ] **Step 3: Build**

Run: `dotnet build src/Inkshelf`
Expected: clean. If the local-function-with-markup doesn't compile, extract `_ItemRow.cshtml` partial and re-build.

- [ ] **Step 4: Full suite**

Run: `dotnet test tests/Inkshelf.Tests`
Expected: 23 + new (Task 4/5) all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Pages/Library.cshtml src/Inkshelf/Pages/Library.cshtml.cs src/Inkshelf/Pages/Shared 2>/dev/null
git commit -m "feat: library search, filters, top pager, favorite star, 10/page"
```

---

## Task 7: Integration verify (seeded ABS) → PR + CI → real-ABS smoke

- [ ] **Step 0: Verify against the seeded ABS (during/after implementation)**

With the seeded stack up (Task 0), run Inkshelf against it and drive the flows
in a browser, then run the automated smoke:

```bash
ABS_IP=$(docker inspect inkshelf-it-audiobookshelf-1 -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')
ABS_URL="http://$ABS_IP:80" ASPNETCORE_URLS=http://0.0.0.0:5099 dotnet run --project src/Inkshelf &
# then:
ABS_URL="http://$ABS_IP:80" INKSHELF_URL=http://localhost:5099 bash docker/smoke-test.sh
```

Browser check (login root/root): favicon; login wordmark; header icon →
libraries; set a favorite → `/` jumps into it → Libraries link escapes; rows
show clickable authors/series that filter; `?q=Dune` returns grouped
Books/Series/Authors; 10/page with a top pager. Expect **SMOKE PASS**.

- [ ] **Step 1: Push + PR**

```bash
git push -u origin feat/ui-convenience
gh pr create --base main --title "feat: UI polish + convenience (logo, favorite, search, filters)" \
  --body "Logo/favicon, favorite-library shortcut, 10/page top pager, clickable author/series, grouped per-library search. See docs/superpowers/specs/2026-07-13-ui-convenience-design.md."
```

- [ ] **Step 2: Watch CI** (`test` must pass; `image` skipped on PR).

```bash
RUN_ID=$(gh run list --branch feat/ui-convenience --limit 1 --json databaseId -q '.[0].databaseId')
gh run watch "$RUN_ID" --exit-status
```

- [ ] **Step 3: Final smoke against the real ABS**

After CI is green and seeded-ABS verification passed, run Inkshelf against the
user's real ABS instance and do a manual smoke of the same flows (login with
real credentials). This is the last gate before merge.

- [ ] **Step 4: Tear down the test stack**

```bash
docker compose -f docker/docker-compose.yml down -v   # removes the transient ABS + volumes
```

- [ ] **Step 5: Merge** (human) → `:main` image republishes.

---

## Self-Review notes

- **Spec coverage:** transient seeded ABS + smoke harness, isolated from abs-cli (T0), assets+favicon+manifest (T1), header+login wordmark (T2), README (T3), full-metadata/filter/search client + encode (T4), favorite cookie+redirect+endpoint (T5), 10/page+top pager+clickable author/series+search modes+star (T6), seeded-ABS verify + smoke + real-ABS smoke + teardown (T7). All covered.
- **Isolation:** T0 uses compose project `inkshelf-it`, port 13379, prefixed volumes — abs-cli's stack (project `docker`, port 13378) is untouched, so both run simultaneously.
- **Seeded ABS reachability:** resolve the container IP (`docker inspect`), not `host.docker.internal`, from the dev container.
- **`minified` removal:** the existing `GetItemsAsync_builds_query_and_parses` test asserts `minified=1` — Task 4 Step 7 updates it. Flagged so it isn't missed.
- **No download/ebook features** in this iteration (scrapped earlier). Dropping `minified=1` is solely to obtain author/series ids for clickable filters; unmapped full-item fields are ignored by the deserializer.
- **Razor row rendering:** if the local-function-with-markup form fights the compiler, fall back to a `_ItemRow.cshtml` partial (called out in T6).
- **Filter link encoding:** `Uri.EscapeDataString` on the whole `group.<b64>` value in hrefs and when passing to ABS; pager links carry the filter forward.
