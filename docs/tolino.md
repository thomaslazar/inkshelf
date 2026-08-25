# Tolino readers and browsers

Everything device-specific about the Tolino family: which EPUB reader renders a
converted comic, and what its browser engine can and cannot do. For the devices
themselves and the settings each one needs, see [`DEVICES.md`](DEVICES.md).

## Reader engines

Firmware 16.2.0 offers two EPUB readers, the beta one enabled by a setting on the
device. Which is active decides how converted comics look:

- **beta** - honours the viewport a fixed-layout page declares, then keeps ~2% of
  the page height for itself. Set page scale to 98 or the bottom is clipped.
- **standard** - ignores the declared viewport and sizes pages from the image
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

### 16.2.0 - Chrome 30

`Android 4.4.2 … AppleWebKit/537.36 … Chrome/30.0.0.0`, a 2013-era Chromium on
`Linux armv7l`. **Treat it as Chrome 30 / ES5.** Probed on three devices, which
differ only in screen metrics:

| Device | screen | inner | dpr |
|---|---|---|---|
| epos 2 (2026-07-13) | 769 × 953 | - | 1.875 |
| vision 5 (2026-08-21) | 675 × 825 | 675 × 807 | 1.875 |
| page 2 (2026-08-21) | 573 × 702 | 573 × 684 | 1.325 |

### Confirmed support - identical on all three

The feature results came back byte for byte the same on all three devices, so this
list covers the whole 16.2.0 generation rather than one model.

Supported: `display:flex` (old flexbox), `calc()`, `overflow-wrap`,
`XMLHttpRequest`, `localStorage`, `addEventListener`.

NOT supported - avoid: flexbox `gap`, CSS grid, `object-fit`, CSS custom
properties (`--x` / `var()`), `min()`/`max()`/`clamp()`, `aspect-ratio`,
`position: sticky`, `:has()`, `@media (prefers-color-scheme)`; and in JS:
`Promise`, `fetch`, ES6 `const`/`let`/arrow functions/template literals,
`Array.prototype.includes`.

Practical rules:
- **Spacing:** use `margin`/`padding`, never flex/grid `gap`.
- **Layout:** flexbox is fine (old syntax); **no CSS grid**.
- **Images:** `max-width`/`max-height` + a fixed wrapper box; **no `object-fit`**.
- **No CSS variables, no `clamp()`/`min()`/`max()`, and no `calc()` either** -
  `calc()` works on 16.2.0 but is measured absent on 10.5.0, so use fixed values.
- **Dark mode:** `prefers-color-scheme` never matches, so the light/black theme
  always applies on-device (dark variants are only for GitHub, etc.).
- **JS:** keep it out of app pages. Any diagnostic JS must be ES5 + `XMLHttpRequest`.

### 10.5.0 - the shine, and the floor (probes 2026-08-21, measured 2026-08-23)

`Android 2.3.4 … AppleWebKit/533.1 … Version/4.0 Mobile Safari/533.1` - the 2011
Gingerbread stock browser, `Linux armv7l`. Two engine generations behind the
epos 2, and it is the floor that matters:

- **No `CSS.supports()`**, so nothing here can be feature-detected; the
  measured probe answers instead (2026-08-23):
  - **`box-sizing` yes** - with the `-webkit-` prefix alongside, which is what
    the stylesheet ships.
  - **`display: flex` no** - rows and the header rely on float fallbacks here.
  - **`rem` yes** - which the stylesheet leans on heavily.
  - **`calc()` no.** Supported on 16.2.0, absent here. Use fixed values.
  Nothing in the epos 2 list above can be assumed on this engine.
- **JS confirmed absent:** `Promise`, `fetch`, `Array.prototype.includes`,
  `const`/`let`, arrow functions, template literals. So the ES5 rule is a hard
  floor, not a preference.
- **JS confirmed present:** `XMLHttpRequest`, `localStorage`,
  `addEventListener` - which is exactly what the convert poll script uses.

Its screen metrics need care. It reports `screen 567×686` with
`innerWidth == screenWidth` - no chrome subtracted - at `devicePixelRatio 1.325`,
which is the 751×909 the settings readout shows for a 758×1024 panel. An earlier
run of the same page reported `screen 749×906` instead and did not reproduce, so
treat a single reading as indicative and calibrate against what pages actually
look like
- see [`DEVICES.md`](DEVICES.md).

## Downloads hand off to a separate manager

A large download does not stay in the browser. The firmware passes it to a
separate download manager, which re-requests the same URL - observed on the
shine (10.5.0) and on the vision 5 and epos 2 (16.2.0), so the handoff itself is
not a quirk of one generation. The browser's own partial transfer is abandoned
and its bytes are discarded rather than resumed: the manager sends no `Range`
header even when the response advertises `Accept-Ranges`.

**On the shine that re-request carries no cookies**, which is what made large
downloads fail outright there. On 16.2.0 both requests arrive with the session
cookie (measured on the epos 2 with the request log's `NOCOOKIE` marker), so a
cookie-less re-request is so far specific to 10.5.0 - which is also why those
devices downloaded 50 MB comics before download tickets existed. What carries the
cookie there is not established: the manager may forward it, or the browser may
be retrying the transfer itself. Design for the cookie-less case regardless; it
costs nothing where the cookie is present.

This is why every download link carries a short-lived ticket in its URL. A link
that authorises by cookie alone cannot be completed by the component that
actually fetches the file.

## Guidance

- Prefer margins/padding over `gap`.
- Prefer `max-width`/`max-height` over `object-fit`.
- Assume no modern layout niceties (`aspect-ratio`, `position: sticky`,
  container queries, `:has()`, `clamp()`) until proven - see the probe below.
- Keep JavaScript out of the app pages entirely; the ABS web UI fails on this
  browser precisely because it is JS-heavy.

## Capability probe

`src/Inkshelf/wwwroot/diag.html` is a standalone diagnostic page (the only
place JS is used, and it is not part of the app flow). Visit it on the device;
it runs `CSS.supports()` / `matchMedia` / JS feature checks, renders a table
on-screen, and best-effort POSTs the results to `/diag` (logged server-side).
Update the "Confirmed" list above from a real probe run.

The server log is the only practical way to get a probe off one of these readers -
their browsers cannot select or copy text - so read it there:
`docker logs inkshelf 2>&1 | grep "Browser probe"`.
