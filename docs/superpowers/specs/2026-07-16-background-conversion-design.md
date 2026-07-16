# Inkshelf Background Conversion — Design

**Date:** 2026-07-16
**Status:** Approved for planning
**Source:** Roadmap priority #1 (*Convert UX / feedback → Background conversion*),
refined in a brainstorming session with the owner. Root cause confirmed from real
logs on a low-power self-hosted box.

## Goal & scope

Decouple CBZ/CBR → EPUB conversion from the HTTP request so a client disconnect
can never kill it, and give the listing a way to reflect progress.

**Root cause (confirmed).** Conversion runs *inside* the `/convert` request,
threaded with the request's `CancellationToken` (`RequestAborted`). When the
client disconnects before it finishes — the warm XHR timing out, or the user
navigating away — the token cancels and tears the conversion down mid-flight. The
`.tmp` is discarded, nothing is cached, and the next attempt re-downloads the
whole archive. On a slow box a large comic takes minutes, well past the client's
patience, so it can **never** complete.

**The cure.** Run the conversion **detached from the request** on the app
lifetime, tracked by a small in-memory registry, with a cheap status signal the
client polls (JS) or watches via `<meta refresh>` (no-JS).

**In scope:**
- Background execution model (queue + worker + registry).
- New `/convert` endpoint semantics (kick / status / download / regen).
- Client feedback: JS in-place poll + no-JS `<noscript>` listing meta-refresh.
- Complementary roadmap items that fall out naturally: **Listing freshness**
  (`Cache-Control: no-store`) and **Regen (↻) feedback** (aligned to the same
  machinery).

**Out of scope** (separate roadmap items, only *touched* where noted):
- Conversion memory footprint / speed (the memory & speed items only shrink the
  cancellation window — this design removes the window entirely).
- Settings system / retina / resolution / grayscale / EPUB2 fallback.
- Suppressing Convert upfront for known-oversized archives (a nice future touch
  using `EbookFile.Metadata.Size`, noted but not built).

Ground rules (from `CLAUDE.md` / `ARCHITECTURE.md`):
- ASP.NET Core Razor Pages + minimal APIs, .NET 10, **no AOT**, **no new NuGet**
  (the queue is built-in `System.Threading.Channels` + a `BackgroundService`).
- Near-zero client JS: this evolves the existing warm-XHR inline script — a
  real-device test is required before merge; defensive CSS only.
- New config extends the typed `AbsOptions`. `dotnet test` green after each step.
- Conventional Commits; ask before committing; no `Co-Authored-By`.

## Architecture overview

```
tap Convert (JS on)         tap Convert (JS off)
      │                            │
      ▼                            ▼
GET ?warm=1  ──enqueue──►   GET /convert/{id}?return=…  (file missing)
  (202, returns now)          ──enqueue──► 302 back to the listing
      │                            │
      ▼ poll ?status=1 /5s         ▼ listing has a pending row →
   done → "EPUB ↓"              <noscript><meta refresh=30></noscript>
   (no reload)                     │ (30s full reloads, no-JS only)
                                   ▼ done → row flips to "EPUB ✓"
```

Two new units, one existing unit reused:

- **`ConvertQueue`** (new, singleton) — the in-memory job registry **and** the
  channel producer. Owns `ConcurrentDictionary<string cachePath, JobState>` and
  an unbounded `Channel<ConvertJob>`. Public surface: `Enqueue(job)` (idempotent —
  see dedup), `Status(cachePath)`, and internal `Reader` for the worker.
- **`ConvertWorker`** (new, `BackgroundService`) — drains the channel with a
  global concurrency cap, runs the conversion on the **app lifetime**
  (`ApplicationStopping`), and writes terminal state back to the registry.
- **`ConvertService`** (existing) — its convert-on-miss block moves into the
  worker path; it keeps `ConvertLock` (same-target dedup / double-checked
  `File.Exists`) and the archive-ceiling guard unchanged.

"Done" is **never stored in the registry** — it is the atomic existence of the
`.epub` on disk (`EpubWriter` writes `outPath + ".tmp"` then `File.Move`s it into
place, so a crash never yields a partial `.epub`). The registry holds only the
transient states the feedback UX needs.

## Job registry & states

`JobState` ∈ `{ Queued, Running, Failed }` (a small enum; `Failed` carries a
UTC `since` for TTL sweeping, reason logged not stored).

`ConvertQueue.Status(cachePath)` resolves the client-facing status in this order:

1. `File.Exists(cachePath)` → **`done`** (disk is the source of truth).
2. registry entry `Running` → **`running`**; `Queued` → **`queued`**.
3. registry entry `Failed` → **`failed`**.
4. otherwise → **`none`** (never started → render a plain `Convert`).

**Restart is stateless.** The registry is in-memory; on restart it is empty. A
job caught mid-flight is simply gone — the listing shows `Convert` again and a
re-tap re-runs it from scratch (no resume). Orphan `.tmp` files are swept on
startup (delete `*.tmp` under the cache dir), which also closes the open
security-roadmap item "assert no partial file".

**Failed TTL.** `Failed` entries older than ~10 min are swept (on the next
`Status`/`Enqueue` touch, or by the worker), so an un-retried failure reverts to a
plain `Convert` rather than lingering forever. A re-tap clears `Failed`
immediately and re-enqueues.

## Concurrency & dedup

- **Global cap.** The worker processes at most `MaxConcurrentConversions` jobs at
  once (default **1** — a small box must not run two ImageSharp resizes
  concurrently). Implemented as N concurrent consumer loops (or a `SemaphoreSlim`
  gate) in `ConvertWorker`.
- **Channel is unbounded.** Dedup prevents same-target pile-up and realistically
  few distinct jobs queue; a bounded channel + "busy" rejection is YAGNI.
- **Enqueue dedup (important).** `Enqueue` is idempotent: if the cache path is
  already `Queued`/`Running`, it is a no-op that returns the current status. This
  stops repeated taps or a poll-driven re-kick from stacking duplicate jobs.
  `ConvertLock` remains the second line of defense inside the worker (double-checked
  `File.Exists`).
- **Cancellation token.** The worker runs each conversion on a token linked to
  `IHostApplicationLifetime.ApplicationStopping` — **never** the request token. A
  client disconnect cannot cancel it; only app shutdown can (leaving a `.tmp` for
  the next startup sweep).

## Endpoint surface (`/convert/{id}`)

One route, behavior selected by query params (all GET — idempotent, so a plain
`<a>` works with JS off; no antiforgery needed):

| Request | Meaning | Response |
|---|---|---|
| `GET /convert/{id}` | download, or no-JS kick | file exists → stream the EPUB (serves the `EPUB ✓` link **and** the JS "second tap"). File missing → `Enqueue` + **302 back to the listing** (the `return` param). |
| `GET /convert/{id}?warm=1` | JS kick | `Enqueue`, return **202** immediately (never streams the file). |
| `GET /convert/{id}?status=1` | JS poll | plain text: `queued` / `running` / `done` / `failed` (trivial ES5 check, no JSON). |
| `GET /convert/{id}?fresh=1` | regen (↻) | `RemoveForItem` + `Enqueue`; JS intercepts like `warm`, no-JS **302s back to the listing**. |

**Return-to-listing.** The no-JS `Convert`/regen anchors carry a `return` param —
the current listing URL (facet/sort/page), built by `LibraryLinks` (the single URL
authority) so the user lands back exactly where they were. The endpoint validates
it is a **local** path (must start with `/`, no scheme/host — open-redirect guard)
and falls back to `/` otherwise. JS paths never navigate, so they don't need it.

The endpoint still parses the `scr` cookie into `(maxW, maxH, dpr)` and computes
the cache path via `EpubCache.PathFor` to key the job and answer status.

## Client feedback

**JS on (evolve the existing warm inline script in `_Layout.cshtml`):**
- Tap → `preventDefault`, set link text `Converting…`, `GET ?warm=1`.
- Then poll `GET ?status=1` every **~5s** (a named constant): `done` → text
  `EPUB ↓` + `data-ready=1` (user taps to download — two-tap, as today; no
  auto-download); `failed` → text `Convert (retry)` (a re-tap re-kicks); keep
  polling on `queued`/`running`.
- `data-ready=1` links let the native click through to download.

**JS off (listing meta-refresh):**
- The plain `Convert` anchor navigates to `GET /convert/{id}?return=…`; file
  missing → `Enqueue` + **302 back to the listing**.
- The listing renders `<noscript><meta http-equiv="refresh" content="30"></noscript>`
  in `<head>` **whenever any visible row has a pending job** (`queued`/`running`).
  So a no-JS browser reloads the whole listing every **30s** (a named constant),
  and on each render the server flips the finished row to `EPUB ✓`; once no
  visible row is pending, the `<noscript>` block is omitted and reloading stops.
- The `<noscript>` wrapper is the key reconciliation with the JS path: JS-on
  browsers **ignore** it (they poll in place, no reload), JS-off browsers honor
  it. Server doesn't need to detect JS. `<noscript>` in `<head>` containing
  `<meta>` is ancient, universally-supported HTML — safe on old e-ink engines.
- 30s (not 5s) because a full-listing reload re-hits the ABS batch call,
  re-renders every row/cover, and flashes the e-ink panel; the interval trades
  latency for gentleness on the box and the display.

## Listing freshness (complementary)

- `<noscript>` meta-refresh (above) is the no-JS progress surface.
- `Cache-Control: no-store` on the library listing response so a manual reload
  (and each meta-refresh reload) always re-renders current registry/file state
  instead of a stale bfcache/proxy copy.
- `Library.cshtml.cs` already computes cached-ness per row; it additionally
  consults `ConvertQueue.Status` to render the `Converting…` state and to decide
  whether to emit the `<noscript>` refresh block.

## Testing

- **`ConvertQueue`**: `Enqueue` is idempotent for an in-flight path (no duplicate
  channel item; returns current status); `Status` precedence (file-exists beats
  registry; `none` when absent); `Failed` past TTL sweeps to `none`.
- **`ConvertWorker`**: a job runs to completion on a token unrelated to any
  request (disconnect simulated by a cancelled *request* token does **not** cancel
  it); concurrency cap of 1 serializes two distinct jobs; a throwing conversion
  lands `Failed` (not `Running`) and is logged.
- **Startup sweep**: an orphan `*.tmp` in the cache dir is deleted on start; a
  real `.epub` is left intact.
- **Endpoint**: `?status=1` returns the right text per state; `?warm=1` returns
  202 without streaming; `/convert/{id}` streams when the file exists and **302s
  to the listing** when it does not; the `return` param is honored only when local
  (a non-local/absolute `return` falls back to `/` — open-redirect guard).
- **Listing render**: the `<noscript>` meta-refresh block is emitted when a
  visible row is `queued`/`running` and **omitted** when none is pending; the row
  shows `Converting…` / `Convert` / `EPUB ✓` per `ConvertQueue.Status`.
- **`ConvertService`**: existing convert/ceiling/lock tests stay green; the
  convert-on-miss logic is exercised through the worker path.

## Sequencing

Steps on `feat/background-conversion`, one PR at the end. Each ends `dotnet test`
green.

1. **`AbsOptions`**: add `MaxConcurrentConversions` (default 1). Named constants
   for the two intervals (JS poll 5s, no-JS refresh 30s) and the `Failed` TTL.
2. **`ConvertQueue`** (registry + channel producer, idempotent `Enqueue`,
   `Status`, TTL sweep) + unit tests.
3. **`ConvertWorker`** `BackgroundService`: drain with the cap on
   `ApplicationStopping`, write terminal state, startup `.tmp` sweep + tests.
   Move `ConvertService`'s convert-on-miss into the worker path (keep
   `ConvertLock` + ceiling).
4. **Endpoint rewrite**: `warm` → 202 kick, new `?status=1`, `/convert/{id}`
   file-or-302-to-listing (with local-only `return` guard), `?fresh=1` alignment
   + tests.
5. **Client**: evolve the warm inline script (kick → poll); `LibraryLinks` adds
   the `return` param to the convert/regen anchors; listing emits the `<noscript>`
   meta-refresh when a row is pending, `Cache-Control: no-store`, and the
   `Converting…` render. **Real-device test before merge.**
6. **Docs**: update `docs/ARCHITECTURE.md` (background-conversion convention, new
   config, endpoint semantics); tick the roadmap items.

## Non-goals

- No new dependency (built-in Channels + `BackgroundService` only).
- No durable/resumable jobs — restart-safe by being stateless (re-tap re-runs).
- No change to the conversion *output* (same fixed-layout EPUB, same cache key,
  same `ConvertLock`/ceiling guarantees) — only *when and where* it runs and how
  progress is surfaced.
- No memory/speed optimization here (separate roadmap items).
