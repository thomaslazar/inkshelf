# Tolino e-reader browser — known limitations

Inkshelf targets the Tolino's built-in web browser, which is an old,
limited engine. Design CSS/HTML for it, not for a modern browser.

## Device (from the /diag probe)

Tolino epos 2 — `AppleWebKit/537.36 … Chrome/30.0.0.0 … Android 4.4.2`
(a 2013-era Chromium), `Linux armv7l`. Viewport 769×953 CSS px (browser chrome
leaves ~541 px tall), devicePixelRatio 1.875. **Treat it as Chrome 30 / ES5.**

## Confirmed support (probe, 2026-07-13)

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
