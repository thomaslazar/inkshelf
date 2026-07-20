# Inkshelf Architecture

A thin, server-rendered web client for the Audiobookshelf (ABS) API, built to run
on old e-reader browsers. This document describes the steady-state
structure and the conventions that hold it together. **Read it before adding
features** — several conventions look like cleanup targets but are load-bearing.

## Big picture

- ASP.NET Core, .NET 10, **no AOT**. Razor Pages render HTML; minimal-API
  endpoints serve streams and actions. Stateless: the ABS JWT lives in an
  encrypted cookie (Data Protection).
- Near-zero client JavaScript so old e-reader browsers work. Only two tiny inline
  scripts exist (in `_Layout.cshtml`) and both are tested on a real e-ink reader.

## Layout — where things live

```
src/Inkshelf/
  Program.cs            Bootstrap only: config → DI → middleware → endpoint maps.
  AbsOptions.cs         Typed view of config (ABS_URL, CachePath, DataProtectionKeysPath).
  Abs/                  ABS API access.
    AbsAuthClient.cs      Login + refresh. Handler-FREE typed client.
    AbsApiClient.cs       The 9 data methods. Typed client WITH AbsAuthHandler.
    AbsAuthHandler.cs     DelegatingHandler: injects Bearer, refresh-on-401-retry.
    AbsDownloadClient.cs  Handler-free authenticated ebook download for ConvertWorker.
    AbsModels.cs          Response DTOs (three separate metadata shapes — see below).
    AbsFilter.cs          Encodes ABS facet filters (authors.<b64>, series.<b64>).
    AbsExceptions.cs      AbsAuthException / AbsUnauthorizedException / AbsLoginFailedException.
  Auth/                 TokenStore (encrypted cookie), Tokens, Favorites (fav-library
                        cookie), DeviceSettings (per-device settings cookie).
  Convert/              CBZ/CBR → fixed-layout EPUB.
    ConvertService.cs     Orchestrates the /convert kick (detail → validate → cache → enqueue).
    ConvertQueue.cs       In-memory job registry + Channel producer (singleton).
    ConvertWorker.cs      BackgroundService: drains the queue on the app lifetime, downloads + converts.
    EpubConverter.cs      Thin orchestrator: reader → processor → writer.
    ComicArchiveReader.cs Yields image entries in ordinal order (IAsyncEnumerable).
    PageImageProcessor.cs Decode + downscale-to-cap + WebP→JPEG transcode + optional grayscale.
    EpubWriter.cs         Writes the EPUB zip (string-built XML).
    EpubCache.cs          File cache keyed by item+size+mtime+screen size+grayscale.
    ScreenTarget.cs       Parses the "scr" probe + settings flags into a RenderTarget.
    RenderTarget.cs       Resolved per-device render knobs (cap, dpr, grayscale).
  Endpoints/            Minimal-API groups, one static MapXxxEndpoints() each:
                        Cover, Download, Convert, Read, Session (logout+favorite), Settings, Diag.
  Pages/                Razor Pages: Index, Login, Library, Converted, Settings (+ models); Shared/ partials.
    Support/            Non-page helper types: LibraryLinks, ItemRowModel, Pager, SortLinks, ConvertRowStateResolver.
```

Tests live in `tests/Inkshelf.Tests/`, one file per unit. `dotnet test` from the
repo root (inside the devcontainer) must stay green.

## Load-bearing conventions (do not "clean these up")

- **Three ABS clients, not one.** `AbsAuthClient` (login/refresh) has **no** auth
  handler; `AbsApiClient` (data) runs through `AbsAuthHandler`. This split is what
  makes refresh-on-401 impossible to recurse — refresh goes through the
  handler-free client. Never attach `AbsAuthHandler` to `AbsAuthClient`, and never
  put login/refresh on `AbsApiClient`. `AbsDownloadClient` is the third: also
  handler-free, but for a different reason — it's the background worker's
  authenticated ebook download, and the worker has no `HttpContext` for
  `AbsAuthHandler` to resolve a token from. It carries a caller-supplied bearer
  (captured at kick time) and does **not** refresh on 401 — a failed download just
  fails the job, and the user re-taps with a fresh token. Never attach
  `AbsAuthHandler` to it; never use it from a request path (use `AbsApiClient`
  there).
- **`AbsAuthHandler` resolves scoped services per request.** It injects
  `IHttpContextAccessor` and resolves `TokenStore` + `AbsAuthClient` from
  `HttpContext.RequestServices` inside `SendAsync` — it must not constructor-inject
  them (the handler is pooled by `IHttpClientFactory` longer than a request scope).
- **The retry copies request headers.** `AbsAuthHandler` rebuilds the request for
  its retry and copies the incoming headers first — this preserves the
  `User-Agent`, which the ABS reverse proxy requires (it 403s an empty UA). Losing
  it is a production outage. The request body is buffered to `byte[]` so it
  re-sends identically; streaming request uploads are unsupported by the retry.
- **Three separate ABS metadata shapes** (`AbsMetadata`, `AbsBatchMetadata`,
  `AbsDetailMetadata`). ABS reuses the `series` JSON key with different types per
  endpoint (object vs array); unifying them reintroduces a deserialization bug.
- **Read state is ABS media progress, not local.** The listing/search reads the
  finished-set from `GET /api/me` once per render (keyed by `libraryItemId`),
  and the per-row toggle POSTs to `/read/{id}` → `PATCH /api/me/progress/{id}`
  `{isFinished}`. A failed read-state fetch degrades to "all unread" rather than
  failing the page.
- **String-built EPUB XML** in `EpubWriter`. Verbose but dependency-free and
  epubcheck-clean. Do not swap in an XML library.
- **The EPUB declares a cover.** `EpubWriter` emits both the EPUB3
  `properties="cover-image"` manifest flag and the EPUB2 `<meta name="cover">`.
  The worker prefers the ABS cover art (`AbsDownloadClient.DownloadCoverAsync`,
  600px) and the converter falls back to flagging the first page when ABS has no
  usable cover. Metadata only — the cover is never a spine entry, so reading opens
  on page 1. It is not part of the cache key (it derives from the item).
- **Razor Pages for HTML, minimal APIs for streams/actions.** Keep the split.
- **`LibraryLinks` is the single URL authority.** Every library listing/row/sort
  URL is built there, used by both the page model (`LibraryModel.Links`) and the
  row partial (`ItemRowModel.Links`). Don't re-implement URL building in a view.
- **Convert-row state is computed in one place.** `ConvertRowStateResolver.Resolve`
  turns (item, batch media, device `RenderTarget`, cache, queue) into a
  `ConvertRowState`. Both the library listing and the `/converted` view call it, so
  a "converted" badge always agrees across pages. The `/converted` view is the EPUB
  cache read back: `EpubCache.ListVariants` reverse-parses filenames into item ids,
  filtered to the current device's target, then one cross-library
  `GET /api/items/batch/get` supplies metadata.
- **Near-zero JS.** The two inline scripts in `_Layout.cshtml` (screen-size cookie,
  convert-warm XHR) are deliberate and tested on a real e-ink reader. Anything
  touching them needs a real-device test before merge; defensive CSS only (no
  `object-fit`, no flex
  `gap`).
- **Cookie `Secure` derives from config, not just `Request.IsHttps`.** Behind a
  TLS-terminating proxy `IsHttps` is spoofable, so the flag is
  `ForceSecureCookies || Request.IsHttps`. `TokenStore` and `Favorites` must apply
  the same rule — keep them in sync.
- **Conversion runs in the background.** The `/convert/{id}` request only *kicks*
  a conversion: fetch item detail, validate the format, compute the per-device
  cache path, capture the caller's ABS access token, and enqueue a job on
  `ConvertQueue`. `ConvertWorker` (a `BackgroundService`) does the actual
  download-and-convert on the app lifetime (`ApplicationStopping`), not the
  request's cancellation token, so a client disconnect (page nav, browser close)
  can't cancel an in-flight conversion. "Done" is the atomic existence of the
  on-disk `.epub` (temp-file-then-rename); `ConvertQueue`'s registry only ever
  holds the transient `Queued`/`Running`/`Failed` states, kept in memory rather
  than persisted, so a restart just drops any pending/running row back to "no
  job" and the next tap re-enqueues it — there is nothing to reconcile. An
  interrupted conversion can leave an orphan `.tmp` behind; `ConvertWorker` sweeps
  those at startup before it starts draining the queue. The endpoint distinguishes
  callers by query param: plain navigation downloads the file (or 302s back to the
  listing if not ready yet); `?warm=1` is the JS kick, answered with a 202 and a
  status body while queued/running; `?status=1` polls without enqueuing, returning
  the status as plain text; `?fresh=1` discards the cached EPUB and reconverts.
- **Two device cookies, two purposes.** `scr` is JS-written device *truth* (the
  screen probe); `inkshelf_settings` (`DeviceSettings`) is server-written user
  *choice* (retina, grayscale). Wherever conversion is computed —
  `ConvertEndpoints` and the Library row-state — read **both** and combine them
  via `ScreenTarget.FromCookie(scr, retina, grayscale)` into a `RenderTarget`, so
  a real conversion and the "✓ converted" badge agree. Grayscale is part of the
  cache key (`-g` marker); retina already changes `maxW/maxH`.
- **Conversion is serialized and resource-bounded.** `ConvertService` and
  `ConvertWorker` share `ConvertLock` (a singleton keyed by cache-output path)
  with a double-checked `File.Exists`, so concurrent jobs for the same target
  don't double-convert or corrupt the `.tmp`. Client-influenced inputs are
  bounded (archive size, total cache size, `scr` dimensions) via `AbsOptions` — see
  Configuration.
- **Conversion is streamed, not buffered.** The downloaded archive is spooled to
  a temp file rather than a `MemoryStream`. `EpubConverter` yields pages lazily,
  and `EpubWriter.WriteAsync` consumes that `IAsyncEnumerable<Page>`, writing
  each page's image and xhtml straight into the file-backed zip and keeping only
  lightweight per-page metadata — one page's bytes are held at a time, never the
  whole book. `ConvertWorker` releases ImageSharp's retained (unmanaged) memory
  pool after each conversion so peak usage doesn't compound across jobs. This is
  what keeps the sidecar's memory footprint roughly independent of archive size.
- **Workstation GC, not Server GC.** `Inkshelf.csproj` pins
  `ServerGarbageCollection=false` (plus `ConcurrentGarbageCollection=false` and
  `System.GC.ConserveMemory=5`), overriding the ASP.NET Core default. A
  single-user sidecar doing sequential, CPU-bound conversions doesn't benefit
  from Server GC's per-core heaps sized for throughput; Workstation GC keeps one
  compact heap and hands memory back to the OS, which is what a mostly-idle
  sidecar needs. Don't "fix" this back to Server GC.

## Adding a new X

- **New endpoint (stream/action):** add a `MapXxxEndpoints` extension in
  `Endpoints/`, inject `AbsApiClient` (data) — no token handling needed, the
  handler does it — and call `app.MapXxxEndpoints()` from `Program.cs`.
- **New ABS call:** add a method to `AbsApiClient` (no `accessToken` parameter; the
  handler injects the Bearer). If a response introduces yet another metadata shape,
  add a new DTO rather than widening an existing one.
- **New page:** add a Razor Page under `Pages/`, inject `AbsApiClient`; build any
  library URLs through `LibraryLinks`, not inline strings. A page that hits an
  expired session lets `AbsAuthException` propagate — the middleware in `Program.cs`
  redirects to `/login`.

## Configuration (`AbsOptions`)

All config is read once into `AbsOptions` at startup (`Program.cs`). Keys:

| Key | Default | Purpose |
|---|---|---|
| `ABS_URL` | — (required) | Audiobookshelf base URL |
| `CachePath` | `<content>/.cache/epub` | converted-EPUB cache dir |
| `DataProtectionKeysPath` | `<content>/.keys` | Data Protection key ring |
| `FORCE_SECURE_COOKIES` | `false` | force cookie `Secure` behind a TLS proxy |
| `TRUSTED_PROXY` | (unset) | comma-separated IPs/CIDRs allowed to set forwarded headers; unset = trust all |
| `DIAG_ENABLED` | `true` | map the `/diag` probe endpoint |
| `MaxArchiveBytes` | `524288000` (500 MB) | refuse larger ebook archives (OOM guard) |
| `MaxCacheBytes` | `1073741824` (1 GB) | LRU-evict the EPUB cache past this |
| `MaxConcurrentConversions` | `1` | cap on concurrent background `ConvertWorker` conversion loops |

## Security

The app trusts a reverse proxy in front of it and defends the boundaries that a
client can influence: the session token lives in a Data-Protection-encrypted,
HttpOnly, SameSite=Lax cookie whose `Secure` flag can be pinned via config;
state-changing requests are antiforgery-protected; user-supplied ids are
URL-escaped and EPUB metadata is XML-escaped; the unauthenticated `/diag` probe is
bounded, sanitized, and gateable; and conversion is bounded against
resource-exhaustion (archive size, cache size, screen-dimension inputs) — all tuned
through Configuration.

`/settings` (the GET page and `POST /settings`) sits outside the `AbsAuthException`→
`/login` gate by design: it only reads/writes the per-device `inkshelf_settings`
cookie and never calls `AbsApiClient`, so there's no ABS session to lose and
nothing that throws `AbsAuthException` in the first place.

By design, `/login` relies on Audiobookshelf's own brute-force protection rather
than a local rate limit, and Data-Protection keys are stored unencrypted on their
(private) volume.
