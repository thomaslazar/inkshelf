# Roadmap

An unordered backlog of work to build. Shipped items move to **Done** at the
bottom as a short record; the changelog is the authoritative history of what
has shipped.

## Priority (my current focus)

No priority features atm.

## Settings

Settings to add to the per-device settings system:

- **EPUB2 reflowable fallback.** Fixed-layout (EPUB3) is flagged by some older
  e-ink eReaders ("Das Öffnen dieses Buches kann zu Fehlern führen") and can crash
  them, as their Adobe engines are EPUB2-only. Offer a reflowable EPUB2 mode —
  works everywhere but has reader-imposed margins (not full-bleed) — for devices
  that can't do fixed-layout. (Our EPUB is already epubcheck-clean; the warning is
  the device's EPUB3 limitation, not our bug.)

## Conversion / rendering

- **Conversion speed.** First conversion of a big comic is ~60–90 s (ImageSharp
  resizing ~280 pages, serially). Parallelise page processing. Trades against
  memory, though: parallelising raises the per-conversion peak (more pages held
  at once), so this is deferred in favour of the low-memory serial path already
  shipped (see the runtime-footprint entries under Done).
- **Disable conversion via config.** An `AbsOptions` flag / env var (e.g.
  `CONVERSION_ENABLED`, default `true`, mirroring `DIAG_ENABLED`) to turn the
  whole CBZ/CBR→EPUB system off — for e-readers that read comic archives natively
  and only want the raw download. When off: hide the Convert / EPUB / ↻ actions
  everywhere (rows and the detail page show only Download), skip registering
  `ConvertWorker`, don't map the `/convert` endpoints, and drop the `/converted`
  view plus its home-page link. The retina/grayscale settings only affect
  conversion, so hide those on the Settings page too when it's off. Any
  already-cached EPUBs are simply unreachable while disabled.

## Browsing & reading

- **Login credentials aren't offered to the e-reader's password store
  (investigation).** The e-ink e-reader's browser never offers to save the
  Inkshelf username/password, so every login is hand-typed on a slow keyboard.
  Safari on macOS saves it fine against the same deployment, which rules out
  most of the obvious causes — so this is device-specific, not a broken form.

  **Ruled out** (don't re-test these):
  - *The form markup.* `autocomplete="username"` / `current-password` were
    always present, and the input elements are byte-identical across every
    revision of `Login.cshtml` — the view's history only ever changed the
    heading, a CSS class and localisation. Adding `id`/`for`/explicit
    `type="text"` changed nothing on the device.
  - *Insecure origin.* Fails on the proxied HTTPS deployment as well as the
    plain-HTTP dev server, and a self-signed-cert HTTPS dev run didn't help
    either.
  - *`Cache-Control: no-cache, no-store`* on `/login` (set automatically by the
    antiforgery token, not by us). Safari saves the credentials in spite of it,
    and it has been there since the first scaffold anyway.

  - *`display: "standalone"` in `wwwroot/site.webmanifest`.* A standalone launch
    renders without browser chrome, and the save prompt is chrome — but the
    device opens Inkshelf from an ordinary bookmark in a normal tab, so
    standalone never applies. Confirmed the hard way: removing the `display` key
    and retesting on the device changed nothing, so it was put back. (Worth
    knowing regardless: that value is stock favicon-generator boilerplate from
    `c57aa9a`, not a deliberate choice, and nothing relies on it.)
  - *Scheme, cert validity and origin form.* Safari saves against the dev server
    over plain HTTP **and** over self-signed HTTPS, on a bare LAN IP.

  So the app is serving a form that competent stores accept; only that engine
  declines. Remaining candidates, cheapest first:
  - **The device's own password-saving setting**, or a site allow/block list in
    its store. Free to check, and check it first.
  - **A stale entry already in the store** for that origin, suppressing a
    re-prompt. Worth distinguishing "never prompts" from "prompts but never
    autofills later" — they point at different causes.
  - **`<button type="submit">` vs `<input type="submit">`.** Some very old
    engines only treat the latter as a login submit. Swapping it touches
    styling, so it needs a layout check.
  - **The engine may simply have no form-login password manager**, in which case
    there is nothing to fix and the item should be closed as won't-fix.
- **Screenful pagination (investigation).** Spike whether we can size a page to
  exactly one screenful instead of a fixed 10. The `scr` cookie already reports
  the viewport (CSS w×h×dpr), so server-side we could compute
  `pageSize ≈ floor((viewportHeight − chrome) / rowHeight)`. Motivation: a typical
  e-ink reader fits only ~7 rows and scrolling is cumbersome, so "one page = one
  screen, no scroll" would be much nicer. Open questions: variable row heights
  (multi-author/series wrap), the first load before the cookie is set, and how
  this interacts with search results. Decide feasibility + approach before
  committing.

## Done

Shipped; kept as a short record (full detail in git history / the PR).

- **Resolution override** — width, height and pixel ratio can be set by hand when
  the `scr` probe is missing, wrong, or simply not what the user wants. It takes
  precedence over the probe entirely, including when the probe is absent, which is
  also what finally gives `SpreadMode.Fit` a page box on a device with no
  JavaScript.
- **Two-page spread handling** — a landscape page image (two pages scanned as one)
  used to keep its wide fixed-layout viewport, which the reader letterboxed
  vertically and clipped on the right. A per-device setting now picks how: split in
  two with either half leading (left-first is the default; right-first is manga
  order), rotate 90° either way, or fit the whole spread on one page. A CBZ carries
  nothing that says which half comes first, so that stays the reader's choice.
- **One page size per book, and a page-scale knob** — every page is letterboxed
  onto a single box, because the reader lays a whole book out in one box and clips
  the pages that do not fit it. It also cuts a strip off every page, from an inset
  that cannot be probed from the browser, so `Scale` (a percentage, 50–100) lets the
  user shrink pages until nothing is lost. It started as a menu of coarse steps; the
  useful values turned out to be a percent or two below 100, so it is a free number.
- **Build identification** — the version on the libraries and login pages is now
  `InformationalVersion`, which the Docker build stamps as `X.Y.Z+pr-34.a1b2c3d` or
  `X.Y.Z+main.a1b2c3d`; a version with no `+` suffix means a release image. Paired with CI
  pushing a `:pr-<n>` image for PRs labelled `test-image`, so a branch can be tried
  on a real device and identified once deployed.
- **SSO / OIDC login** (`OIDC_ENABLED`) — optional second login method through
  the provider ABS itself uses, so a household on SSO needs no separate ABS
  password. ABS's web callback flow demands a same-origin callback, so this drives
  the "mobile" flow ABS offers third-party clients: Inkshelf runs leg 1
  server-side to capture the session cookies its token exchange requires, keeps
  the PKCE verifier and those cookies in a 10-minute encrypted cookie, and
  exchanges the code when the browser returns. No client secret, no new
  dependency. Setup spans three systems (Inkshelf env, ABS's mobile redirect URIs,
  and the ABS client's redirect URIs at the provider), so the README carries it as
  numbered steps with a symptom→fix table. Two constraints found the hard way: the
  ABS whitelist entry cannot carry a port, so SSO needs Inkshelf behind a proxy on
  80/443; and `ABS_PUBLIC_URL` is required when `ABS_URL` is internal, because ABS
  builds its own redirect URL from the host we present.

- **E-reader touch design pass** — every action became a finger-sized bordered
  target (~48px, was ~24px with 5.6px gaps ≈ 0.7mm on an e-ink panel, so all
  three row actions fit under one fingertip). Listing actions moved from a fixed
  8.5rem side column onto their own full-width line below the title, which also
  killed the `max-width: calc(…)` hack that column forced and stopped the long
  German labels wrapping. Regen `↻` left listing rows for the item page — too
  small a target to sit beside Convert when a mistap costs a conversion run. One
  `max-width: 600px` breakpoint for phones; deliberately no device detection.
  Same treatment across the search results (series/authors now use the shared
  `.taplist` block links), the settings form, and the pager, whose Prev button
  now always renders — disabled on page 1 — so it stops moving between pages.

- **Downloaded-file marks** — each download action shows whether *this device*
  already fetched *that file* (`↓`), so working through a batch doesn't mean
  re-downloading or skipping one. Keyed on a device id minted into the settings
  cookie, with marks in a server-side file per device; deliberately not keyed on
  the render target (a variant key, not an identity) or the ABS user (answers the
  wrong question). The old `EPUB ✓` went with it: the label already says `EPUB`
  rather than `Convert`, so the checkmark was decoration, and dropping it leaves
  `↓` as the only glyph in that column. Marks for devices that stop visiting are
  pruned at `ConvertWorker` startup, for any device untouched for 30 days.
- **Converted view sorting** — `/converted` defaults to newest conversion first
  (the one you came to fetch) with Converted / Series / Title / Author sort links.
  The timestamp is the cached EPUB's own write time, exposed as
  `CachedVariant.ConvertedAtUtc` — deliberately not the source-ebook `mtimeMs`
  that the cache filename carries as an invalidation key. Sorting is a two-state
  toggle rather than the library listing's off/asc/desc cycle, because a locally
  sorted list has no server-side default order to fall back to. Filtering and
  paging were considered and left out: the list is per-device and short.
- **Structured settings cookie** — `DeviceSettings` stores a keyed value
  (`retina=1&gray=0&lang=de&fav=lib_x`) instead of a positional string, so adding
  a setting is one key and an absent key falls back to that setting's documented
  default. The favorite library folded into the same cookie, retiring
  `inkshelf_fav_library` and leaving one preferences cookie; legacy positional
  values and the old favorite cookie are still read, so existing devices keep
  their settings.
- **Security test follow-ups** — the gaps left by the hardening work are covered:
  `ConvertLock`'s cancellation path (a queued `AcquireAsync` that gets canceled
  unwinds its ref-count and leaves the semaphore usable), the archive-ceiling
  paths assert the cache dir is empty afterward (no partial `.epub`, no orphan
  `.dl.tmp`), and the preferences cookie's `Set` has the forced-vs-default
  `Secure`-flag pair mirroring `TokenStore`.
- **Item detail page** — a per-item page at `/item/{id}` (reached by the row
  title/cover) showing the full metadata (larger cover, multiple authors/series/
  narrators as filter links, genres, tags, publisher/year, plain description),
  every ebook file with its own download, and — for cbz/cbr files — the Convert
  action. Convert is per-file: the primary uses the item's existing cache entry
  (no `file=`), non-primary files use `/convert/{id}?file={ino}`; the cache key is
  unchanged. Also carries the read/unread toggle. Genre/tag/narrator links jump to
  a filtered library listing.
- **Converted (this device) view** — a `/converted` page listing every comic
  already converted and cached for the current device, across all libraries
  (the cache is enumerated by reverse-parsing filenames and filtered to the
  device's render target). Reuses the listing row (`_ItemRow`), with a metadata
  batch fetch for title/series/author + a series link into each item's library.
  Reached from a link on the home page. Shipped as a single combined view (not
  per-library): `POST /api/items/batch/get` is not library-scoped, so one call
  covers every library and carries `libraryId` per item.
- **Cover image** — the converted EPUB declares a real cover (EPUB3
  `properties="cover-image"` + EPUB2 `<meta name="cover">`), so Apple Books and
  other strict readers show a thumbnail. Prefers the ABS cover art (fetched at
  600px), falling back to the first page when ABS has no usable cover. Metadata
  only — reading still opens on page 1.
- **Background conversion** (PR #9) — conversion runs detached in a background
  worker (app lifetime, keyed by cache path), so a client disconnect can't kill
  it; JS polls a status endpoint, no-JS gets a `<noscript>` meta-refresh.
- **Listing freshness** — `Cache-Control: no-store` on the listing.
- **Regen (↻) feedback** — rides the same status poll as Convert.
- **Conversion memory footprint** — archive spooled to a temp file (not a
  `MemoryStream`) and pages streamed into the EPUB one at a time (only one
  page's bytes held); ImageSharp's pool released per conversion.
- **Runtime footprint (idle)** — Workstation GC + `ConserveMemory` baked into
  the image; measured on-box (resting ~897 → ~554 MiB from GC alone, streaming
  + pool-release cut the rest); container memory-limit guidance added. Further
  trimming was considered and dropped: GC config carried the bulk,
  `InvariantGlobalization` measured only ~4 MiB and cost `CultureInfo`, and
  `PublishTrimmed` risks reflection breakage in Razor Pages for little gain.
- **Per-device settings + retina/grayscale** — a server-written
  `inkshelf_settings` cookie (`DeviceSettings`) with a plain-`<form>` Settings
  page (cog link in the Index/Library heads) exposing a **retina** toggle
  (replaces the hard-coded `ScreenTarget.Retina`) and a **grayscale** toggle.
  Both flow through a `RenderTarget` into conversion + the cache key (grayscale
  `-g` marker); includes the retina dpr clamp-after-multiply + dpr bound fix.
- **Read-state toggle** — per-row Mark read / ✓ Read on listing + search rows,
  synced to ABS media progress (`GET /api/me` finished-set; `PATCH
  /api/me/progress/{id}` `{isFinished}`).
- **Conversion failure reasons** — a failed convert records a reason category
  (TooLarge / DownloadFailed / BadArchive / ConvertError) on its transient queue
  entry; oversized archives are rejected before download. The row's "why?" link
  (and the poll-JS auto-nav on failure) opens a plain-HTML `/convert/{id}/why`
  page explaining the failure — actionable for TooLarge ("archive is X, over the
  Y limit"). Failure log lines carry the item title. All strings localized.
- **UI localisation (German)** — Inkshelf's own chrome (nav, breadcrumbs, row
  actions, pager, login/settings forms, empty states) is translated via a
  lightweight file-backed JSON catalog keyed by the source English string, loaded
  at startup. Language is per-device (`DeviceSettings` + a Settings dropdown),
  defaulting from the browser's `Accept-Language` with English as the per-string
  fallback. No `CultureInfo`, no new dependency; a new language is a `<lang>.json`
  drop-in plus a restart. ABS content (titles, descriptions) is untouched.
