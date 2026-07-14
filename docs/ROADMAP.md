# Roadmap

Outstanding work, mostly follow-ups from the sorting + ebook-delivery feature.
Nothing here is blocking; the current build (sorting, download, device-sized
CBZ/CBR→EPUB conversion, cached indicator, search links) works.

## Conversion / rendering

- **Retina toggle (configurable).** Pages are currently hard-coded to
  **non-retina** (`ScreenTarget.Retina = false`) because full-resolution
  ("retina") pages crash the tolino **epos** reader on large comics (its Adobe
  engine is memory-limited and EPUB3/fixed-layout-flaky). Make it configurable —
  ideally per-device via the `scr` cookie or a user setting — so the tolino app,
  webreader, and higher-memory devices can opt into crisp retina pages.
- **User-defined resolution override.** Let the user hand-set the conversion
  resolution per device (via the cookie), for when the browser-reported screen
  size isn't ideal. Pairs with the retina toggle.
- **Grayscale / monochrome option.** Optionally convert pages to grayscale to
  shrink files on e-ink. Auto-detecting a mono display via
  `matchMedia('(monochrome)')` is unreliable on e-ink (the browser reports its
  colour rendering surface, not the panel), so make it a manual per-device
  toggle; keep colour for colour e-ink readers.
- **EPUB2 reflowable fallback.** Fixed-layout (EPUB3) is flagged by older tolino
  eReaders ("Das Öffnen dieses Buches kann zu Fehlern führen") and can crash the
  epos, whose Adobe engine is EPUB2-only. Offer a reflowable EPUB2 mode — works
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

## Infrastructure

- **Cache eviction.** The EPUB cache grows unbounded (per-item × per-device-size
  variants). Add a size cap / LRU eviction.
