# Tolino e-reader browser — known limitations

> This file is about the **browser engine's** CSS/JS limits. For per-device
> rendering settings and which readers are known to work, see
> [`DEVICES.md`](DEVICES.md).

Inkshelf targets the Tolino's built-in web browser, which is an old,
limited engine. Design CSS/HTML for it, not for a modern browser.

## Device (from the /diag probe)

Tolino epos 2 — `AppleWebKit/537.36 … Chrome/30.0.0.0 … Android 4.4.2`
(a 2013-era Chromium), `Linux armv7l`. Viewport 769×953 CSS px (browser chrome
leaves ~541 px tall), devicePixelRatio 1.875. **Treat it as Chrome 30 / ES5.**

## Confirmed support (epos 2 probe, 2026-07-13)

The same profile was confirmed byte for byte on a **vision 5** and a **page 2**
(2026-08-21): both are the identical engine — `Android 4.4.2 … Chrome/30.0.0.0 …
AppleWebKit/537.36` — differing only in screen size and pixel ratio. Treat this
list as covering that whole generation.

Supported: `display:flex` (old flexbox), `calc()`, `overflow-wrap`,
`XMLHttpRequest`, `localStorage`, `addEventListener`.

NOT supported — avoid: flexbox `gap`, CSS grid, `object-fit`, CSS custom
properties (`--x` / `var()`), `min()`/`max()`/`clamp()`, `aspect-ratio`,
`position: sticky`, `:has()`, `@media (prefers-color-scheme)`; and in JS:
`Promise`, `fetch`, ES6 `const`/`let`/arrow functions/template literals,
`Array.prototype.includes`.

Practical rules:
- **Spacing:** use `margin`/`padding`, never flex/grid `gap`.
- **Layout:** flexbox is fine (old syntax); **no CSS grid**.
- **Images:** `max-width`/`max-height` + a fixed wrapper box; **no `object-fit`**.
- **No CSS variables, no `clamp()`/`min()`/`max()`** — use fixed values or `calc()`.
- **Dark mode:** `prefers-color-scheme` never matches, so the light/black theme
  always applies on-device (dark variants are only for GitHub, etc.).
- **JS:** keep it out of app pages. Any diagnostic JS must be ES5 + `XMLHttpRequest`.

## Older floor: Tolino shine (probe 2026-08-21)

`Android 2.3.4 … AppleWebKit/533.1 … Version/4.0 Mobile Safari/533.1` — the 2011
Gingerbread stock browser, `Linux armv7l`. Two engine generations behind the
epos 2, and it is the floor that matters:

- **No `CSS.supports()`**, so CSS capabilities cannot be feature-detected there
  at all. Every CSS row in its probe came back unknown, not supported. Nothing
  in the epos 2 list above — flexbox, `calc()`, `overflow-wrap` — can be
  assumed here.
- **JS confirmed absent:** `Promise`, `fetch`, `Array.prototype.includes`,
  `const`/`let`, arrow functions, template literals. So the ES5 rule is a hard
  floor, not a preference.
- **JS confirmed present:** `XMLHttpRequest`, `localStorage`,
  `addEventListener` — which is exactly what the convert poll script uses.

Its screen metrics are not trustworthy. The probe page reported
`screen 749×906`, `innerWidth == screenWidth` (no chrome subtracted) and
`devicePixelRatio 1.325` — multiply those out and you get 992×1200 for a
758×1024 panel. The app's own probe on the same device yields 567×686 CSS,
i.e. the 751×909 the settings readout shows. Same viewport meta on both pages,
so the discrepancy is the engine's, not ours. Treat `screen.*` on this class of
device as indicative only, and calibrate against what pages actually look like
— see [`DEVICES.md`](DEVICES.md).

## Guidance

- Prefer margins/padding over `gap`.
- Prefer `max-width`/`max-height` over `object-fit`.
- Assume no modern layout niceties (`aspect-ratio`, `position: sticky`,
  container queries, `:has()`, `clamp()`) until proven — see the probe below.
- Keep JavaScript out of the app pages entirely; the ABS web UI fails on this
  browser precisely because it is JS-heavy.

## Capability probe

`src/Inkshelf/wwwroot/diag.html` is a standalone diagnostic page (the only
place JS is used, and it is not part of the app flow). Visit it on the device;
it runs `CSS.supports()` / `matchMedia` / JS feature checks, renders a table
on-screen, and best-effort POSTs the results to `/diag` (logged server-side).
Update the "Confirmed" list above from a real probe run.

The server log is the only practical way to get a probe off an e-reader — those
browsers cannot select or copy text — so read it there:
`docker logs inkshelf 2>&1 | grep "Browser probe"`. The page repeats the payload
as one block for probes run from a desktop or phone.
