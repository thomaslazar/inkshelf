# Tolino readers and browsers

Everything device-specific about the Tolino family: which EPUB reader renders a
converted comic, and what its browser engine can and cannot do. For the devices
themselves and the settings each one needs, see [`DEVICES.md`](DEVICES.md).

## Reader engines

Firmware 16.2.0 offers two EPUB readers, the beta one enabled by a setting on the
device. Which is active decides how converted comics look:

- **beta** — honours the viewport a fixed-layout page declares, then keeps ~2% of
  the page height for itself. Set page scale to 98 or the bottom is clipped.
- **standard** — ignores the declared viewport and sizes pages from the image
  itself, never enlarging one. Page scale does nothing here, and **retina must
  stay on**: with it off, images are capped at the panel divided by the pixel
  ratio and pages come out at roughly half size.

16.2.0 is the last release the epos 2, vision 5 and page 2 receive, so both
engines stay relevant. The shine (10.5.0) has neither, only its own older reader,
which supports less of what a fixed-layout book declares: it sizes pages from the
image, so page scale has no effect and the screen override is the only knob.

The reader is not the browser and cannot be probed: Inkshelf's JavaScript runs in
the browser, while comic layout happens in the reader app. Everything above comes
from looking at pages on hardware.

## Browser engine

Inkshelf targets these built-in browsers, which are old and limited. Design
CSS/HTML for them, not for a modern browser.

### 16.2.0 — Chrome 30

`Android 4.4.2 … AppleWebKit/537.36 … Chrome/30.0.0.0`, a 2013-era Chromium on
`Linux armv7l`. **Treat it as Chrome 30 / ES5.** Probed on three devices, which
differ only in screen metrics:

| Device | screen | inner | dpr |
|---|---|---|---|
| epos 2 (2026-07-13) | 769 × 953 | — | 1.875 |
| vision 5 (2026-08-21) | 675 × 825 | 675 × 807 | 1.875 |
| page 2 (2026-08-21) | 573 × 702 | 573 × 684 | 1.325 |

### Confirmed support — identical on all three

The feature results came back byte for byte the same on all three devices, so this
list covers the whole 16.2.0 generation rather than one model.

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

### 10.5.0 — the shine, and the floor (probe 2026-08-21)

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

The server log is the only practical way to get a probe off one of these readers —
their browsers cannot select or copy text — so read it there:
`docker logs inkshelf 2>&1 | grep "Browser probe"`.
