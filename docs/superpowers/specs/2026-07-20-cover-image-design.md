# Cover image — design

## Problem

The converted EPUB declares **no cover**. `EpubWriter.Opf(...)` emits no
`properties="cover-image"` manifest flag and no EPUB2 `<meta name="cover">`, so
Apple Books shows a blank placeholder and lenient readers just fall back to
rendering page 1. The book has no library thumbnail.

## Goal

The converted EPUB declares a real cover so strict readers show a thumbnail.
**Thumbnail only** — the cover is metadata, not a rendered page; reading still
opens on page 1.

## Cover source (fallback, decided in the worker)

1. **Prefer the ABS cover art.** Fetch `/api/items/{id}/cover?width=600` with the
   captured bearer. If it succeeds and decodes → use it.
2. **Fall back to page 1** when ABS has no cover, the fetch fails, or it doesn't
   decode → flag the first page image as the cover. No extra file.

For comics with no embedded cover, ABS auto-extracts page 1 as the cover, so the
two paths often yield the same image — which is fine, since the cover only lands
in metadata, never the spine.

## Both cover mechanisms (compatibility)

- **EPUB3:** the cover manifest item gets `properties="cover-image"`.
- **EPUB2:** `<meta name="cover" content="<item-id>"/>` in `<metadata>`. The
  `content` references the manifest item **id**, not its href.

## Requested width

Fixed **600px**, independent of the device page cap. Rationale: the cover is only
ever a thumbnail (and a reader's "cover view"), so page-resolution art (retina
`target.MaxW` ≈ 1400px) would just bloat the file. 600px is crisp as a thumbnail
for tens of KB. The device page cap still acts as an *upper* bound inside
`PageImageProcessor` (it only downscales, never upscales), so a low-res device
naturally gets a smaller cover and everyone else gets 600. (For contrast, the
in-app `/cover` listing proxy requests 120px, capped at 400 — those are the tiny
e-reader listing rows; this embedded cover is seen in other readers' libraries
too, hence the step up.)

## Changes by unit

### `AbsDownloadClient`
Add `DownloadCoverAsync(itemId, accessToken, width, ct)`, mirroring
`DownloadEbookAsync`: handler-free, caller-supplied bearer, **no** 401 refresh.
Returns the cover stream (+ content-type). This is why we do **not** reuse
`AbsApiClient.GetCoverAsync` — the worker has no `HttpContext` for `AbsAuthHandler`
to resolve a token from.

### `ConvertWorker.ProcessAsync`
After acquiring the convert lock (and reusing the scope's client), best-effort
fetch the cover at width 600: read the stream to a small `byte[]` and derive the
extension from the content-type (`image/png`→`.png`, `image/webp`→`.webp`, else
`.jpg`). Wrap the whole fetch in try/catch → `null` on any failure. **The cover
must never fail the conversion.** Pass the raw bytes + ext to the converter.

The cover is fetched with the same captured token used for the ebook download;
one small extra request on an already-heavy (60–90 s) path is negligible, and
always-trying avoids coupling to the detail metadata's `coverPath` shape. A 404
(no cover) simply falls through to `null`.

### `EpubConverter.ConvertAsync`
New optional parameter carrying the raw cover `(byte[] Bytes, string Ext)?`. When
present, run it through **`PageImageProcessor.ProcessAsync`** with the same cap,
grayscale flag, and WebP→JPEG transcode the pages get, then hand the processed
`(bytes, ext, w, h)` to the writer. When absent, pass nothing.

### `EpubWriter.WriteAsync`
New optional parameter for the processed cover.

- **Cover present:** write `OEBPS/cover<ext>`; add a manifest item
  `id="cover-img" href="cover<ext>" media-type="…" properties="cover-image"`; add
  `<meta name="cover" content="cover-img"/>` to `<metadata>`.
- **Cover absent (fallback):** in `Opf(...)`, add `properties="cover-image"` to the
  **first page's** existing `img1` manifest item and emit
  `<meta name="cover" content="img1"/>`. No extra file. The manifest is written
  after the page loop, so `img1` is always known by then. (Guard the zero-page
  edge: if there are no pages and no cover, emit no cover metadata.)

The cover **image bytes** are written to the zip before the page loop when
present (order after `mimetype` is flexible); the **manifest item + `<meta>`** are
emitted in `Opf(...)` at the end. `Opf(...)` decides present-vs-fallback from
whether a cover was supplied and whether any pages exist.

## Non-goals

- **No cache-key change.** The cover derives deterministically from the item — no
  new user input — so `EpubCache.PathFor` is untouched. Existing cached EPUBs stay
  coverless until regenerated (↻) or evicted; not worth invalidating the cache.
- **No `cover.xhtml` spine page.** Thumbnail only; adding the cover to the reading
  flow would double the cover for comics whose page 1 is already the cover.
- **No min-dimension "large enough" guard.** Present + decodable is enough; ABS
  covers are effectively always usable for comics.
- **String-built EPUB XML stays** (load-bearing convention — no XML library).

## Testing

- `EpubWriter`: cover present → `OEBPS/cover.jpg` entry exists, manifest item has
  `properties="cover-image"`, and `<meta name="cover" content="cover-img"/>`
  present. Cover absent → `img1` carries `properties="cover-image"` and
  `<meta name="cover" content="img1"/>`; no `cover.*` entry. Zero pages + no cover
  → no cover metadata at all.
- `EpubConverter`: a supplied cover is run through the processor (e.g. a WebP
  cover comes out `.jpg`; grayscale flag applies).
- `ConvertWorker`: a cover-fetch failure still produces a valid EPUB (fallback
  path). Existing conversion tests stay green.
- Output stays epubcheck-clean.
