# Roadmap

Outstanding work, mostly follow-ups from the sorting + ebook-delivery feature.
Nothing here is blocking; the current build (sorting, download, device-sized
CBZ/CBR→EPUB conversion, cached indicator, search links) works.

## Conversion / rendering

- **Retina toggle (configurable).** Pages are currently hard-coded to
  **non-retina** (`ScreenTarget.Retina = false`) because full-resolution
  ("retina") pages crash some e-ink readers on large comics (their Adobe-based
  engines are memory-limited and EPUB3/fixed-layout-flaky). Make it configurable —
  ideally per-device via the `scr` cookie or a user setting — so apps, the
  webreader, and higher-memory devices can opt into crisp retina pages.
- **User-defined resolution override.** Let the user hand-set the conversion
  resolution per device (via the cookie), for when the browser-reported screen
  size isn't ideal. Pairs with the retina toggle.
- **Grayscale / monochrome option.** Optionally convert pages to grayscale to
  shrink files on e-ink. Auto-detecting a mono display via
  `matchMedia('(monochrome)')` is unreliable on e-ink (the browser reports its
  colour rendering surface, not the panel), so make it a manual per-device
  toggle; keep colour for colour e-ink readers.
- **EPUB2 reflowable fallback.** Fixed-layout (EPUB3) is flagged by some older
  e-ink eReaders ("Das Öffnen dieses Buches kann zu Fehlern führen") and can crash
  them, as their Adobe engines are EPUB2-only. Offer a reflowable EPUB2 mode — works
  everywhere but has reader-imposed margins (not full-bleed) — as a per-device
  option for devices that can't do fixed-layout. (Our EPUB is already
  epubcheck-clean; the warning is the device's EPUB3 limitation, not our bug.)
- **Conversion speed.** First conversion of a big comic is ~60–90 s (ImageSharp
  resizing ~280 pages, serially). Parallelise page processing.
- **Cover image.** Add a cover (`<meta name="cover">` + first page) to the
  converted EPUB for nicer library display.

## Convert UX / feedback

- **Reliable "converting… / done" feedback.** The inline warm-XHR flip
  ("Convert" → "Converting…" → "EPUB ↓") is fragile on old browsers: the ~80 s
  warm request drops before it returns, and the browser also serves stale cached
  list pages. Options, cheapest first:
  1. `Cache-Control: no-store` on the listing so a normal reload reliably shows
     the server-rendered "EPUB ✓" (low-risk; fixes the stale-reload root cause).
  2. Auto-refresh the list (`<meta refresh>`) while a convert is pending.
  3. Robust polling: background convert + a cheap status endpoint + short polls
     that flip the link when ready.
- **Regen (↻) feedback.** The regenerate link is a plain direct link with no
  progress feedback; align it with whatever feedback approach is chosen.

## Browsing & reading

- **Item detail page.** A per-item page showing the full metadata ABS exposes —
  title, series (+ sequence), author(s), narrator, genre, tags, description,
  publisher/published date — plus **all** downloadable ebook files, not just the
  primary. Some items carry several formats of the same book (e.g. EPUB *and*
  PDF); the detail page lists each with its own download link, and for CBZ/CBR
  files shows the Convert action (or the EPUB download / "✓ converted" link when a
  conversion already exists for this device). Keep the **listing rows lean** —
  they still show only the primary file's download and/or the convert option — and
  make the detail page the one place that exposes every file. Layout should stay
  nice within the near-zero-JS / defensive-CSS constraints (cover, metadata block,
  formats list). *Note:* enumerating all ebook files likely needs the **expanded**
  item (`libraryFiles[]`), not just `media.ebookFile`; verify against the ABS
  source.
- **Per-library "already converted" view.** A page, per library, listing the
  books that are already converted and cached **for this device** (the cache is
  keyed by item + size + mtime + device dimensions, so filter to variants matching
  the current `scr`). Use case: "ah, I already converted that — click the series
  to grab the next volume." Show title/series/author with links (series → filtered
  listing) and a direct EPUB download. *Note:* needs a way to map cache entries
  back to items — either reverse-map the item id from the cache filename and
  re-fetch metadata, or keep a small sidecar index alongside the cache.
- **Screenful pagination (investigation).** Spike whether we can size a page to
  exactly one screenful instead of a fixed 10. The `scr` cookie already reports
  the viewport (CSS w×h×dpr), so server-side we could compute
  `pageSize ≈ floor((viewportHeight − chrome) / rowHeight)`. Motivation: a typical
  e-ink reader fits only ~7 rows and scrolling is cumbersome, so "one page = one
  screen, no scroll" would be much nicer. Open questions: variable row heights
  (multi-author/series wrap), the first load before the cookie is set, and how
  this interacts with search results. Decide feasibility + approach before
  committing.
- **Read-state toggle.** Let the user mark an item read/unread from **both** the
  listing rows and the detail page. Reading happens offline on the device, so ABS
  never sees progress; at minimum we want a manual "read" mark. *Design question:*
  where does the state live — local to Inkshelf (cookie/small store, simple but
  unsynced), or pushed to ABS via its media-progress / "finished" API so it
  reflects everywhere? Prefer syncing to ABS if the API supports it (verify the
  endpoint against the ABS source).

## Security

Follow-ups from the hardening work (all non-blocking; the shipped controls are in
place — these tighten test coverage and one latent edge):

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
- **Retina dpr clamp.** `ScreenTarget` clamps dimensions to `MaxDimension` *before*
  multiplying by the client-supplied `dpr`, and `dpr` itself is unbounded —
  harmless while `Retina = false`, but when the retina toggle (see Conversion /
  rendering) lands, clamp *after* the multiply and bound `dpr`.
