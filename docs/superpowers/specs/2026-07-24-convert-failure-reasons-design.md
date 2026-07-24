# Conversion failure reasons — design

## Problem

A failed conversion shows only "Convert (retry)"; the reason lives solely in
the server log. The instance is shared with non-technical users: they can
reach the admin, but shouldn't have to guess, and the admin shouldn't have to
log-dive for every vague report. Deterministic failures (an archive over the
size ceiling) look identical to transient ones, and every retry of an
oversized item re-downloads up to `MaxArchiveBytes` just to fail again.

## Goal

When a conversion fails, the user sees *why*, in their language, with an
actionable message where one exists ("The archive is 1.3 GB, over the 1 GB
limit"), plus Retry and Back. Works on the oldest e-ink engine (plain HTML,
no client JS required). The admin gets a log line rich enough that `docker
logs` alone answers a family report. The UI test harness exercises the whole
path against a seeded oversized item.

## UX flow (settled in brainstorming)

- Tapping the row action on a Failed item **retries immediately** (today's
  behaviour, unchanged).
- **JS path:** the existing status poll, on seeing `failed`, navigates to the
  reason page — the watching user lands on the reason automatically.
- **No-JS path:** the listing keeps its redirect-back + meta-refresh flow;
  when a row renders Failed, it shows "Convert (retry)" plus a small "why?"
  link to the reason page.
- The "why?" link lives in the shared `_ConvertAction.cshtml` Failed case, so
  it appears on listing rows, search rows, the `/converted` view, **and the
  item detail page** — identically for JS and no-JS renders.
- The reason page has **Retry** (the normal `/convert/{id}?…&return=…` kick
  URL) and **Back** (the guarded return URL). If the failed entry has expired
  (10-min TTL) or been re-queued, the page redirects back instead of showing
  stale state.

## Capture

- New `ConvertFailReason` enum: `TooLarge`, `DownloadFailed`, `BadArchive`,
  `ConvertError`.
- `ConvertQueue.MarkFailed(path)` gains `(ConvertFailReason reason, string?
  detail)` stored on the existing Failed `Entry` — same 10-min TTL, in-memory
  only. Transient is fine: a re-tap reproduces a deterministic failure. A new
  accessor (e.g. `FailureFor(path)`) returns the reason while the entry is
  Failed, null otherwise.
- `detail` carries raw values only (e.g. actual bytes for TooLarge); the page
  formats and localizes. No English prose is stored in the queue.
- `ConvertWorker` categorizes by stage:
  - archive size over the ceiling (pre-check or copy guard) → `TooLarge`
  - `DownloadEbookAsync` / spool-copy exception → `DownloadFailed`
  - `ConvertAsync` exception → `BadArchive` for archive-format errors
    (SharpCompress format exceptions, zero readable pages), else
    `ConvertError`

### Pre-download size check

`ConvertJob` gains the archive size ABS already reports (it is the same
`efm.Size` that keys the cache path; `ConvertService` passes it through). The
worker fails `TooLarge` **before downloading** when the reported size exceeds
`MaxArchiveBytes`. With tap-retries-immediately this matters: it stops every
retry of an oversized item re-downloading up to 1 GB to fail deterministically.
The existing `CopyWithLimitAsync` guard stays as the backstop for lying
metadata; both paths mark the same `TooLarge` reason.

## Surface

- A small Razor page (route `/convert/{id}/why`, query: `file`, `return`) —
  not hand-built HTML in `ConvertEndpoints` — so it gets `_Layout`, the `@L`
  localizer, and the existing CSS for free. Plain HTML, zero JS.
- Content: item title (from the batch-metadata fetch the other pages already
  use), one localized sentence per reason — TooLarge is actionable and
  includes both sizes ("The archive is 1.3 GB, over the 1 GB limit") — then
  Retry and Back links.
- It resolves the same cache path as the endpoint (device `scr` cookie +
  settings → `RenderTarget` → `EpubCache.PathFor`) and reads
  `ConvertQueue.FailureFor`; any state other than Failed redirects to the
  return URL.
- `_ConvertAction.cshtml` Failed case adds the "why?" link next to
  "Convert (retry)". Rows stay lean: one short localized word.
- Layout JS: when the poll transitions to `failed`, set `location.href` to
  the why-page URL (built server-side into a `data-why` attribute — the JS
  composes nothing).

## Logging

The worker's failure log lines gain the item **title** and, for TooLarge, the
**actual archive size** (not just the id and the limit), so `docker logs` is
sufficient when a report is vague.

## Localisation

All new strings go through `@L[...]` and get `de.json` entries. Sizes are
formatted server-side (GiB/MiB, one decimal).

## UI test harness (uicheck)

The seeded-ABS Playwright pass must hit the real failure path:

- `docker/seed.sh` seeds one extra CBZ fixture, an **oversized comic** (e.g.
  "Neon Blade Vol. 0" — a CBZ padded to ~1 MiB with incompressible bytes so
  the stored size is genuinely over the test ceiling).
- `tools/uicheck/run.sh` starts Inkshelf with a very small archive ceiling
  (e.g. `export MaxArchiveBytes=102400`, 100 KiB) so the oversized fixture
  trips `TooLarge` while the existing tiny `sample.cbz` still converts. The
  shipped default (1 GiB) is unchanged.
- New authenticated check: open the oversized item, click Convert, wait for
  the poll to navigate to the why-page, screenshot, and assert the localized
  over-the-limit text (German pass: "über", sizes) plus the Retry and Back
  links. A second assertion covers the server-rendered "why?" link on the
  Failed row after navigating back.

## Unit tests

- `ConvertQueue`: reason stored with Failed and returned while Failed; gone
  after TTL expiry and after re-queue.
- `ConvertWorker` categorization: TooLarge via pre-check (no download
  attempted), TooLarge via copy guard, DownloadFailed, ConvertError.
- Why-page: renders the reason for a Failed entry; redirects (guarded return)
  for any other state.

## Out of scope

- Persisting failure reasons across restarts (TTL transience is deliberate).
- Surfacing reasons in listing rows beyond the "why?" link.
- Any retry-throttling or backoff.
