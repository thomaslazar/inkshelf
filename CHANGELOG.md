# Changelog

All notable changes to Inkshelf are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

## v1.0.0 - 2026-09-10

First stable release. The settings cookie stays backward compatible, so a
device that has been running an earlier version keeps its settings and needs no
attention after the upgrade.

### Highlights
- Marking a book read or unread no longer reloads the page. Without JavaScript
  it still works, and now returns to the row you tapped instead of the top of
  the list.
- New per-device setting, default off: **Return to the list after a download.**
  For readers whose browser is killed when their book reader app takes the
  foreground, which loses the page you were on.
- New per-device setting, default 10: **Items per page**, from 5 to 50, for
  both the library listing and the converted page. The converted page also
  gained a pager, so it no longer renders every converted book in one list.
- An expired session no longer makes the read button lie. It used to report a
  book as read when nothing had been saved; it now sends you to the login page.

### Features
- feat: add an items-per-page setting
- feat: add a return-to-the-list-after-download setting
- feat: answer 204 to an xhr read toggle
- feat: mark read without reloading the page
- feat: mark the download links that serve a file
- feat: page the converted list
- feat: page the library listing at the configured size
- feat: return to the listing after a download
- feat: return to the tapped row when marking read

### Fixes
- fix: agree subject and verb in a German items-per-page string
- fix: arm data-warm download anchors once ready
- fix: correct dlreturn uicheck comments and harden replace guard
- fix: do not redirect an xhr read to the login page
- fix: fall back to a real post if the read script throws
- fix: guard dlreturn replace against a protocol-relative path
- fix: keep the converted page's pager in the row return URL
- fix: keep the items-per-page warning inside its own paragraph
- fix: remove a stray razor brace from the settings page
- fix: restore read label before 401 redirects to login

### Internal

Refactors:
- refactor: dedupe the read button's tooltip and label strings
- refactor: extract the read button into one partial

Tests:
- test: add no-JS round trip check for the read form
- test: cover the dlreturn arming half and fix wait ordering
- test: cover the read button's XHR failure-revert path
- test: exercise the read button's live click path in uicheck
- test: pin xhr binding and antiforgery on the read endpoint
- test: strengthen converted pager and ticket-count assertions

Docs:
- docs: correct the spec's inverted rule for data-warm anchors
- docs: document the items-per-page setting
- docs: document the return-after-download setting
- docs: fix a razor quoting bug in the plan snippet
- docs: fix stale spec claims and add the missing map entry
- docs: gate only the script, not the markers
- docs: list IPagedListing in the Support code map
- docs: match the file's quote-only convention for a setting name
- docs: name the two lists the setting sizes, not both
- docs: note the read button's out-of-order response ceiling
- docs: plan marking read without a reload
- docs: plan returning to the list after a download
- docs: plan the items-per-page setting
- docs: record the absolute read-state invariant
- docs: spec a per-device items-per-page setting
- docs: spec marking read without a reload
- docs: spec returning to the list after a download
- docs: stop calling the converted page size fixed
- docs: trim an unproven claim from the LiveCount comment

Chore:
- chore: bump version to 1.0.0

## v0.7.0 - 2026-08-31

### Highlights
- **Enlarge small pages.** A new per-device setting, off by default, for readers
  that leave a small comic small. Page images were only ever shrunk to fit, never
  enlarged, so a comic whose scans are smaller than the screen was drawn small
  with dead margin around it on any reader that sizes a page from the image
  itself. Tick the setting and the pages are resampled up to the screen instead.
  Files get bigger, which is the trade.
- **The FAQ no longer gives advice that cannot work.** It used to say retina or
  the screen override would enlarge small pages. Neither can: both only raise a
  ceiling, and a scan already below it is untouched. The entry now points at the
  new setting, and the neighbouring entry about the opposite cause, scans larger
  than the screen, is distinguished from it.

### Features
- feat: add the enlarge-small-pages checkbox
- feat: carry the upscale flag in the device settings
- feat: grow the page box when upscaling is on
- feat: key the epub cache on the upscale flag
- feat: let the page processor enlarge undersized pages

### Fixes
- fix: do not upscale when there is no screen cap

### Internal

Docs:
- docs: correct a rounded test value in the plan
- docs: correct upscale spec claims and add FAQ note
- docs: distinguish oversized from undersized scans in FAQ
- docs: document the enlarge-small-pages setting
- docs: name inkshelf, not the reader, as what shrinks scans
- docs: plan the enlarge-small-pages setting
- docs: reword the enlarge-small-pages label
- docs: spec the enlarge-small-pages setting

Tests:
- test: pin the upscale checkbox's checked binding

### Notes

Nothing already cached is invalidated: the new setting is part of the cache
filename, and existing files carry no marker and are read as not upscaled, which
is what they are. Turning the setting on converts afresh.

The setting only matters on readers that ignore the page size a book declares.
Readers that honour it already enlarge small pages themselves, for free, and see
an unchanged layout either way.

## v0.6.1 - 2026-08-25

### Highlights
- The libraries page now names the ABS account the device is signed in as, next
  to the version string. On a shared install there was previously no way to tell
  which account a reader was using.

### Features
- feat: keep the logged-in username in the session cookie
- feat: name the logged-in user on the libraries page

### Fixes
- fix: drop the em dash from the version line
- fix: no-store the libraries page and de-dup a test cookie helper

### Internal

**Tests**
- test: assert the username itself round-trips past a newline

**Docs**
- docs: drop the last references to a locale file that does not exist
- docs: fix rollback, citations, and roadmap for the logged-in user
- docs: forbid em dashes and purge them from this branch
- docs: name the banned characters by codepoint
- docs: plan the logged-in user display
- docs: spec showing the logged-in user on the libraries page

**Chore**
- chore: bump version to 0.6.1
- chore: hyphenate the two user-facing strings and widen the rule
- chore: kill em and en dashes in code comments
- chore: kill em and en dashes in the design docs
- chore: kill em and en dashes in the docs and readme
- chore: kill em and en dashes in the tooling
- chore: kill em dashes in the compose example

## v0.6.0 - 2026-08-25

### Highlights
- Large downloads now finish on e-readers whose download manager re-fetches the
  link without the browser's cookies - previously they arrived empty or as the
  login page saved under the book's name.
- Converted comics fit the reader's page instead of landing in a corner or losing
  a strip off the bottom.
- A device whose browser reports the wrong screen size can be calibrated by hand,
  and a reader that forgets its cookies gets that calibration back from a single
  bookmark instead of re-measuring.
- Library listings open newest-first, and the breadcrumb links back to the
  unfiltered listing so one tap clears a filter or a search.
- One log line per request, which is what makes a download that failed on a
  reader diagnosable at all.

### Features
- feat: add the download ticket table
- feat: add the screen override controls to the settings page
- feat: honour the screen override wherever a conversion target is built
- feat: land on a bookmarkable url after saving settings
- feat: let a screen override take precedence over the scr probe
- feat: let the reader pick the split and rotate direction
- feat: link the library breadcrumb to its own unfiltered listing
- feat: log NOCOOKIE marker for cookie-less file requests
- feat: log one line per request
- feat: make the page scale a number instead of a menu
- feat: measure CSS support in the probe, not just ask
- feat: mint a download ticket into every download link
- feat: restore device settings from a bookmarked url
- feat: say that the settings page can be bookmarked
- feat: serve a converted comic from a download ticket
- feat: serve a raw ebook download from a download ticket
- feat: sort library listings newest first by default
- feat: store a hand-entered screen override in the settings cookie

### Fixes
- fix: address final review findings on resolution override
- fix: answer file requests with 401 instead of the login page
- fix: close final-review gaps in bookmarkable settings
- fix: compare-and-remove expired download tickets
- fix: correct stale AbsDownloadClient request-path invariant
- fix: declare the size of an ebook download
- fix: fit the page inside the reader, not just to its width
- fix: give every page of a comic one size, with a scale knob
- fix: handle two-page spreads in CBZ conversion
- fix: keep the header on one line without unprefixed flex
- fix: keep the sort cycle intact under the new default
- fix: lay out rows on an engine without unprefixed flex
- fix: mint /convert's device id lazily, not on every poll
- fix: offer Automatic in the language list, and Save twice
- fix: put the pixel ratio in the epub cache key
- fix: read bearer after abs calls, guard filename, cover /converted tickets
- fix: read the bearer after the abs calls, not before
- fix: report the detected screen in device pixels, fields on one line
- fix: round the pixel ratio to four decimals
- fix: say when override numbers are out of range instead of dropping them silently
- fix: scale the declared viewport to the cap so low-res scans fill the screen
- fix: see a mid-request token refresh, and fall through on a dead ticket
- fix: size comic pages relative to the reader, not in pixels
- fix: space the logout button with padding, not an auto margin

### Internal

**Refactors**
- refactor: drop the bearer-deferral machinery and three dead guards
- refactor: make retina apply under an override instead of disabling it
- refactor: parse settings from a query or the cookie with one parser
- refactor: report content type and length from the ebook download
- refactor: return the cache path the row state was keyed on
- refactor: share the epub download name and the device id mint
- refactor: trim the comments to the rules

**Tests**
- test: assert download links carry a ticket
- test: catch render-only query settings on the listing page
- test: close two gaps the final review found
- test: cover ticket re-stamp and cache eviction, tighten guard
- test: discriminate content-type parsing and dispose stream
- test: pin query settings to the settings page
- test: pin raw-download ticket item-id, length, and device-id rules
- test: pin the epub-name blank guard and convert's lazy did mint

**Docs**
- docs: add a device support matrix
- docs: add the vision 5 and page 2 to the matrix
- docs: bound the container log without pinning the driver
- docs: bring the matrix in line with the shipped page geometry
- docs: correct RowFor fallback comment about _states
- docs: correct how a changed override reconverts
- docs: cut the matrix back to the matrix, move symptoms to a FAQ
- docs: describe small pages without diagnosing the cause
- docs: describe the page scale as the free number it is
- docs: drop the conjectured download answer
- docs: drop the guessed page scale from the FAQ
- docs: file the ticket invariant under downloads, correct the spec
- docs: fill in the vision 5 and page 2 panel resolutions
- docs: fix a namespace trap and a contradiction in the plan
- docs: fix the ambiguous QueryCollection line in the plan
- docs: fold each device's notes into the matrix as a spanning row
- docs: give the browser engines their own section
- docs: keep the roadmap entry to user-facing value
- docs: key the matrix on firmware and per-reader page scale
- docs: make the server log the primary way to retrieve a probe
- docs: mark unpublished panel resolutions rather than guessing
- docs: measure the cookie on 16.2.0 instead of inferring it
- docs: name a restart as a cause of stale download links
- docs: name the two tolino readers standard and beta
- docs: note that the shine's layout degrades
- docs: note what the resolution override has to override
- docs: plan bookmarkable settings implementation
- docs: plan the download ticket implementation
- docs: plan the resolution override implementation
- docs: point device reports at the issue tracker
- docs: record all three 16.2.0 probes and that they share one profile
- docs: record bookmarkable settings
- docs: record retina as required on the standard reader
- docs: record that the download manager never resumes
- docs: record that the shine retains no cookies
- docs: record the resolution override
- docs: record the shine probe and how to retrieve one
- docs: record the two tolino reader engines and the 98% recommendation
- docs: record what the measured probe found on the shine
- docs: refresh the device screenshots and add the settings page
- docs: reorder Bookmarkable settings entry to lead with user benefit
- docs: reword the screen override strings in both languages
- docs: say when a bookmark is worth making
- docs: separate panel, detected and working resolutions
- docs: separate the observed handoff from the inferred cookie behaviour
- docs: soften the shine's reader to what was observed
- docs: spec bookmarkable device settings
- docs: spec download tickets for cookie-less download managers
- docs: spec the resolution override setting
- docs: spell out the restart flow in the settings spec
- docs: split the tolino specifics out of the device matrix
- docs: state how close the detected numbers run to the panel
- docs: the beta reader is a device setting, not a per-book choice
- docs: tighten the resolution override spec
- docs: trim the matrix notes to what the columns cannot say

**Chore**
- chore: bump version to 0.6.0
- chore: drop a duplicate using in DeviceSettings

## v0.5.0 - 2026-07-30

### Highlights

- **Log in with SSO.** If your Audiobookshelf server is set up with an OIDC
  provider (Authentik, Keycloak, Pocket ID, …), Inkshelf can now offer that same
  login next to the password form - so people on a shared server no longer need a
  separate ABS password. Off unless you set `OIDC_ENABLED=true`, and password login
  is unchanged. Inkshelf reuses ABS's own OIDC client: no client ID, no client
  secret, and it never sees your provider password.
- **Setup spans three systems**, so the README walks it as numbered steps with a
  symptom → cause → fix table: environment variables on Inkshelf, Inkshelf's
  callback URL in ABS, and ABS's mobile redirect URI at your provider. Note that
  the URL registered in ABS cannot contain a port, so SSO requires Inkshelf served
  through a reverse proxy on 80/443.

New configuration: `OIDC_ENABLED`, `OIDC_PROVIDER_NAME`, `ABS_PUBLIC_URL` (needed
for SSO only when `ABS_URL` is an internal address). Nothing existing changed, so
upgrading without touching your configuration keeps today's behaviour.

### Features

- feat: add the oidc config flags
- feat: add the oidc login endpoints
- feat: drive the abs oidc mobile flow from the auth client
- feat: match the login buttons and show the build on /login
- feat: offer sso on the login page
- feat: stamp non-release builds with branch and sha in the version
- feat: store the oidc flow in an encrypted cookie

### Fixes

- fix: fail fast on a malformed ABS_URL or ABS_PUBLIC_URL
- fix: present abs's public host when starting the oidc flow

### Internal

**CI**

- ci: push a pr image when the pr is labelled test-image

**Tests**

- test: assert the sso button in the browser pass

**Docs**

- docs: add OIDC login design spec
- docs: add the oidc login implementation plan
- docs: document sso login
- docs: stop naming a specific version in the build-stamp examples

**Chore**

- chore: bump version to 0.5.0

## v0.4.1 - 2026-07-29

### Highlights

- **The logout button sits beside the settings cog again.** v0.4.0's header work
  moved it across the header to the title; it belongs with the other controls on
  the right.
- **The search bar reliably gets its own line.** It already landed there most of
  the time, but only because the header ran out of room - so a short library name
  could have squeezed it up beside the breadcrumb, and a long one could have
  pushed the cog down instead. The breadcrumb row now keeps the same shape
  whatever your library is called.

### Fixes

- fix: give the search bar its own line by design
- fix: put the logout button back beside the cog

### Internal

- chore: bump version to 0.4.1

## v0.4.0 - 2026-07-29

### Highlights

- **The whole UI is now sized for finger taps.** Actions were text links about
  24px tall with 5.6px between them - on a 6" e-ink panel that put the three
  actions on a listing row roughly 3.6mm apart, well inside a fingertip. Every
  action is now a bordered target around 48px, and listing actions moved out of
  the cramped side column onto their own full-width line under the title.
- **You can see which files this device already downloaded.** A `↓` on the
  Download and EPUB actions marks what this reader has already fetched, so
  working through a batch no longer means guessing.
- **The converted view sorts.** Newest conversion first by default, with sorting
  by series, title or author.
- **Phones get a usable layout.** One width breakpoint stacks the row actions
  full-width below 600px. No device sniffing involved - the same finger-sized
  design is simply correct everywhere.
- Existing settings survive the upgrade: the preferences cookie changed shape
  internally, but the old format and the old favourite-library cookie are still
  read and migrated.

### Features

- feat: add a per-device downloaded-file mark store
- feat: expose when a cached epub was converted
- feat: mint a per-device id in the settings cookie
- feat: record a mark when a file is downloaded
- feat: show which files this device already downloaded
- feat: size the whole UI for finger taps on an e-reader
- feat: sort the converted view, newest conversion first

### Fixes

- fix: refine the touch pass from the on-device round
- fix: serialize mark writes so concurrent downloads don't lose marks
- fix: show the applied sort direction on the converted view
- fix: stop the poll script claiming a conversion was downloaded
- fix: translate retina page cost as file size, not weight
- revert: drop the speculative login autofill attributes

### Internal

Refactors:

- refactor: evict the epub cache FIFO, not touch-on-serve
- refactor: key the settings cookie instead of packing it positionally
- refactor: move the favorite library into the settings cookie
- refactor: read the favorite from the settings cookie everywhere

Tests:

- test: close the gaps the branch review found
- test: cover the converted sortbar; docs: record the sorting
- test: cover the per-file download mark, and unbreak dotnet format
- test: cover the security hardening follow-ups
- test: discriminate the cached state by title, not a bare EPUB match
- test: fix the smoke test's convert checks
- test: make the series fixture actually exercise the sequence key

Docs:

- docs: accept and pin the descending series grouping flip
- docs: correct the plan's expected test counts
- docs: correct why marks survive cache eviction
- docs: cut ARCHITECTURE.md back to a map, codify what belongs
- docs: distinguish raw from converted marks in the spec
- docs: drop baseline trim from the backlog
- docs: drop the no-AOT asides
- docs: fold removing touch-on-serve into the sorting plan
- docs: make the clobber-hazard test a real end-to-end red
- docs: match the pruning and cache-path docs to the code
- docs: narrow the password-store spike to the standalone manifest
- docs: note the manifest experiment was tried and didn't help
- docs: plan per-device downloaded-file marks
- docs: plan sorting for the converted view
- docs: plan the structured settings cookie refactor
- docs: point testing at the local stack and uicheck
- docs: queue a per-device downloaded-file marker
- docs: queue a spike into credentials not reaching the password store
- docs: queue newest-first sorting for the converted view
- docs: record the downloaded-file marks
- docs: record the single keyed preferences cookie
- docs: retire the EPUB checkmark in the marks spec
- docs: rule out standalone display in the password-store spike
- docs: spec per-device downloaded-file marks
- docs: spec sorting for the converted view
- docs: spec the e-reader touch design pass
- docs: spec the structured settings cookie refactor

Chore:

- chore: bump version to 0.4.0

## v0.3.0 - 2026-07-24

### Highlights
- **German UI localisation.** Inkshelf's own chrome (navigation, breadcrumbs, row actions, pager, login/settings forms, empty states) is now translatable. Language is per-device, chosen in Settings or defaulted from the browser's `Accept-Language`, with English as the fallback. New languages drop in as a JSON file plus a restart - no rebuild.
- **Conversion failure reasons.** A failed comic conversion now explains *why* on a plain-HTML page - too large (with the actual size vs the limit), unreadable archive, download failure, or unexpected error - instead of a bare "Convert (retry)". Oversized archives are now rejected before downloading.
- **No more blank page from a stale favorite.** A favorite-library cookie left over from a different Audiobookshelf server no longer produces a blank 500; it's validated and cleared, falling back to the library list.
- **Higher default conversion limits** - archive 1 GiB, cache 5 GiB.
- **Touch- and e-reader-friendly polish** - a larger, better-spaced libraries list, and Failed-row actions that wrap correctly on narrow e-ink screens.

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

## v0.2.1 - 2026-07-21

### Highlights
- The header icon (left of "Libraries") is now a link back to the libraries
  list, on every page.
- The libraries page shows the deployed Inkshelf version, so you can tell which
  build is actually running.

### Features
- feat: link the header icon to libraries and show the version

## v0.2.0 - 2026-07-21

### Highlights
- **Per-device settings** - a Settings page (cog link in every header) with
  **retina** and **grayscale** toggles that flow through comic conversion and the
  cache, so each e-reader gets pages tuned to its screen.
- **Read / unread tracking** - mark items read from the listing, search, and
  detail rows, synced to your Audiobookshelf progress.
- **Real EPUB covers** - converted comics embed a proper cover (the ABS cover
  art, falling back to the first page) instead of a blank placeholder.
- **Item detail page** - a per-item page with full metadata (author, series,
  narrators, genres, tags, description - all filterable), every downloadable file,
  and a per-file convert action.
- **"Converted on this device" view** - one page listing every comic already
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

## v0.1.2 - 2026-07-17

### Highlights
- **Much lower memory use during and after conversions.** Comic conversion no
  longer buffers the whole archive and every page in RAM - the download is
  spooled to a temp file and pages are streamed into the EPUB one at a time, so
  only a single page is held. Combined with Workstation GC (which hands memory
  back to the OS), the sidecar no longer ratchets up to ~900 MiB and stay there
  after a batch; it returns to near-idle.
- **The per-conversion peak is bounded by one page**, so even large comics stay
  modest - safe on a memory-constrained host.
- Operators can now set a **container memory limit** (see the compose example /
  README); with the lower footprint it can be kept tight.
- No change to converted-EPUB output - byte-identical to before.

### Performance
- perf: stream pages into the EPUB instead of buffering all
- perf: spool archive to temp file and release ImageSharp pool per job
- perf: use Workstation GC + conserve memory for the sidecar

### Fixes
- fix: make convert temp-file cleanup best-effort in finally

## v0.1.1 - 2026-07-16

### Highlights
- **Comic conversion no longer dies on slow hosts.** CBZ/CBR→EPUB conversion now
  runs in a background worker instead of inside the web request, so a client
  disconnect (a timed-out tab, navigating away) can no longer cancel it
  mid-flight. On a low-powered box, a large comic that previously *never*
  finished now converts reliably and stays cached.
- The listing shows live progress - "Converting…" flips to "EPUB ✓" via a small
  status poll, with a no-JavaScript fallback (a periodic refresh) for old e-reader
  browsers.
- Regenerate (↻) and repeated taps behave cleanly - no duplicate rows, no
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

## v0.1.0 - 2026-07-15

First tagged release of Inkshelf - a thin, server-rendered web client for the
Audiobookshelf (ABS) API, built for e-reader browsers with near-zero JavaScript.
Ships as a multi-arch container image at `ghcr.io/thomaslazar/inkshelf`.

### Highlights
- **Browse an ABS library** from a plain-HTML client: search, author/series
  filters, cycling sort links, top/bottom pagination, and a favorite-library
  shortcut - `<form>`/`<a>` only, no client JS required.
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
