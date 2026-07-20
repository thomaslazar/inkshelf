# Roadmap

An unordered backlog of work to build. Shipped items move to **Done** at the
bottom as a short record; the changelog is the authoritative history of what
has shipped.

## Priority (my current focus)

No priority features atm.

## Settings

Settings to add to the per-device settings system:

- **Resolution override.** Let the user hand-set the conversion resolution per
  device, for when the browser-reported screen size isn't ideal. Pairs with the
  retina toggle.
- **EPUB2 reflowable fallback.** Fixed-layout (EPUB3) is flagged by some older
  e-ink eReaders ("Das Öffnen dieses Buches kann zu Fehlern führen") and can crash
  them, as their Adobe engines are EPUB2-only. Offer a reflowable EPUB2 mode —
  works everywhere but has reader-imposed margins (not full-bleed) — for devices
  that can't do fixed-layout. (Our EPUB is already epubcheck-clean; the warning is
  the device's EPUB3 limitation, not our bug.)

## Conversion / rendering

- **Conversion speed.** First conversion of a big comic is ~60–90 s (ImageSharp
  resizing ~280 pages, serially). Parallelise page processing. Trades against
  memory, though: parallelising raises the per-conversion peak (more pages held
  at once), so this is deferred in favour of the low-memory serial path shipped
  under Runtime footprint.

## Browsing & reading

- **Item detail page.** A per-item page showing the full metadata ABS exposes —
  title, series (+ sequence), author(s), narrator, genre, tags, description,
  publisher/published date — plus **all** downloadable ebook files, not just the
  primary. Some items carry several formats of the same book (e.g. EPUB *and*
  PDF); the detail page lists each with its own download link, and for CBZ/CBR
  files shows the Convert action (or the EPUB download / "✓ converted" link when a
  conversion already exists for this device). Keep the **listing rows lean** —
  they still show only the primary file's download and/or the convert option — and
  make the detail page the one place that exposes every file. It also carries the
  read/unread toggle (as the listing rows do). Layout should stay nice within the
  near-zero-JS / defensive-CSS constraints (cover, metadata block, formats list).
  *Note:* enumerating all ebook files likely needs the **expanded** item
  (`libraryFiles[]`), not just `media.ebookFile`; verify against the ABS source.
- **Screenful pagination (investigation).** Spike whether we can size a page to
  exactly one screenful instead of a fixed 10. The `scr` cookie already reports
  the viewport (CSS w×h×dpr), so server-side we could compute
  `pageSize ≈ floor((viewportHeight − chrome) / rowHeight)`. Motivation: a typical
  e-ink reader fits only ~7 rows and scrolling is cumbersome, so "one page = one
  screen, no scroll" would be much nicer. Open questions: variable row heights
  (multi-author/series wrap), the first load before the cookie is set, and how
  this interacts with search results. Decide feasibility + approach before
  committing.

## Runtime footprint

- **Baseline trim.** Smaller idle wins beyond the GC + streaming work already
  shipped: `InvariantGlobalization` (drops ICU — verify title sorting for
  non-ASCII libraries first), disabling unused ASP.NET Core features / logging
  providers, `PublishTrimmed`. Native AOT is off the table (CLAUDE.md); GC
  configuration carried the bulk.

## Security

Test-coverage follow-ups from the hardening work (non-blocking):

- **`ConvertLock` cancellation test.** The keyed convert lock's cancellation path
  (a queued `AcquireAsync` that gets canceled) unwinds its ref-count but isn't
  exercised by a test. Add one asserting `ActiveKeys` returns to 0 and the
  semaphore isn't left stuck.
- **Archive-ceiling test: assert no partial file.** The `MaxArchiveBytes`
  over-limit test checks the `NotFound` outcome; also assert the cache dir is empty
  afterward, so a regression that wrote a partial `.tmp`/`.epub` before aborting
  would be caught.
- **`Favorites` force-secure test.** `TokenStore` has forced-vs-default
  `Secure`-flag tests; `Favorites.Set` applies the same rule but is untested — add
  the mirror pair for symmetry.

## Done

Shipped; kept as a short record (full detail in git history / the PR).

- **Converted (this device) view** — a `/converted` page listing every comic
  already converted and cached for the current device, across all libraries
  (the cache is enumerated by reverse-parsing filenames and filtered to the
  device's render target). Reuses the listing row (`_ItemRow`), with a metadata
  batch fetch for title/series/author + a series link into each item's library.
  Reached from a link on the home page. Shipped as a single combined view (not
  per-library): `POST /api/items/batch/get` is not library-scoped, so one call
  covers every library and carries `libraryId` per item.
- **Cover image** — the converted EPUB declares a real cover (EPUB3
  `properties="cover-image"` + EPUB2 `<meta name="cover">`), so Apple Books and
  other strict readers show a thumbnail. Prefers the ABS cover art (fetched at
  600px), falling back to the first page when ABS has no usable cover. Metadata
  only — reading still opens on page 1.
- **Background conversion** (PR #9) — conversion runs detached in a background
  worker (app lifetime, keyed by cache path), so a client disconnect can't kill
  it; JS polls a status endpoint, no-JS gets a `<noscript>` meta-refresh.
- **Listing freshness** — `Cache-Control: no-store` on the listing.
- **Regen (↻) feedback** — rides the same status poll as Convert.
- **Conversion memory footprint** — archive spooled to a temp file (not a
  `MemoryStream`) and pages streamed into the EPUB one at a time (only one
  page's bytes held); ImageSharp's pool released per conversion.
- **Runtime footprint (idle)** — Workstation GC + `ConserveMemory` baked into
  the image; measured on-box (resting ~897 → ~554 MiB from GC alone, streaming
  + pool-release cut the rest); container memory-limit guidance added. Baseline
  trim remains (see backlog).
- **Per-device settings + retina/grayscale** — a server-written
  `inkshelf_settings` cookie (`DeviceSettings`) with a plain-`<form>` Settings
  page (cog link in the Index/Library heads) exposing a **retina** toggle
  (replaces the hard-coded `ScreenTarget.Retina`) and a **grayscale** toggle.
  Both flow through a `RenderTarget` into conversion + the cache key (grayscale
  `-g` marker); includes the retina dpr clamp-after-multiply + dpr bound fix.
- **Read-state toggle** — per-row Mark read / ✓ Read on listing + search rows,
  synced to ABS media progress (`GET /api/me` finished-set; `PATCH
  /api/me/progress/{id}` `{isFinished}`).
