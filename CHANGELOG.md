# Changelog

All notable changes to Inkshelf are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

## v0.3.0 — 2026-07-24

### Highlights
- **German UI localisation.** Inkshelf's own chrome (navigation, breadcrumbs, row actions, pager, login/settings forms, empty states) is now translatable. Language is per-device, chosen in Settings or defaulted from the browser's `Accept-Language`, with English as the fallback. New languages drop in as a JSON file plus a restart — no rebuild.
- **Conversion failure reasons.** A failed comic conversion now explains *why* on a plain-HTML page — too large (with the actual size vs the limit), unreadable archive, download failure, or unexpected error — instead of a bare "Convert (retry)". Oversized archives are now rejected before downloading.
- **No more blank page from a stale favorite.** A favorite-library cookie left over from a different Audiobookshelf server no longer produces a blank 500; it's validated and cleared, falling back to the library list.
- **Higher default conversion limits** — archive 1 GiB, cache 5 GiB.
- **Touch- and e-reader-friendly polish** — a larger, better-spaced libraries list, and Failed-row actions that wrap correctly on narrow e-ink screens.

### Features
- feat: add conversion failure reason page
- feat: add language picker to Settings
- feat: add LocalizationCatalog for JSON translation files
- feat: add per-device language to DeviceSettings
- feat: add request-scoped Localizer with language resolution
- feat: carry archive size and expose failure via ConvertService
- feat: categorize conversion failures and reject oversized archives early
- feat: enlarge and space out the libraries list for touch
- feat: link failed rows to the reason page and auto-navigate on failure
- feat: load and inject UI translation catalog
- feat: localise index, library, item, settings, and converted pages
- feat: localise login page + localisation integration test
- feat: localise shared layout and row/pager/convert partials
- feat: merge UI translations from baseline + optional override dir
- feat: raise default conversion limits (archive 1 GiB, cache 5 GiB)
- feat: store failure reason on the convert queue entry

### Fixes
- fix: align German UI terms with Audiobookshelf wording
- fix: don't blank-500 when a favorite library is missing on the current ABS
- fix: don't report a misleading size on copy-guard TooLarge; harden FailureFor lookup
- fix: emit convert-status JS labels as JSON, not HTML-encoded
- fix: even out pager spacing around the page indicator
- fix: guard locale directory listing against enumeration errors
- fix: rename ConvertWhy.File query param to avoid hiding PageModel.File
- fix: use "Als gelesen markieren" for the mark-read label
- fix: wrap the failed-row convert actions so the why? link fits narrow screens

### Internal
- refactor: harden localisation edge cases and drop dead FilterDisplay
- test: cover the conversion failure reason path in uicheck
- test: seed corrupt comic fixtures and cover BadArchive/ConvertError in uicheck
- chore: add headless-browser UI screenshot harness (tools/uicheck)
- chore: extend uicheck to authenticated pages via seeded ABS
- chore: add Node devcontainer feature for ponytail plugin hooks
- chore: install ponytail and answer-first plugins in devcontainer
- docs: add localisation and disable-conversion roadmap items
- docs: add structured settings-cookie refactor to roadmap
- docs: add UI localisation design spec; drop InvariantGlobalization
- docs: add UI localisation implementation plan
- docs: clarify DeviceSettings cookie comment (10de is not a lang code)
- docs: document localisation workflow in CONTRIBUTING
- docs: implementation plan for conversion failure reasons
- docs: move shipped UI localisation to roadmap Done
- docs: record conversion failure reasons
- docs: roadmap item for surfacing conversion failure reasons
- docs: spec for surfacing conversion failure reasons

## v0.2.1 — 2026-07-21

### Highlights
- The header icon (left of "Libraries") is now a link back to the libraries
  list, on every page.
- The libraries page shows the deployed Inkshelf version, so you can tell which
  build is actually running.

### Features
- feat: link the header icon to libraries and show the version

## v0.2.0 — 2026-07-21

### Highlights
- **Per-device settings** — a Settings page (cog link in every header) with
  **retina** and **grayscale** toggles that flow through comic conversion and the
  cache, so each e-reader gets pages tuned to its screen.
- **Read / unread tracking** — mark items read from the listing, search, and
  detail rows, synced to your Audiobookshelf progress.
- **Real EPUB covers** — converted comics embed a proper cover (the ABS cover
  art, falling back to the first page) instead of a blank placeholder.
- **Item detail page** — a per-item page with full metadata (author, series,
  narrators, genres, tags, description — all filterable), every downloadable file,
  and a per-file convert action.
- **"Converted on this device" view** — one page listing every comic already
  converted and cached for the device you're on, across all libraries.

### Features
- feat: add ABS read-state read/write to AbsApiClient
- feat: add DeviceSettings per-device settings cookie
- feat: add POST /read endpoint to toggle read state
- feat: add POST /settings endpoint to write the settings cookie
- feat: add read/unread toggle to listing and search rows
- feat: add Settings page and cog entry links
- feat: add the converted-this-device view + index link
- feat: add the item detail page
- feat: add token-less ABS cover download for the worker
- feat: apply device settings to conversion and row-state
- feat: declare an EPUB cover (cover-image + EPUB2 meta) with first-page fallback
- feat: default retina to on
- feat: enumerate cached epubs via reverse-parsed filenames
- feat: expose libraryId/title/cover from the ABS batch fetch
- feat: fetch expanded item detail + per-file ebook stream
- feat: fetch the ABS cover in the worker and embed it in the EPUB
- feat: introduce RenderTarget and parameterize ScreenTarget retina + dpr clamp
- feat: key the EPUB cache on grayscale
- feat: label genre/tag/narrator facet filters
- feat: process and embed the ABS cover during conversion
- feat: support grayscale page desaturation in PageImageProcessor
- feat: thread optional file ino through convert and download

### Fixes
- fix: cap listing body width so long titles don't overlap actions
- fix: don't fail conversion when the cover fetch times out
- fix: replace settings glyph with a PNG gear icon
- fix: show accurate convert state on search-result rows
- fix: show facet type and resolved name in the filter banner
- fix: show the library in the item detail breadcrumb
- fix: stack detail file rows so long names don't overlap actions
- fix: use encoding-safe cookie format and CookieOptions for DeviceSettings

### Refactors
- refactor: extract _ConvertAction partial; link row title/cover to detail
- refactor: extract shared convert-row-state resolver
- refactor: simplify converted-view dedupe to an id set
- refactor: thread RenderTarget through the conversion pipeline

## v0.1.2 — 2026-07-17

### Highlights
- **Much lower memory use during and after conversions.** Comic conversion no
  longer buffers the whole archive and every page in RAM — the download is
  spooled to a temp file and pages are streamed into the EPUB one at a time, so
  only a single page is held. Combined with Workstation GC (which hands memory
  back to the OS), the sidecar no longer ratchets up to ~900 MiB and stay there
  after a batch; it returns to near-idle.
- **The per-conversion peak is bounded by one page**, so even large comics stay
  modest — safe on a memory-constrained host.
- Operators can now set a **container memory limit** (see the compose example /
  README); with the lower footprint it can be kept tight.
- No change to converted-EPUB output — byte-identical to before.

### Performance
- perf: stream pages into the EPUB instead of buffering all
- perf: spool archive to temp file and release ImageSharp pool per job
- perf: use Workstation GC + conserve memory for the sidecar

### Fixes
- fix: make convert temp-file cleanup best-effort in finally

## v0.1.1 — 2026-07-16

### Highlights
- **Comic conversion no longer dies on slow hosts.** CBZ/CBR→EPUB conversion now
  runs in a background worker instead of inside the web request, so a client
  disconnect (a timed-out tab, navigating away) can no longer cancel it
  mid-flight. On a low-powered box, a large comic that previously *never*
  finished now converts reliably and stays cached.
- The listing shows live progress — "Converting…" flips to "EPUB ✓" via a small
  status poll, with a no-JavaScript fallback (a periodic refresh) for old e-reader
  browsers.
- Regenerate (↻) and repeated taps behave cleanly — no duplicate rows, no
  stuck "Converting…" state after a restart.

### Features
- feat: poll convert status client-side, meta-refresh fallback
- feat: reshape convert into a background-kick + status endpoint
- feat: add ConvertWorker background service and tmp sweep
- feat: add handler-free AbsDownloadClient for the worker
- feat: add ConvertQueue job registry and channel
- feat: add MaxConcurrentConversions option

### Fixes
- fix: make regen a plain link to avoid duplicate EPUB row
- fix: restore cache LRU touch-on-serve and regen JS intercept

## v0.1.0 — 2026-07-15

First tagged release of Inkshelf — a thin, server-rendered web client for the
Audiobookshelf (ABS) API, built for e-reader browsers with near-zero JavaScript.
Ships as a multi-arch container image at `ghcr.io/thomaslazar/inkshelf`.

### Highlights
- **Browse an ABS library** from a plain-HTML client: search, author/series
  filters, cycling sort links, top/bottom pagination, and a favorite-library
  shortcut — `<form>`/`<a>` only, no client JS required.
- **Ebook delivery**: download the original ebook, or convert CBZ/CBR comics on
  demand to a device-sized, epubcheck-clean fixed-layout EPUB, cached on disk
  with an "already converted" indicator.
- **Stateless auth**: the ABS token lives in a Data-Protection-encrypted cookie
  and refreshes transparently on expiry via a delegating HTTP handler.
- **Hardened**: force-secure cookies behind a proxy, optional trusted-proxy
  scoping, a bounded + sanitized + gateable diagnostics endpoint, and
  resource-exhaustion guards on conversion (archive size ceiling, cache LRU cap,
  per-target convert lock, screen-dimension clamp).
- **Easy to run**: a single multi-arch (`linux/amd64` + `linux/arm64`) image; no
  external services beyond your ABS server.

### Features
- feat: library search, filters, top pager, favorite star, 10/page
- feat: cycling sort links that compose with filters and paging
- feat: search results jump-bar with per-category counts + anchors
- feat: favorite library cookie with auto-redirect
- feat: libraries list page
- feat: paginated library items page
- feat: placeholder (title initial) for items without a cover
- feat: layout and login page
- feat: add logo, favicon set, manifest, header, and login wordmark
- feat: download the primary ebook file
- feat: CBZ/CBR to fixed-layout EPUB converter
- feat: /convert endpoint serving cached CBZ/CBR to EPUB
- feat: device-sized comic conversion, accurate author/series links, cached indicator
- feat: epubcheck-clean EPUBs, inline convert, non-retina pages, search links
- feat: on-disk EPUB cache keyed by item id + size + mtime
- feat: cap EPUB cache with LRU eviction and touch-on-serve
- feat: encrypted-cookie token store
- feat: AbsClient login and refresh
- feat: AbsClient libraries, items, cover
- feat: item detail, ebook stream, and sort params in AbsClient
- feat: full item metadata, filter param, and library search in AbsClient
- feat: AbsSession with refresh-on-401 and persistence
- feat: AbsAuthClient/AbsApiClient split and auth DelegatingHandler
- feat: cover proxy, logout, and deploy assets
- feat: wire DI, data protection, auth-redirect middleware
- feat: scaffold web + test projects with fail-fast ABS_URL
- feat: version the app and derive ABS User-Agent from it
- feat: force-secure cookies via FORCE_SECURE_COOKIES option
- feat: optional TRUSTED_PROXY scoping for forwarded headers
- feat: bound and sanitize /diag body, add DIAG_ENABLED kill-switch
- feat: clamp scr cookie dimensions to a safe maximum
- feat: add keyed ConvertLock for serializing conversions
- feat: bound archive buffering with a MaxArchiveBytes ceiling

### Fixes
- fix: send User-Agent on ABS requests to pass reverse-proxy WAF
- fix: resolve author/series filters by name and 404 missing covers
- fix: reach descending sort and author-named convert filename
- fix: search "to top" links jump to page top, not the results line
- fix: antiforgery token on favorite/logout forms; add browser-capability probe page
- fix: constrain header brand icon to 24px (global img rule blew it up)
- fix: harden proxy, csrf, error handling, and route guards
- fix: dispose ABS response on non-401 error to avoid leak
- fix: root the session cookie at path / so logout clears it
- fix: serialize same-target conversions to avoid double-work and corrupt cache
