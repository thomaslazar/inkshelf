# Tolino e-reader browser — known limitations

Inkshelf targets the Tolino's built-in web browser, which is an old,
limited engine. Design CSS/HTML for it, not for a modern browser.

## Confirmed (observed on-device)

- **No CSS `object-fit`.** `width`+`height` on an `<img>` just stretches it
  (distorted). Size images with `max-width`/`max-height` and let the other
  dimension stay `auto` to preserve aspect ratio. Reserve a fixed box with a
  wrapper element if you need consistent layout.
- **No flexbox `gap`.** `gap` on a flex container is ignored → no spacing.
  Use `margin` on the children instead.

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
