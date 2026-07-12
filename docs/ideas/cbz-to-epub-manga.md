# Idea: CBZ → EPUB conversion for Tolino manga delivery

> **Status:** Deferred / future (post-v1). Captured for later design — not yet
> brainstormed into a spec. When picked up, run it through the normal
> brainstorming → spec → plan flow.

## Goal

Deliver manga to a Tolino e-reader. Tolino's native reader can't show CBZ, so
CBZ files get repackaged to EPUB on the way out. Regular ebooks pass through
untouched; **only comics get repacked**.

## Delivery model

The user downloads the (E)PUB through the Tolino browser to the device, then
reads **offline** in the native reader (hardware page-turn buttons, bookmarks,
etc.). Inkshelf is only hit at **download time**, not during reading. This is
deliberate: keeps load off the weak host and avoids e-ink browser rendering
entirely.

## Metadata

Comes **entirely from ABS**, which is already curated and correct. Do NOT parse
titles/authors/series from CBZ filenames or folder structure. Inkshelf pulls
metadata from the ABS API and injects it into the EPUB OPF (`dc:title`, author,
series). The CBZ contributes **page images only**.

## Conversion approach (.NET)

- CBZ = ZIP of images; a manga EPUB = ZIP of one-image-per-page XHTML + an OPF
  manifest. Both handled by `System.IO.Compression.ZipArchive`. If no image
  re-encoding is needed, it's a repackage job — a few hundred lines, no heavy
  deps.
- **Critical EPUB footgun:** the `mimetype` entry MUST be the first entry in
  the archive AND stored uncompressed (`CompressionLevel.NoCompression`). Some
  readers reject the EPUB otherwise.
- **Fixed-layout, not reflowable:** use `rendition:layout: pre-paginated` so
  each image fills a page. Needs a per-page viewport meta matching each image's
  pixel dimensions. Read image header dimensions cheaply with ImageSharp
  (header decode, no full pixel decode).
- **RTL: out of scope.** Swipe direction doesn't matter for this use; skip
  `page-progression-direction` handling.

## Image format risk

Most CBZs seen are JPEG (pass through, no re-encode, effectively instant). But
contents aren't guaranteed. If any contain **WebP**, Tolino's reader likely
won't render it, forcing WebP → JPEG/PNG transcode. ImageSharp decodes WebP
natively. Handle this: detect format per entry, transcode only when needed,
pass through otherwise.

## Performance & caching

- Pure repackage: I/O-bound, ~1s for a 200-page volume.
- With re-encoding: ~30–100ms/page single-threaded ImageSharp, so tens of
  seconds per volume (less if parallelized across cores).
- **Cache the converted EPUB keyed by source file hash.** First download slow,
  rest served from cache. Never convert live on every request. Makes "live"
  conversion fine even on a ZimaBoard-class host.

## Deferred / v2 (optional manga niceties)

- Grayscale conversion (e-ink is grayscale anyway; ~halves file size for free).
- Double-page spread splitting (wide 2-page image → two portrait pages).
- These are the bulk of what Kindle Comic Converter (KCC) does. Could shell out
  to KCC for quality, but it's a Python dep that weighs down the light sidecar;
  only if hand-rolled isn't good enough.

## Download UX — verified working (2026-07-12)

Confirmed on the actual Tolino via a throwaway Inkshelf build: a
browser-initiated download of an EPUB/PDF **completes and the ebook is usable
in the native reader**. The download was served through a proxy endpoint
(`GET /download/{id}` → ABS `GET /api/items/{id}/ebook`) with the real ABS
filename and `Content-Disposition: attachment`. So the delivery model holds:
Inkshelf serves the file, the Tolino downloads it, reading happens offline in
the native reader.

The throwaway was reverted; the proper download feature (ebook button +
filename scheme) is to be built for real. That build should re-confirm the
finer point — whether the download **auto-appears** in the reading library vs.
needs a manual import step (firmware/model-dependent) — but the core path is
proven.
