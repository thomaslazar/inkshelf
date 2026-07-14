# Inkshelf Structural Refactor — Design

**Date:** 2026-07-14
**Status:** Approved for planning
**Source:** `temp/refactor-handover.md` (review of `main` @ 3bf358a) turned into a
spec via a brainstorming session with the owner.

## Goal & scope

Restructure Inkshelf for maintainability, landing on **idiomatic ASP.NET Core /
Razor Pages patterns**. This effort is **structural only** (handover §3, findings
3.1–3.5). The §5 security findings are explicitly **out of scope** and become a
separate spec later.

Ground contract (from `CLAUDE.md` and the handover §7):

- **Behavior-preserving.** `dotnet test` (from repo root, inside the devcontainer)
  must be green after *every* PR. No user-visible behavior changes.
- Respect the handover's **non-goals** (§4): keep the three ABS metadata shapes
  separate, keep the Razor-Pages + minimal-API mix, keep string-built EPUB XML,
  keep near-zero JS. None of the work below touches the two inline scripts, so no
  Tolino device re-test is required for this effort.
- Conventional Commits; ask before committing; no `Co-Authored-By` lines.
- Verify ABS API behavior against `temp/audiobookshelf/`, never the stale docs.

The owner is not deeply familiar with Razor Pages, so each section names the
idiomatic pattern it adopts and why.

## Sequencing

Six sequenced steps on one branch (`refactor/structural`), shipping as a
**single PR at the end**. Each step is planned just-in-time (plan → implement →
plan next) and ends with `dotnet test` green — the green-tests contract is
per-commit, not deferred to PR time.

1. **3.1** — Extract endpoints + `ConvertService`.
2. **3.2** — Auth `DelegatingHandler`; delete `AbsSession`.
3. **3.3** — `LibraryLinks` shared link-builder.
4. **3.4** — Consistency pass (options, antiforgery, namespaces, layout).
5. **3.5** — Split `EpubConverter`.
6. Final docs/consistency sweep.

**3.1 leads** (not 3.2): it is the lowest-risk, highest-visibility win and it
establishes the `Endpoints/` + `ConvertService` structure the later steps slot
into. 3.2 rewrites call sites regardless of order, so leading with 3.1 costs
nothing.

---

## 3.1 — Program.cs split + ConvertService

**Problem.** `Program.cs` (194 lines) mixes bootstrap/DI with five inline
minimal-API endpoints. `/convert` (lines 99–156) is ~60 lines of business logic
(item-detail fetch, format validation, cache lookup, conversion orchestration,
warm-mode handling, filename sanitisation) inside a route lambda closing over
`app.Logger`.

**Design.**

- New `Endpoints/` folder, one static class per endpoint group, each exposing a
  `MapXxxEndpoints(this IEndpointRouteBuilder)` extension method — the idiomatic
  minimal-API grouping pattern:
  - `CoverEndpoints` → `/cover/{id}`
  - `DownloadEndpoints` → `/download/{id}`
  - `ConvertEndpoints` → `/convert/{id}`
  - `SessionEndpoints` → `/logout`, `/favorite`
  - `DiagEndpoints` → `/diag`
- New `ConvertService` (registered in DI) owns the `/convert` orchestration:
  detail fetch → format validation → cache probe → conversion → warm-mode →
  filename. The endpoint lambda becomes a thin call into it. Logging moves to an
  injected `ILogger<ConvertService>`.
- `Program.cs` shrinks to bootstrap: DI registration + `app.MapXxxEndpoints()`
  calls + middleware.

**Non-goal reminder.** The concurrent-convert race (§5 #5) is *not* fixed here —
no `SemaphoreSlim`. `ConvertService` is a structural home for that later fix, but
this PR is behavior-preserving.

**Tests.** Existing endpoint smoke tests stay green. `ConvertService` gets direct
unit coverage for the orchestration branches (cached / not-cached / warm /
wrong-format) previously only exercised through the endpoint.

---

## 3.2 — Auth DelegatingHandler; delete AbsSession

**Problem.** Every ABS call threads the token through a lambda:
`_session.ExecuteAsync((tok, c) => _client.GetItemsAsync(tok, …), ct)`. This
noise repeats across `Library.cshtml.cs`, `Index.cshtml.cs`, and the endpoints.
`AbsClient`'s nine data methods each carry an `accessToken` parameter.

**Design — the "two typed clients" approach.** A `DelegatingHandler`
(`AbsAuthHandler`) transparently does what `AbsSession.ExecuteAsync` does today:

```
read token (TokenStore) → attach Bearer → send
   └─ 401? → refresh (AbsAuthClient) → save new token (TokenStore) → retry once
             └─ refresh throws / retry still 401 → throw AbsAuthException
   └─ token missing → throw AbsAuthException
```

`AbsClient` splits into two typed clients so login/refresh never pass through the
auth handler (the handover's hard caveat — otherwise refresh recurses through
itself):

- **`AbsAuthClient`** — `LoginAsync`, `RefreshAsync`. Plain typed client, **no
  handler**.
- **`AbsApiClient`** — the nine data methods, **with** `AbsAuthHandler` in its
  pipeline. Methods **lose the `accessToken` parameter**.

`AbsAuthHandler` depends on `TokenStore` + `AbsAuthClient`. Because the refresh
call runs on the handler-free `AbsAuthClient`, recursion is **structurally
impossible** — no request-path sniffing needed. `TokenStore` already uses
`IHttpContextAccessor`, so reading the token and writing the refreshed token back
to the cookie both work from inside the handler on the same request scope.

**Caveats and how this design meets them:**

- **Double-401 → redirect.** The handler throws `AbsAuthException` after a failed
  refresh or a second 401. `TokenStore.Read()` returning null also throws it
  (matches today's `?? throw new AbsAuthException()`). The existing auth-redirect
  middleware catches it unchanged.
- **Re-sendable request bodies.** The only POST with a body is the metadata batch
  (`JsonContent`), which re-serialises fine on the retry. **Constraint to
  document in code:** the handler cannot retry a streaming request body; none
  exist today, and any future streaming *upload* must bypass the retry.
- **Streaming responses.** `/cover` and `/ebook` use `ResponseHeadersRead` and
  hand the stream to the caller. The handler returns the response without
  disposing it; a 401 arrives in the headers before the body, so the retry path
  is unaffected.

**Call-site effect.** `_session.ExecuteAsync((tok,c) => _client.GetItemsAsync(tok,…), ct)`
becomes `_api.GetItemsAsync(…, ct)`. `AbsSession.cs` is deleted.

**Tests.** `AbsSessionTests` port to `AbsAuthHandler` tests using a stub inner
handler: (a) 401→refresh→retry→200 saves the new token; (b) double-401 throws
`AbsAuthException`; (c) missing token throws `AbsAuthException`; (d) a body-POST
re-sends its content on retry. `AbsClient` tests split across the two clients.

---

## 3.3 — LibraryLinks shared link-builder

**Problem.** URL-building lives in two places that can drift: six helpers on
`LibraryModel` (`Library.cshtml.cs:127-151`) and three *re-implemented* Razor
local functions in `_ItemRow.cshtml:11-14`, fed the library id through
stringly-typed `ViewData["LibraryId"]`.

**Design.** One `LibraryLinks` type constructed from the request's facet context
(`LibraryId` + active `Filter` / `Author` / `Series` / `Sort` / `Desc`). Two
method families:

- *Row links* (need only the id): `FilterHref(group, id)`, `AuthorHref(name)`,
  `SeriesHref(display)`. The `SeriesHref` `" #seq"`-stripping quirk lives here,
  once.
- *Page links* (carry the active facet): `ListingHref(sort, desc, page)`,
  `SortHref(field)`.

- `LibraryModel` exposes it as a `Links` property.
- **`ItemRowModel` gains a `LibraryLinks Links` field**; the partial calls
  `Model.Links.FilterHref(…)` etc.
- `ViewData["LibraryId"]` (set at `Library.cshtml:3`) is **deleted**.

Rejected alternative: give the row only `LibraryId` + static helpers — splits
facet state and row state across two idioms. One instance is simpler to reason
about.

**Tests.** New `LibraryLinks` unit tests for each helper (facet carry-over, page
reset on sort change, series-name stripping, escaping). Existing `SortLinks`
tests stay green (`LibraryLinks.SortHref` delegates to `SortLinks.Next`).

---

## 3.4 — Consistency pass

- **Typed options.** `AbsOptions { AbsUrl, CachePath, DataProtectionKeysPath }`,
  bound from configuration with `[Required]` / `[Url]` data annotations and
  `.ValidateOnStart()`. Replaces the raw `Configuration["ABS_URL"]` /
  `["CachePath"]` / `["DataProtectionKeysPath"]` reads; misconfiguration now fails
  fast at boot instead of on first request.
- **Antiforgery — pick one pattern.** Make both state-changing endpoints
  identical: **manual token validation + `DisableAntiforgery()`** on both
  `/favorite` and `/logout`. Explicit and independent of middleware ordering,
  which suits minimal-API POSTs. (Today `/favorite` does this; `/logout`
  validates manually without `DisableAntiforgery()`.)
- **Namespaces.** Move `Favorites` → `Inkshelf.Auth` and `ScreenTarget` →
  `Inkshelf.Convert` (its cache-key consumer lives there). `Program.cs` gets a
  `using Inkshelf.Convert;` instead of fully-qualifying `Inkshelf.Convert.*`.
- **Layout (cosmetic).** Move `Pager`, `SortLinks`, `ItemRowModel` under
  `Pages/Support/`. Pure tidy; no behavior change.

**Tests.** All existing tests stay green. Add an options-validation test
(missing `AbsUrl` fails startup).

---

## 3.5 — EpubConverter split

**Problem.** `EpubConverter.ConvertAsync` does three jobs in one 153-line file:
read the archive, decode/resize each image, write the EPUB (the OPF/NCX/nav/xhtml
string builders).

**Design.** Split into three single-responsibility, independently testable units:

- **`ComicArchiveReader`** — open a CBZ/CBR stream, yield image entries in order
  (name + bytes).
- **`PageImageProcessor`** — decode + resize to `maxWidth` / `maxHeight` / `dpr`,
  return `(bytes, width, height)`.
- **`EpubWriter`** — given processed pages + `EbookMeta`, produce the EPUB zip.
  **Keeps the string-built XML** (non-goal §4 #3 — do not swap in an XML library).

`EpubConverter` becomes a thin orchestrator: reader → processor → writer.

**Framing.** This split is for **testability and single-responsibility**, not to
add input formats (the handover brackets it as YAGNI-unless-new-formats; we
accept the split for cleanliness). Output stays **byte-identical and
epubcheck-clean** — behavior-preserving.

**Tests.** Existing `EpubConverter` end-to-end tests stay green (they now cover
the orchestrator). Add focused unit tests per unit: archive reader (entry
ordering, non-image skip), image processor (resize math, DPR), EPUB writer
(OPF/NCX structure, metadata XML-escaping).

---

## Architecture documentation (deliverable)

A standing `docs/ARCHITECTURE.md` that describes the **post-refactor** structure
and the conventions future agents must follow when building in this repo. It is
**not** a rehash of this spec (which is a one-time change record) — it documents
the steady state:

- The layering and where things live: `Endpoints/` (minimal-API groups),
  `ConvertService` and the `Convert/` units, `Abs/` two-client + auth-handler
  model, `Pages/` + `Pages/Support/`, `Auth/`, options.
- The load-bearing conventions and *why* they exist, so they are not "cleaned up"
  by mistake: the two separate ABS clients (auth vs api) and why login/refresh
  bypass the handler; the three deliberately-separate ABS metadata shapes;
  string-built EPUB XML; the Razor-Pages-for-HTML / minimal-API-for-streams split;
  near-zero-JS and the two inline scripts; `LibraryLinks` as the single URL
  authority.
- A short "adding a new X" guide (new endpoint, new ABS call, new page).

It is **linked from `CLAUDE.md`** so every future agent session picks it up. It is
authored in **Step 6**, once the structure has settled, and each earlier PR that
changes structure updates its relevant part (or PR 6 reconciles).

## Step 6 — Final sweep

Author `docs/ARCHITECTURE.md` (above) reflecting the settled structure and add a
link to it from `CLAUDE.md`. Docs touch-up (README/design-doc pointers to the new
structure) and a last consistency pass over anything the earlier PRs left
inconsistent. No new behavior.

## Out of scope (tracked elsewhere)

- All §5 security findings (proxy-trust config, `/diag` cap, `scr` clamp, cache
  eviction, archive size ceiling, concurrent-convert `SemaphoreSlim`,
  login rate-limit). Separate spec.
- The §4 non-goals — not to be "fixed".
