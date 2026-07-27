# Roadmap

An unordered backlog of work to build. Shipped items move to **Done** at the
bottom as a short record; the changelog is the authoritative history of what
has shipped.

## Priority (my current focus)

No priority features atm.

## Settings

Settings to add to the per-device settings system:

- **Resolution override.** Let the user hand-set the conversion resolution per
  device, for when the browser-reported screen size isn't ideal. Pairs with the
  retina toggle.
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
  at once), so this is deferred in favour of the low-memory serial path shipped
  under Runtime footprint.
- **Disable conversion via config.** An `AbsOptions` flag / env var (e.g.
  `CONVERSION_ENABLED`, default `true`, mirroring `DIAG_ENABLED`) to turn the
  whole CBZ/CBR→EPUB system off — for e-readers that read comic archives natively
  and only want the raw download. When off: hide the Convert / EPUB ✓ / ↻ actions
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

  **Leading hypothesis: `display: "standalone"` in `wwwroot/site.webmanifest`.**
  Launched from a home-screen shortcut, a standalone web app renders without
  browser chrome — and the "save password?" prompt *is* chrome, so the store is
  never asked. The timeline fits: the manifest arrived in `c57aa9a`, after the
  point the user remembers saving working. **Discriminating test:** on the
  device, open the site by typing the URL into a normal browser tab instead of
  using the shortcut. Prompt appears → confirmed.

  If confirmed, it's a **tradeoff, not a bug fix**: `display: "browser"` keeps
  the manifest's icon and name but launches in a normal tab, restoring the
  prompt at the cost of the chrome-free reading area that standalone buys on an
  e-ink screen. Decide which is worth more before changing it.

  Otherwise the remaining candidates are the device's own password-saving
  setting or a site allow-list in its store (free to check first), then
  `<button type="submit">` vs `<input type="submit">` — some very old engines
  only treat the latter as a login submit, and swapping it touches styling.
- **Mark files as already downloaded (per device).** Track which files this device
  has downloaded and show a marker on the row, so you can tell at a glance whether
  you already grabbed volume 4 and don't fetch it twice. Covers both raw ebook
  downloads and converted EPUBs. Distinct from the shipped read/unread toggle:
  that is per-*user* ABS media progress and means "I read this", whereas this is
  per-*device* and means "the file is on this reader".
  Promising approach, no JS needed: the download itself is a plain `<a>` hitting
  our own endpoint, so the download **response** can `Set-Cookie` and append the
  id — automatic rather than a thing you have to remember to tick, which is the
  whole point. Key on item id plus file ino, since the detail page offers
  per-file downloads and a multi-file item needs per-file marks.
  Open question is the cookie ceiling: ~4 KB, ABS ids are UUIDs, and the cookie
  rides every request on a slow e-ink connection. Likely wants a short id hash
  (first ~8 hex chars) plus a rolling cap with FIFO eviction — a "recently
  downloaded" window, not a permanent ledger. Keep it in its **own** cookie, not
  the settings one: settings are small and stable, this list grows and churns.
- **Sort the Converted view, newest first.** `/converted` currently lists cached
  EPUBs in whatever order the cache enumeration yields. Default it to
  **converted-at descending** so the most recently converted comic is at the top —
  that's the one you actually came to fetch. Then expose the other useful orders
  (title, series, author) as sort links; `Pages/Support/SortLinks.cs` already does
  this for the library listing, so reuse it rather than rolling a second
  mechanism. The sort key is available without extra ABS calls: the cache
  filenames carry the item id and the files carry an mtime, and the view already
  batch-fetches title/series/author for its rows.
  Considered and deliberately left out: **filtering** (by series or otherwise) —
  the list is per-device and short, so newest-first plus the existing sort links
  should be enough, and a filter UI costs more than it saves; and **paging** —
  for the same reason. Revisit either only if a real cache grows big enough to
  make scrolling painful.
- **Screenful pagination (investigation).** Spike whether we can size a page to
  exactly one screenful instead of a fixed 10. The `scr` cookie already reports
  the viewport (CSS w×h×dpr), so server-side we could compute
  `pageSize ≈ floor((viewportHeight − chrome) / rowHeight)`. Motivation: a typical
  e-ink reader fits only ~7 rows and scrolling is cumbersome, so "one page = one
  screen, no scroll" would be much nicer. Open questions: variable row heights
  (multi-author/series wrap), the first load before the cookie is set, and how
  this interacts with search results. Decide feasibility + approach before
  committing.

## Runtime footprint

- **Baseline trim.** Smaller idle wins beyond the GC + streaming work already
  shipped: disabling unused ASP.NET Core features / logging providers,
  `PublishTrimmed`. Native AOT is off the table (CLAUDE.md); GC configuration
  carried the bulk. (`InvariantGlobalization` was measured at ~4 MiB resident on
  this app and dropped — not worth losing `CultureInfo`; UI localisation was
  pursued instead, see Done.)

## Done

Shipped; kept as a short record (full detail in git history / the PR).

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
  + pool-release cut the rest); container memory-limit guidance added. Baseline
  trim remains (see backlog).
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
