# Sorting + ebook delivery (download & CBZ/CBR→EPUB) — Design

**Date:** 2026-07-13
**Status:** Approved — proceeding to plan/implementation on `feat/sort-and-download`

## Purpose

Three related additions, shipped in one PR:

1. **Sorting** the item listing (e-ink-friendly, composes with filters + paging).
2. **Download** the primary ebook file for any item that has one.
3. **Convert** CBZ/CBR comics to EPUB on the fly (ABS metadata embedded,
   disk-cached, force-regenerable) so they read on the Tolino.

Near-zero-JavaScript throughout (`<form>`/`<a>` only). Target browser is the
Tolino's Chrome-30/ES5 engine — see `docs/tolino-browser.md`.

## A. Sorting

A sort-links row at the top of the listing:

`Sort: Title · Author · Added` (plus `· Sequence` only when a series filter is active).

- Each field cycles on click: **off → ascending → descending → off**. Clicking a
  different field starts it at ascending. The active field shows `↑`/`↓`.
- Maps to ABS `?sort=<key>&desc=0|1`:
  - Title → `media.metadata.title`
  - Author → `media.metadata.authorNameLF`
  - Added → `addedAt`
  - Sequence → `sequence` (series-filtered view only)
- **Composition:** `sort`, `desc`, the active facet (`filter`/`author`/`series`),
  and `page` all coexist in the URL. Changing the sort resets to page 1 but
  keeps the facet. Default (no `sort`) = ABS default order.
- Search results are not sorted (ABS ranks them); the sort row shows only in the
  listing modes (default + filtered).

Each field link computes its own next-state href from the current `sort`/`desc`.

## B. Download primary ebook

**Link gating:** the `Download` (and `Convert`) links render **only when the
item has an ebook** — the listing JSON already carries `media.ebookFormat`, so
the row decides for free (no extra request). Items without an ebook (audio-only)
show no link at all.

The `Download` link → `GET /download/{id}`:

- Fetches item detail for the ebook file's real filename
  (`media.ebookFile.metadata.filename`), then proxies
  `GET /api/items/{id}/ebook` (no special ABS permission) and streams the bytes
  with `Content-Disposition: attachment; filename="<abs filename>"`.
- Works for every format (epub/pdf/cbz/cbr/mobi/…) — the primary file, as-is.
- **Defensive 404:** since the link is gated, users don't normally hit
  `/download/{id}` for an ebook-less item — but if the endpoint is hit directly
  (bookmark/stale link/audio-only id), it returns 404 rather than 500.

## C. CBZ/CBR → EPUB conversion

Items whose `ebookFormat` is `cbz` or `cbr` *additionally* get
**`Convert to epub`** (`GET /convert/{id}`) and a small **`↻`**
(`GET /convert/{id}?fresh=1`).

### Flow

1. Fetch item detail: title, author(s), series+sequence, and the ebook file's
   `size` + `mtimeMs` (+ `ebookFormat`).
2. Compute cache key `{itemId}-{size}-{mtimeMs}.epub`.
3. If `fresh=1`, delete any cached file for this item first.
4. Cache hit → stream the cached EPUB. Miss → generate (below), cache, stream.
5. Serve with `Content-Disposition: attachment; filename="<Author> - <Title>.epub"`
   (sanitized; guards against identical titles).

### Generation

- Download the archive via `GET /api/items/{id}/ebook`.
- Read entries with **SharpCompress** (handles ZIP *and* RAR/RAR5 in managed
  code — **no `rar`/`unrar` binary needed at runtime**). Select image entries
  (`.jpg/.jpeg/.png/.webp/.gif`), sort by entry name (natural/ordinal).
- For each page, read pixel dimensions with **SixLabors.ImageSharp**
  (`Image.Identify`, header-only). **WebP** pages are decoded and re-encoded to
  JPEG (the Tolino can't render WebP); JPEG/PNG pass through unchanged.
- Write a **fixed-layout (pre-paginated) EPUB** with `System.IO.Compression`:
  - `mimetype` first entry, **stored uncompressed**.
  - `META-INF/container.xml`.
  - `OEBPS/content.opf`: `dc:title`, `dc:creator` (author), EPUB3
    `rendition:layout = pre-paginated`; series via calibre meta
    (`calibre:series`, `calibre:series_index`) when present; manifest of images
    + one XHTML per page; spine in page order.
  - One XHTML per page with `<meta name="viewport" content="width=W, height=H">`
    (the image's pixels) and the image filling the page.
  - A minimal nav document (EPUB3 `toc`).

### Cache

- Path from config key **`CachePath`** (env `CachePath`), default
  `<ContentRoot>/.cache/epub`. Created at startup.
- **Persistence across container rebuilds:** `docker-compose.example.yml` mounts
  a named volume (or bind mount) at the cache path; documented in `README.md`.
  `.cache/` is gitignored.
- Files named `{itemId}-{size}-{mtimeMs}.epub`. `fresh=1` removes existing
  `{itemId}-*.epub` before regenerating.

### Behavior notes

- Generation is synchronous: the first convert of a large volume takes tens of
  seconds (request waits, then streams); cached thereafter. No progress UI
  (near-zero-JS) — acceptable for a download action.
- Errors (corrupt archive, no images) → a plain error response; the `↻` lets the
  user retry a fresh build.

## Deferred (later pass, per `docs/ideas/cbz-to-epub-manga.md`)

- Grayscale conversion, double-page spread splitting.
- Any non-Tolino delivery, reading in-browser, progress reporting.

## Dependencies (NuGet)

- `SharpCompress` — read ZIP + RAR archives (CBZ/CBR) in managed code.
- `SixLabors.ImageSharp` — image dimensions + WebP→JPEG transcode.
- EPUB writing uses `System.IO.Compression` (framework, no package).

## Endpoints (added to `Program.cs`)

- `GET /download/{id}` — proxy the primary ebook file (any format).
- `GET /convert/{id}[?fresh=1]` — CBZ/CBR → cached EPUB.

## Client / model additions

- `AbsMedia` already exposes `Metadata` + `CoverPath`; add nothing for the row
  gating beyond the existing `ebookFormat` (fetched where needed).
- `AbsClient`:
  - `GetItemDetailAsync(token, id)` → title, authors, series (+sequence),
    ebook `{ format, filename, size, mtimeMs }` (from `GET /api/items/{id}`).
  - `GetEbookStreamAsync(token, id)` → `(Stream, contentType, ...)` for
    `GET /api/items/{id}/ebook` (reused by download + convert).
- `GetItemsAsync` gains `sort`/`desc` params (alongside the existing `filter`).
- Services: `EpubConverter` (archive→EPUB) and `EpubCache` (path/key/read/
  write/delete), registered in DI.

## Testing

- **Unit:** sort-link next-state logic (off→asc→desc→off); cache key from
  (id,size,mtime); `fresh` deletes then regenerates; EPUB structure (mimetype
  first & stored, N pages from an N-image archive, viewport per page); WebP
  entry transcoded (build a tiny in-memory CBZ with ImageSharp-generated images
  incl. a WebP; convert; assert pages + no WebP in output).
- **Integration (seeded ABS):** the cbz/cbr fixtures → `/convert` yields a valid
  EPUB (unzip, verify structure); `?fresh=1` rebuilds; `/download` streams the
  primary file. Add these to `docker/smoke-test.sh`.
- Existing tests stay green.

## Out of scope

- Multi-file / multi-format selection (only the primary ebook).
- Sorting/searching changes beyond the fields listed.
