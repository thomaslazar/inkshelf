# Item detail page — design

## Problem

There is no per-item page. The listing/search/converted rows are deliberately
lean (primary file's download + convert + read toggle), so the full metadata ABS
holds — genres, tags, description, narrators, publisher, multiple series/authors
— is never shown, and only the *primary* ebook file is reachable. An item with,
say, a PDF + EPUB + CBZ exposes only one of them.

## Goal

A per-item page at `/item/{id}` showing the full metadata with a larger cover,
every downloadable ebook file (each with its own download link and, for cbz/cbr,
a Convert-to-EPUB action), genre/tag/narrator jump-off links into a filtered
listing, and the read/unread toggle. The listing rows stay lean; the detail page
is the one place that exposes every file.

## Scope decisions (settled in brainstorming)

- **Entry point:** the row **title and cover** (in the shared `_ItemRow`) link to
  `/item/{id}`, across listing, search, and the converted view.
- **Per-file convert:** each cbz/cbr ebook file gets its own Convert action (the
  primary could be a non-convertible pdf), *except* the primary file uses the
  existing no-ino path so it shares the listing's cache entry (see below).
- **Multiple series/authors/narrators** are all arrays and render as lists of
  individual facet links.
- **Description** is shown as ABS's `descriptionPlain` (HTML-stripped) — safe on
  old e-reader browsers, no injection.
- **Read state** reuses the shared `/api/me` finished-set.

## Data source

`GET /api/items/{id}?expanded=1` (`toOldJSONExpanded`) supplies everything:

- `metadata`: title, subtitle, `authors[] {id,name}`, `series[] {id,name,sequence}`
  (many-to-many → possibly several), `narrators[]` (string names), `genres[]`
  (string names), publisher, publishedYear, language, description +
  `descriptionPlain`.
- media-level: `tags[]` (string names), `coverPath`, `ebookFile` (the **primary**,
  carrying an `ino`).
- item-level: `libraryId`, `libraryFiles[]` — each with `ino`, `fileType`, and
  `metadata` (filename, ext, size, mtimeMs). The downloadable ebooks are the
  entries with `fileType == "ebook"`.

`GetItemDetailAsync` will request `?expanded=1` (additive — it already returns
`AbsItemDetail`; `ConvertService`, which uses it for the primary ebook, keeps
working since expanded still carries `media.ebookFile`/`metadata`).

## The page (`/item/{id}` — new `Item.cshtml` + `ItemModel`)

Injects `AbsApiClient`, `EpubCache`, `ConvertQueue`. `OnGetAsync`:

1. Fetch expanded detail; 404 → `NotFound`. An expired session lets
   `AbsAuthException` propagate → `/login` (existing middleware).
2. Compute the current `RenderTarget` (scr + settings), as elsewhere.
3. Read the finished-set (`GetFinishedItemIdsAsync`) → read flag for this id.
4. Build the metadata view-model and the formats list (below).

### Metadata block
Larger cover (`/cover/{id}` at a bigger width, e.g. ~240px — still bounded by the
existing `/cover` cap of 400). Title (+ subtitle), then **lists** of author,
series (with `#sequence`), and narrator links, plus publisher/year/language,
genres, and tags. Author/series link by **id**; narrator/genre/tag link by
**name** — all via the existing `?filter=` facet path (`AbsFilter.Encode(group,
value)` → `/library/{libraryId}?filter=…`), built through a per-item
`LibraryLinks(libraryId, …)`. Description shown as `descriptionPlain` below.

### Formats list
For each `libraryFiles[]` entry with `fileType == "ebook"`:

- **Download** link → `/download/{id}?file={ino}` (proxied; filename from the
  file's metadata). The **primary** file (its `ino == media.ebookFile.ino`) uses
  plain `/download/{id}` (no `file`), i.e. the existing behavior.
- If the file's format is **cbz/cbr**, also a **Convert action**
  (Convert / Converting… / retry / EPUB ✓ + ↻ regen), rendered by a shared
  `_ConvertAction` partial (see below). The **primary** cbz/cbr targets plain
  `/convert/{id}` (no `file`); a **non-primary** cbz/cbr targets
  `/convert/{id}?file={ino}`. Both carry `return=/item/{id}`.

### Read toggle
The existing `/read/{id}` form, identical to the rows.

## The cache key never contains the ino

The cache scheme is unchanged: `{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}[-g].epub`.
The only choice is **which file's** size+mtime to use:

- **Primary** cbz/cbr → the primary ebook's size+mtime. This is *byte-identical*
  to the key the listing/converted view already write, so a comic converted from
  the listing shows **EPUB ✓** on the detail page (and vice-versa). This is the
  load-bearing requirement: the primary must never be looked up under an
  ino-derived key, or it would miss the existing cached file.
- **Non-primary** cbz/cbr → that file's own size+mtime (a distinct key; different
  files have distinct size+mtime, so no collision — the ino is not needed in the
  key).

The `ino` is used only to (a) download the correct non-primary file's bytes and
(b) label per-file downloads — never in the cache key.

### Per-file convert-state
`ConvertRowStateResolver` gains a lower-level overload
`ResolveFor(string itemId, long size, long mtimeMs, string? fmt, RenderTarget
target, EpubCache cache, ConvertQueue queue)` that does the format check +
`PathFor` + `queue.Status` mapping. The existing
`Resolve(item, media, …)` becomes a thin wrapper that extracts fmt/size/mtime and
calls it (listing behavior unchanged). The detail page calls `ResolveFor` per
cbz/cbr file — for the primary, with the primary's size+mtime (→ matches the
listing).

## Per-file convert / download plumbing

- **`/convert/{id}?file={ino}`** and **`/download/{id}?file={ino}`** — `file` is
  optional; **absent = primary**, so all existing links are unchanged.
- `ConvertService.ResolveAsync` accepts an optional file ino: when present, it
  locates that `libraryFile` (needs `libraryFiles` from the expanded detail),
  reads its filename/size/mtime/format, and validates cbz/cbr; when absent, the
  primary as today. Cache path from the resolved file's size+mtime.
- `ConvertJob` gains a nullable `FileIno`. `AbsDownloadClient.DownloadEbookAsync`
  gains an optional `fileIno` → `GET /api/items/{id}/ebook/{ino}` when set, else
  `/api/items/{id}/ebook` (primary).
- `AbsApiClient` gets a per-file ebook stream (`/api/items/{id}/ebook/{ino}`) for
  the download endpoint; `DownloadEndpoints` resolves the download filename from
  the matching libraryFile.

## Shared `_ConvertAction` partial

Extract the convert-action `<span>` (the 4 states + the plain regen ↻ link) out
of `_ItemRow` into `_ConvertAction.cshtml`, taking `(Id, FileIno?, State,
ReturnUrl)` and building the `/convert/{id}[?file=…]&return=…` hrefs. `_ItemRow`
renders it for the primary (`FileIno = null`); the detail formats list renders it
per cbz/cbr file. This keeps the load-bearing "regen stays a plain link (no
data-warm)" rule in one place — the existing `ListingRenderTests` guard it.

## Genre / tag / narrator filter labelling

These facets filter by **name** (their value *is* the display name), unlike
authors/series (by id). `LibraryModel` currently resolves the facet name from the
fetched batch (id → name) for series/authors only. Extend it so that for
`genres`/`tags`/`narrators` the label is the decoded facet value directly, and
`Humanize` maps them to "Genre"/"Tag"/"Narrator". No server-side change — ABS
already accepts these filter groups (verified against v2.35.1).

## Layout (defensive CSS — no flex `gap`, no `object-fit`)

```
Libraries › {Library} › {Title}                         ⚙
┌──────────┐  Title  (Subtitle)
│  cover   │  Authors:   [Author One], [Author Two]
│ (bigger) │  Series:    [The Sandman #3], [Endless Nights #1]
│          │  Narrators: [Narrator A], [Narrator B]
└──────────┘  Publisher, 2021 · English
              Genres: [Fantasy] [Horror]
              Tags:   [owned] [favorite]
[✓ Read] / [Mark read]

Description…

Files
  My Comic.cbz   Download   Convert / EPUB ✓ ↻
  My Comic.pdf   Download
  My Comic.epub  Download
```

## Edge cases

- **No ebook files** (audio-only or empty): show the metadata + read toggle, and
  a "No downloadable files" note in the Files section.
- **Item deleted / not found**: `NotFound`.
- **Missing metadata fields** (no series/narrator/publisher/description): omit
  that line rather than show an empty label.
- **Converted view still dedupes by item id**, so multiple converted files for
  one item collapse to one row there — rare, unchanged, noted.

## Non-goals (v1)

- No in-browser reader; no metadata editing; no audio files (ebooks only).
- No change to the cache-key format.
- The `/converted` view is not reworked to show per-file conversions.

## New / changed units

- **DTOs** (`AbsModels.cs`): expand `AbsItemDetail` (`libraryId`,
  `libraryFiles[]`), `AbsDetailMedia` (`tags[]`, `coverPath`, `ebookFile.ino`),
  `AbsDetailMetadata` (subtitle, `series[]` already there, `narrators[]`,
  `genres[]`, publisher, publishedYear, language, descriptionPlain); new
  `AbsLibraryFile` (`ino`, `fileType`, metadata).
- **`AbsApiClient`**: `?expanded=1` on detail; per-ino ebook stream.
- **`ConvertService` / `ConvertJob` / `AbsDownloadClient` / `ConvertEndpoints` /
  `DownloadEndpoints`**: optional `file` ino.
- **`ConvertRowStateResolver`**: `ResolveFor(...)` overload.
- **`_ConvertAction.cshtml`** (new partial); `_ItemRow.cshtml` uses it + adds the
  title/cover link.
- **`Item.cshtml` + `ItemModel`** (new page).
- **`LibraryModel`**: name-valued facet labels (genres/tags/narrators).

## Testing

- ABS client: detail request carries `expanded=1`; parses libraryFiles, genres,
  tags, narrators, multiple series/authors; per-ino ebook stream hits
  `/api/items/{id}/ebook/{ino}`.
- `ConvertService`: `file={ino}` resolves the named libraryFile's size/mtime and
  validates cbz/cbr; absent → primary (existing tests stay green). The primary's
  computed cache path equals the listing's for the same item+device.
- `ConvertRowStateResolver.ResolveFor`: cached/converting/failed/convert/
  not-convertible per (size, mtime, fmt); `Resolve` still delegates unchanged.
- `_ConvertAction`/`_ItemRow`: regen ↻ stays a plain link; primary uses no
  `file=`; a non-primary uses `?file={ino}`.
- Detail render (WebApplicationFactory, stubbed ABS): metadata block shows
  multiple authors/series/narrators as facet links; genres/tags link to the
  library filter; each ebook file has a download link; a cbz shows Convert;
  the primary cbz that is already cached shows **EPUB ✓** (same key as the
  listing); read toggle present; title/cover in a row link to `/item/{id}`.
- `LibraryModel`: a `?filter=genres.<b64>` shows "Genre: <name>" and filters.
- Full suite (`dotnet test`) stays green.
