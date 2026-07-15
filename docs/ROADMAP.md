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
