# Per-device settings system + retina & grayscale toggles

**Status:** design approved, ready for implementation plan
**Date:** 2026-07-17
**Roadmap item:** Priority #1 — "Settings system + retina toggle" (scoped down)

## Goal

Give Inkshelf a real per-device settings system and use it to expose two
rendering knobs that today are hard-coded or absent:

- **Retina toggle** — replaces the hard-coded `ScreenTarget.Retina = false`
  const. On a high-DPR phone the non-retina pages are upscaled and hard to
  read; let the user opt into full-resolution pages per device.
- **Grayscale toggle** — optionally desaturate converted pages to shrink files
  on e-ink; kept manual because `matchMedia('(monochrome)')` is unreliable on
  e-ink panels.

## Scope

**In:** the settings system (cookie + page + write endpoint), the retina
toggle, the grayscale toggle, and the "Retina dpr clamp" security fix (which is
a prerequisite for turning retina on safely).

**Out (stays on the roadmap):**
- **Resolution override** — moved back to the backlog; design undecided.
- **EPUB2 reflowable fallback** — undecided whether to build it at all.

## Design

### A. Storage — the settings cookie

A new server-written cookie `inkshelf_settings` holds the user's choices,
handled by a small `DeviceSettings` type in `Auth/`, mirroring the read/write
shape of the existing `Favorites.cs`.

- **Format:** compact query-string-style, `r=1&g=0` (`r` = retina, `g` =
  grayscale). Absent, empty, or unparseable → defaults.
- **Defaults:** retina **off** (matches today's `Retina = false`), grayscale
  **off**.
- **Cookie flags:** identical to `Favorites` — `HttpOnly`, `SameSite=Lax`,
  `Secure = ForceSecureCookies || Request.IsHttps`, `IsEssential`, `Path=/`,
  `MaxAge = 365 days`.
- The existing `scr` (device-probe) and `inkshelf_fav_library` cookies are
  **unchanged**. `scr` stays JS-written device truth; the settings cookie is
  server-written user choice. They are read together where conversion happens.

The value is a `record DeviceSettings(bool Retina, bool Grayscale)` with a
`Default`; a static helper (static class or static methods on the record)
provides `Read(HttpRequest)` and `Set(HttpResponse, DeviceSettings)`, matching
how `Favorites` reads/writes its cookie.

### B. Settings page + write endpoint + entry links

- **`Pages/Settings.cshtml`** — Razor Page (`OnGet` only). Renders a plain
  `<form method="post" action="/settings">` with two checkboxes (retina,
  grayscale) pre-filled from the cookie, an antiforgery token, and an
  informational readout of the detected device parsed from `scr`
  (e.g. "detected 375×812 @dpr 3") so the retina choice has context. Framed
  clearly as "applies to this device/browser." Defensive CSS only (no
  `object-fit`, no flex `gap`).
- **`POST /settings`** — minimal-API endpoint (new `MapSettingsEndpoints` in
  `Endpoints/`, matching the `/favorite` + `/logout` action convention).
  Writes the cookie and PRG-redirects back (to a local-only return path, same
  open-redirect guard style as `ConvertEndpoints.LocalReturn`). Checkbox
  semantics: an unchecked box sends no field, so absent = off.
- **Entry links:** a cog-glyph-only `<a>` (⚙, no text label — carries an
  accessible `title`/`aria-label`) in the Index `.page-head` (next to Log out)
  and in the Library `.page-head`. No shared header partial — two small `<a>`
  tags in two views, consistent with the current per-page head pattern.

### C. How retina + grayscale reach conversion

The render knobs are bundled into one record to avoid threading four loose
parameters through the call chain:

- **New `RenderTarget` record** `(int MaxW, int MaxH, double Dpr, bool
  Grayscale)`. `ScreenTarget.FromCookie` is changed to take the retina +
  grayscale flags (from `DeviceSettings`) alongside the `scr` string and return
  a `RenderTarget`, replacing the `const bool Retina`.
- **Thread-through path:** `ConvertService.KickAsync` / `StatusAsync` →
  `ConvertQueue` job → `ConvertWorker` / `EpubConverter` →
  `PageImageProcessor` (grayscale = desaturate the decoded image before
  encode). The **`Library.cshtml.cs` row-state** path (`ComputeConvertStates`
  → `RowState` → cache lookup) reads the same two cookies and builds the same
  `RenderTarget`, so the per-row "✓ converted" badge matches what a real
  conversion produces.
- **Cache key:** `EpubCache.PathFor` / `TryGet` gain a grayscale marker:
  `…-{maxW}x{maxH}{(grayscale ? "-g" : "")}.epub`. Retina needs no marker — it
  already changes `maxW/maxH` (retina → css×dpr, non-retina → css), so the two
  variants already produce different filenames. Colour vs grayscale at the same
  dimensions would otherwise collide, hence the marker.
- **Security fix — "Retina dpr clamp"** (the roadmap Security item): in
  `FromCookie`, clamp each dimension to `MaxDimension` **after** multiplying by
  `dpr`, and bound `dpr` itself (cap at 4). Today the clamp happens before the
  multiply and `dpr` is unbounded — harmless only while `Retina = false`. This
  must land with the retina toggle.

### D. Testing + docs

**Tests:**
- `DeviceSettings` parse/serialize round-trip; malformed/absent → defaults;
  cookie-flag correctness including the force-secure mirror pair (matching the
  roadmap's Favorites-force-secure test note).
- `ScreenTarget.FromCookie`: retina on vs off dimensions/dpr; clamp-happens-
  after-multiply; `dpr` bound; the legacy 2-part `scr` fallback still works.
- `EpubCache`: grayscale marker produces a distinct path from colour at the
  same dimensions.
- `PageImageProcessor`: grayscale flag desaturates output.

**Docs:**
- `ARCHITECTURE.md`: document `DeviceSettings`, `RenderTarget`, the Settings
  page + `/settings` endpoint; note the two-cookie split (device probe vs user
  choice).
- `ROADMAP.md`: move retina + grayscale from Priority/Settings into **Done**;
  leave EPUB2 fallback and resolution override in the backlog (resolution
  override re-added to the Settings section as future work); remove the handled
  "Retina dpr clamp" item from the Security section.

## Data flow (summary)

```
scr cookie (JS device probe) ─┐
                              ├─► ScreenTarget.FromCookie ─► RenderTarget ─┐
inkshelf_settings (server) ───┘   (retina, grayscale flags)                │
                                                                           ├─► EpubCache.PathFor (grayscale marker)
                                                                           ├─► ConvertService → Queue → Worker → PageImageProcessor (desaturate)
                                                                           └─► Library row-state (matching cache lookup)
```

## Non-goals / deferred

- Resolution override (back on roadmap).
- EPUB2 reflowable fallback (back on roadmap).
- Folding the favorite-library cookie into the settings cookie (left separate).
- A shared header partial (each page keeps its own `.page-head`).
