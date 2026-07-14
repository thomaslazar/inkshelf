# Inkshelf Architecture

A thin, server-rendered web client for the Audiobookshelf (ABS) API, built to run
on old e-reader browsers (Tolino). This document describes the steady-state
structure and the conventions that hold it together. **Read it before adding
features** — several conventions look like cleanup targets but are load-bearing.

## Big picture

- ASP.NET Core, .NET 10, **no AOT**. Razor Pages render HTML; minimal-API
  endpoints serve streams and actions. Stateless: the ABS JWT lives in an
  encrypted cookie (Data Protection).
- Near-zero client JavaScript so old e-reader browsers work. Only two tiny inline
  scripts exist (in `_Layout.cshtml`) and both are Tolino-tested.

## Layout — where things live

```
src/Inkshelf/
  Program.cs            Bootstrap only: config → DI → middleware → endpoint maps.
  AbsOptions.cs         Typed view of config (ABS_URL, CachePath, DataProtectionKeysPath).
  Abs/                  ABS API access.
    AbsAuthClient.cs      Login + refresh. Handler-FREE typed client.
    AbsApiClient.cs       The 7 data methods. Typed client WITH AbsAuthHandler.
    AbsAuthHandler.cs     DelegatingHandler: injects Bearer, refresh-on-401-retry.
    AbsModels.cs          Response DTOs (three separate metadata shapes — see below).
    AbsFilter.cs          Encodes ABS facet filters (authors.<b64>, series.<b64>).
    AbsExceptions.cs      AbsAuthException / AbsUnauthorizedException / AbsLoginFailedException.
  Auth/                 TokenStore (encrypted cookie), Tokens, Favorites (fav-library cookie).
  Convert/              CBZ/CBR → fixed-layout EPUB.
    ConvertService.cs     Orchestrates /convert (detail → validate → cache → convert → name).
    EpubConverter.cs      Thin orchestrator: reader → processor → writer.
    ComicArchiveReader.cs Yields image entries in ordinal order (IAsyncEnumerable).
    PageImageProcessor.cs Decode + downscale-to-cap + WebP→JPEG transcode.
    EpubWriter.cs         Writes the EPUB zip (string-built XML).
    EpubCache.cs          File cache keyed by item+size+mtime+screen size.
    ScreenTarget.cs       Parses the "scr" device-size cookie into a page cap + dpr.
  Endpoints/            Minimal-API groups, one static MapXxxEndpoints() each:
                        Cover, Download, Convert, Session (logout+favorite), Diag.
  Pages/                Razor Pages: Index, Login, Library (+ models); Shared/ partials.
    Support/            Non-page helper types: LibraryLinks, ItemRowModel, Pager, SortLinks.
```

Tests live in `tests/Inkshelf.Tests/`, one file per unit. `dotnet test` from the
repo root (inside the devcontainer) must stay green.

## Load-bearing conventions (do not "clean these up")

- **Two ABS clients, not one.** `AbsAuthClient` (login/refresh) has **no** auth
  handler; `AbsApiClient` (data) runs through `AbsAuthHandler`. This split is what
  makes refresh-on-401 impossible to recurse — refresh goes through the
  handler-free client. Never attach `AbsAuthHandler` to `AbsAuthClient`, and never
  put login/refresh on `AbsApiClient`.
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
- **String-built EPUB XML** in `EpubWriter`. Verbose but dependency-free and
  epubcheck-clean. Do not swap in an XML library.
- **Razor Pages for HTML, minimal APIs for streams/actions.** Keep the split.
- **`LibraryLinks` is the single URL authority.** Every library listing/row/sort
  URL is built there, used by both the page model (`LibraryModel.Links`) and the
  row partial (`ItemRowModel.Links`). Don't re-implement URL building in a view.
- **Near-zero JS.** The two inline scripts in `_Layout.cshtml` (screen-size cookie,
  convert-warm XHR) are deliberate and Tolino-tested. Anything touching them needs
  a real-device test before merge; defensive CSS only (no `object-fit`, no flex
  `gap`).

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

## Not yet done (tracked separately)

Security hardening (forwarded-header trust, `/diag` body cap, `scr` clamp, cache
eviction, archive size ceiling, concurrent-convert lock) is deferred to its own
spec — see `docs/superpowers/specs/`. `ConvertService` is the intended home for the
per-item convert lock.
