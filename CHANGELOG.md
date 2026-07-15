# Changelog

All notable changes to Inkshelf are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

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
