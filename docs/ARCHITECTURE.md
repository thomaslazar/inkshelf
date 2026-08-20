# Inkshelf Architecture

A thin, server-rendered web client for the Audiobookshelf (ABS) API, built to run
on old e-reader browsers. This describes the steady-state structure and the
invariants that hold it together. **Read it before adding features** — several
conventions look like cleanup targets but are load-bearing.

It is deliberately not a record of what shipped. See `CHANGELOG.md` for history
and `docs/ROADMAP.md` for what's left.

## Big picture

ASP.NET Core, .NET 10. Razor Pages render HTML; minimal-API endpoints
serve streams and actions. Stateless — the ABS JWT lives in an encrypted cookie
(Data Protection), so there is no server-side session to scale or persist.

Near-zero client JavaScript, because the target browsers are old e-ink reader
engines. Everything is plain `<form>` and `<a>`.

Two things shape most of the design: **every page is one round trip to ABS**, and
**CBZ/CBR→EPUB conversion is slow and memory-hungry**, so it runs in a background
worker with an on-disk cache rather than in the request.

## Code map

```
src/Inkshelf/
  Program.cs            Bootstrap only: config → DI → middleware → endpoint maps.
  AbsOptions.cs         Typed view of all config, read once at startup.
  Abs/                  ABS API access.
    AbsAuthClient.cs      Login + refresh + the two OIDC legs. Handler-FREE typed client.
    AbsApiClient.cs       The data methods. Typed client WITH AbsAuthHandler.
    AbsAuthHandler.cs     DelegatingHandler: injects Bearer, refresh-on-401-retry.
    AbsDownloadClient.cs  Handler-free authenticated download, for the worker.
    AbsModels.cs          Response DTOs (three separate metadata shapes — see below).
    AbsFilter.cs          Encodes ABS facet filters (authors.<b64>, series.<b64>).
    AbsExceptions.cs      Auth / Unauthorized / LoginFailed.
  Auth/                 TokenStore (encrypted session cookie), Tokens,
                        OidcFlowStore (short-lived encrypted OIDC flow cookie),
                        DeviceSettings (per-device prefs + favorite, one cookie).
  Convert/              CBZ/CBR → fixed-layout EPUB.
    ConvertService.cs     Orchestrates the /convert kick (detail → validate → cache → enqueue).
    ConvertQueue.cs       In-memory job registry + Channel producer (singleton).
    ConvertWorker.cs      BackgroundService: drains the queue, downloads + converts.
    ConvertLock.cs        Keyed lock serializing jobs for one cache target.
    EpubConverter.cs      Thin orchestrator: reader → processor → writer.
    ComicArchiveReader.cs Yields image entries in ordinal order.
    PageImageProcessor.cs Decode + downscale + transcode + optional grayscale.
    EpubWriter.cs         Writes the EPUB zip (string-built XML).
    EpubCache.cs          File cache keyed by item + source size/mtime + render target.
    ScreenTarget.cs       Parses the "scr" probe + settings into a RenderTarget.
    RenderTarget.cs       Resolved per-device render knobs (cap, dpr, grayscale).
  Endpoints/            Minimal-API groups, one static MapXxxEndpoints() each:
                        Cover, Download, Convert, Read, Session, Settings, Diag, Oidc.
  Localization/         File-backed JSON catalog keyed by the English source string.
  Pages/                Razor Pages (+ models); Shared/ partials.
    Support/            Non-page helpers: LibraryLinks, ItemRowModel, Pager,
                        SortLinks, ConvertRowStateResolver, ConvertActionModel.
```

Tests live in `tests/Inkshelf.Tests/`, roughly one file per unit. `dotnet test`
from the repo root (inside the devcontainer) must stay green, and
`tools/uicheck/run.sh` is the headless-browser pass for UI changes.

## Invariants (do not "clean these up")

**ABS access**

- **Three ABS clients, not one.** `AbsAuthClient` (login/refresh) has **no** auth
  handler; `AbsApiClient` (data) runs through `AbsAuthHandler`. That split is what
  makes refresh-on-401 impossible to recurse. `AbsDownloadClient` is the third:
  also handler-free, because the background worker has no `HttpContext` for the
  handler to resolve a token from — it carries a bearer captured at kick time and
  does not refresh. Never attach `AbsAuthHandler` to either handler-free client,
  never put login/refresh on `AbsApiClient`, and never use the download client
  from a request path.
- **`AbsAuthClient`'s handler must keep `AllowAutoRedirect = false` and
  `UseCookies = false`.** OIDC leg 1 reads the `Location` off ABS's 302, which
  following the redirect destroys; and the handler is pooled process-wide, so a
  `CookieContainer` would pool every user's ABS session in one jar. The OIDC legs
  pass ABS's cookies as headers for exactly that reason.
- **`EnableSourceControlManagerQueries=false` stays in the csproj.** Without it the
  SDK appends the full 40-char HEAD sha to `InformationalVersion`, which the
  libraries and login pages display. The build stamps `SourceRevisionId`
  deliberately instead, so a bare version means a release build.
- **SSO uses ABS's OIDC *mobile* flow, never its web callback flow.** The web flow
  validates the callback as same-origin with ABS, so it can never work from a
  sidecar on its own hostname. The mobile flow's token exchange needs the cookies
  from its first leg, which is why Inkshelf performs that leg server-side.
- **`AbsAuthHandler` resolves scoped services inside `SendAsync`**, from
  `HttpContext.RequestServices`. It must not constructor-inject `TokenStore` or
  `AbsAuthClient` — `IHttpClientFactory` pools the handler for longer than a
  request scope.
- **The 401 retry copies the request headers.** This preserves `User-Agent`,
  which the reverse proxy in front of ABS requires — it 403s an empty UA. Losing
  it is a production outage. The body is buffered so the retry re-sends
  identically, which is why streaming uploads aren't supported.
- **Three separate metadata DTOs** (`AbsMetadata`, `AbsBatchMetadata`,
  `AbsDetailMetadata`). ABS reuses the `series` JSON key with different types per
  endpoint (object vs array); unifying them reintroduces a deserialization bug.

**Rendering**

- **Razor Pages for HTML, minimal APIs for streams and actions.** Keep the split.
- **`LibraryLinks` is the single URL authority.** Don't rebuild library URLs in a
  view.
- **Near-zero JS.** Only two inline scripts exist (`_Layout.cshtml`: the screen
  probe, the convert-warm XHR). Anything touching them needs a real-device test
  before merge. CSS stays defensive — no `object-fit`, no flex `gap`.
- **No device-class detection; layout branches on width alone.** `@media
  (monochrome)` reports 0 on e-ink, `(update: slow)` postdates the target engine,
  and UA sniffing only ever knows the readers we enumerated — this project has
  users on hardware we have never seen. The e-reader design *is* the design:
  finger-sized targets and high contrast are right everywhere, so the base layout
  is touch-first and one `max-width` breakpoint handles narrow screens. The `scr`
  cookie sizes converted comic pages and must not grow into a layout switch.
- **No local read state.** "Read" is ABS media progress, fetched per render and
  toggled through to ABS. A failed fetch degrades to all-unread rather than
  failing the page.

**Conversion**

- **The request only kicks; the worker converts.** `ConvertWorker` runs on the
  application lifetime, never a request token, so a client disconnect can't
  cancel an in-flight conversion.
- **"Done" is the atomic existence of the `.epub`** (temp file, then rename). The
  queue holds only the transient Queued/Running/Failed states, in memory and
  never persisted — so a restart drops pending rows back to "no job" and the next
  tap re-enqueues. There is nothing to reconcile.
- **`ConvertLock` serializes one cache target**, with a double-checked
  `File.Exists`, so concurrent jobs can't double-convert or corrupt the temp file.
- **Memory footprint is independent of archive size.** The archive is spooled to
  disk, pages stream through one at a time, and the image pool is released
  between jobs. Don't reintroduce whole-archive or whole-book buffering.
- **The cache key excludes the file ino.** A per-file convert
  (`/convert/{id}?file={ino}`) still keys on that file's size+mtime, so the
  primary ebook's entry is the same one the listings write — which is why the
  "converted" badge agrees across pages.
- **Convert-row state is computed in one place** (`ConvertRowStateResolver`), for
  the same reason.
- **Cache eviction is FIFO by conversion time, not LRU.** Nothing re-stamps a
  served file, so a cached EPUB's write time *is* its conversion time (which
  `/converted` also sorts on). This cache bridges one expensive conversion to one
  download; touch-on-serve would protect volumes already on the reader and evict
  the ones not yet fetched.
- **String-built EPUB XML** in `EpubWriter` — verbose but dependency-free and
  epubcheck-clean. Don't swap in an XML library.
- **Workstation GC, not Server GC.** `Inkshelf.csproj` pins
  `ServerGarbageCollection=false` (plus non-concurrent GC and `ConserveMemory`).
  A mostly-idle sidecar doing sequential, CPU-bound conversions wants one compact
  heap that hands memory back, not per-core heaps sized for throughput.

**Per-device state**

- **Two device cookies, two purposes.** `scr` is JS-written device *truth* (the
  screen probe); `inkshelf_settings` is server-written user *choice*. Anywhere a
  conversion target is computed, read **both** and combine them via
  `ScreenTarget.FromCookie` into a `RenderTarget` — otherwise a real conversion
  and the badge that describes it disagree. **Every knob that changes the bytes is
  part of the cache key** — size cap, grayscale, spread mode, page scale, dpr — or
  the user flips a setting and is handed the old file, which reads as "the setting
  is broken".
- **A hand-set screen override wins over the probe, and is consulted first.**
  `ScreenTarget.FromCookie` returns early when the `scr` cookie is missing, so an
  override checked later would never be reached in exactly the case it exists for.
  `retina` is not consulted while an override is active — it only chooses between
  the CSS size and CSS × dpr, and both are explicit.
- **A disabled input is not submitted.** The settings form disables fields it does
  not want used, so the POST handler treats an absent field as "keep what is
  stored" for those — otherwise saving would silently zero the override numbers, or
  turn retina off, since absent normally means off for a checkbox.
- **One page size per book, and it is load-bearing.** The e-reader lays every page
  of a book out in a single box and CLIPS anything bigger, so a book with mixed
  page sizes loses the right edge of its odd-sized pages. `EpubConverter`'s page
  box fixes one size from the first page and letterboxes every page onto it.
- **The reader cuts a strip off the page and we cannot measure it.** Its usable box
  is smaller than the screen the `scr` probe reports, it never scales a page to
  fit, and nothing in the EPUB reaches a fixed-layout path — a commercially
  produced fixed-layout comic renders just as clipped. Hence `Scale`: a per-device
  percentage the user dials down until nothing is cut. Do not try to derive it;
  it is not derivable from the browser.
- **One preferences cookie, so every write is read-modify-write.** The favorite
  library is a field in `DeviceSettings`, not a second cookie; constructing a
  fresh instance on save silently drops the other fields.
- **Cookie `Secure` comes from config, not just `Request.IsHttps`.** Behind a
  TLS-terminating proxy `IsHttps` is spoofable, so it's
  `ForceSecureCookies || Request.IsHttps`. `TokenStore` and `DeviceSettings` must
  agree.
- **The device id is a trust boundary.** It arrives in a cookie and becomes a
  filename, so it goes through `SanitizeId`; blank or invalid means "no marks",
  never a fallback name that would pool devices into one bucket.
- **Download marks live in a `marks/` subdirectory of the EPUB cache.** That is
  safe because every cache glob is extension-scoped (`*.epub`, `*.tmp`) and a
  device id can't contain a dot — don't widen one of those patterns.

## Adding a new X

- **Endpoint (stream/action):** a `MapXxxEndpoints` extension in `Endpoints/`,
  injecting `AbsApiClient` — no token handling, the handler does it — mapped from
  `Program.cs`.
- **ABS call:** a method on `AbsApiClient`, no `accessToken` parameter. If the
  response introduces yet another metadata shape, add a DTO rather than widening
  an existing one.
- **Page:** a Razor Page under `Pages/`, building library URLs through
  `LibraryLinks`. Let `AbsAuthException` propagate — the middleware in
  `Program.cs` redirects to `/login`.
- **Setting:** one key in the `inkshelf_settings` value; absent keys must fall
  back to that setting's own default.

## Configuration

Every key is read once into `AbsOptions` at startup. **`README.md` is the
reference** for names, defaults and meanings — it's the operator-facing doc, and
duplicating it here has already produced a wrong default once.

## Security

The app assumes a reverse proxy in front of it, and defends the boundaries a
client can influence:

- Forwarded headers are only honored from `TRUSTED_PROXY` (unset trusts the
  immediate hop).
- The session token is in a Data-Protection-encrypted, HttpOnly, SameSite=Lax
  cookie whose `Secure` flag is pinnable via config.
- State-changing requests are antiforgery-protected.
- User-supplied ids are URL-escaped; EPUB metadata is XML-escaped.
- Client-influenced sizes — archive bytes, total cache bytes, `scr` dimensions —
  are all bounded, because each one is an OOM or disk-exhaustion vector.
- The unauthenticated `/diag` probe is bounded, sanitized, and gateable.

Two deliberate exceptions. **`/settings` sits outside the
`AbsAuthException`→`/login` gate**: it only reads and writes the per-device
cookie and never calls ABS, so there is no session to lose. And **`/login`
relies on ABS's own brute-force protection** rather than a local rate limit.

Accepted risk: Data Protection keys are stored unencrypted on their (private)
volume.
