# Converted (this device) view — design

## Problem

Nothing in the UI shows what has already been converted and cached. The only
record of a conversion is the cache filename
(`{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}[-g].epub`). A user browsing for "the
next volume in a series" can't see at a glance which books they already have as
an EPUB on this device.

## Goal

A single combined page — `/converted` — listing every comic already converted
**and cached for the current device**, across all libraries, so the user can
spot "already have that", jump to the series to grab the next volume, or
re-download. Reachable from a link on the Index (home) page.

## Scope decisions (settled in brainstorming)

- **Single combined view**, not per-library. The cache stores no library id, and
  `POST /api/items/batch/get` is not library-scoped (it queries purely by id), so
  one call returns items spanning every library — and carries `libraryId` per
  item anyway, so rows can still link into the right library.
- **Reuse the existing listing row** (`_ItemRow`) and all its controls
  (Download / EPUB ✓ / ↻ regen / read-toggle). No new row UI. The converted page
  is "the listing rows, but only for items with something cached for this
  device."
- **Reverse-parse the cache filename** to recover item ids; no sidecar index
  (keeps the "no dual source of truth / done = File.Exists" principle — an index
  would drift from the actual files).
- **Entry point:** a dedicated line on the Index body, above the library list
  (`↓ Converted on this device →`), not in the header.

## Data flow

1. **Enumerate the cache.** A new `EpubCache` method parses every `*.epub`
   filename **right-to-left** — strip `.epub`, optional `-g` (grayscale), then
   `{maxW}x{maxH}`, then `-{mtimeMs}`, then `-{size}`; the remainder is the item
   id. Right-to-left parsing is robust even when the item id contains hyphens
   (ABS ids can be UUIDs). Yields `(itemId, size, mtimeMs, maxW, maxH, grayscale,
   path)` per file. Filename-format knowledge stays inside `EpubCache`, next to
   `PathFor` (single source of truth for the scheme).
2. **Filter to this device.** Compute the current `RenderTarget` from the `scr`
   cookie + `DeviceSettings` (retina/grayscale) via `ScreenTarget.FromCookie`,
   exactly as the listing does. Keep only variants whose
   `(maxW, maxH, grayscale)` equal the current target.
3. **Dedupe by item id**, keeping the newest `mtimeMs` (an ebook updated in ABS
   and reconverted without `?fresh` can leave both an old and a new file for the
   same item).
4. **One batch call.** `POST /api/items/batch/get` with all matched ids →
   `title`, `authors[]`, `series[]`, `coverPath`, `ebookFile` (size/mtime/format),
   and `libraryId`, cross-library in one round trip (verified against ABS
   v2.35.1 `LibraryItemController.batchGet` → `toOldJSONExpanded`).

## Components

### `EpubCache` — cache enumeration
Add a method (e.g. `ListVariants()`) returning a small record per `.epub` file:
`CachedVariant(string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool
Grayscale, string Path)`. Parses right-to-left as above; skips any filename that
doesn't match the scheme. This mirrors `PathFor` — the two must stay in sync
(a test round-trips `PathFor` → parse).

### ABS batch fetch — expose id + libraryId + title + coverPath
The batch response already contains these; our DTOs and fetch drop them.

- `AbsBatchMetadata` gains `title` (`[JsonPropertyName("title")] string? Title`).
- `AbsBatchMedia` gains `coverPath` (`string? CoverPath`).
- `AbsBatchItem` gains `libraryId` (`string? LibraryId`).

All additive — safe under the "three separate metadata shapes" convention (that
rule forbids declaring `series` as an array on `AbsMetadata`; it does not forbid
adding scalar fields to the batch shape).

Refactor the ABS client so the raw items are reachable:
- Add `GetItemsBatchAsync(IReadOnlyList<string> ids, ct)` returning
  `List<AbsBatchItem>` (the expanded items).
- Reimplement the existing `GetItemsMetadataBatchAsync` on top of it (build the
  `id → AbsBatchMedia` dict from the list) so the listing is unchanged.

### Convert-state helper — extract for reuse
`LibraryModel.RowState(AbsItem, AbsBatchMedia?, RenderTarget)` and the format/
cache/queue logic it uses are currently private to `LibraryModel`. Extract the
per-item state computation into a shared **static** helper both pages call —
`ConvertRowStateResolver.Resolve(AbsItem item, AbsBatchMedia? media,
RenderTarget target, EpubCache cache, ConvertQueue queue)` returning
`ConvertRowState` — so `ConvertedModel` and `LibraryModel` share one
implementation (DRY). `LibraryModel.RowState` becomes a thin call into it;
behaviour is unchanged for the listing.

### `/converted` — new Razor Page + `ConvertedModel`
Injects `AbsApiClient`, `EpubCache`, `ConvertQueue` (same as `LibraryModel`).
`OnGetAsync`:
1. Compute the current `RenderTarget`.
2. `EpubCache.ListVariants()` → filter to the target → dedupe by item id (newest
   mtime).
3. If none: render the empty state.
4. `GetItemsBatchAsync(ids)` → map each `AbsBatchItem` to an `AbsItem`
   (`AbsMedia` with `Metadata.Title`, `CoverPath`, `EbookFile`) and build an
   `ItemRowModel`: authors/series from the batch metadata, per-row
   `LibraryLinks(libraryId, …)` (no active facet), convert-state from the shared
   helper, `ReturnUrl = "/converted"`, read-flag from `GetFinishedItemIdsAsync`.
5. Render each via the existing `_ItemRow` partial.

Sort rows **series → sequence → title** (directly serves the "next volume" use
case). No sort bar, no pager — the cache is size-bounded (`MaxCacheBytes`, ~dozens
of items), so v1 shows all.

An expired session lets `AbsAuthException` propagate → `/login` (existing
middleware). A batch `HttpRequestException` (including the all-or-nothing 403,
see below) degrades to a notice ("Couldn't load details from ABS") rather than a
500.

### Index page — entry point
Add a line to `Index.cshtml` between the head and the library `<ul>`:
`↓ Converted on this device →` linking to `/converted`. No JS, plain `<a>`.

## UI

Rows are the existing `_ItemRow` — cover thumbnail, title, author link(s),
series link(s), Download, the convert action (EPUB ✓ for current cached items),
↻ regen, and read-toggle — visually identical to the library listing. The page
head mirrors the others (title + a crumb back to Libraries + settings gear).

## Edge cases

- **Deleted-from-ABS items** aren't returned by `batch/get` → silently dropped.
- **Stale cache entry** (ebook changed since conversion): the row is built from
  the *current* ebook file, so the shared state helper computes `Convert` (the
  stale variant doesn't match the current cache key). The row still shows —
  slightly odd for a "converted" list, but correct; the plain convert link will
  produce a fresh EPUB. Acceptable for v1.
- **Batch 403 (permission all-or-nothing):** if the user can't access any one
  item in the batch, ABS 403s the whole request. Won't bite a single-user
  sidecar (the user converted these items, so they have access); handled as a
  graceful notice, not a crash.
- **Regen (↻) on `/converted`** hits `/convert/{id}?fresh=1`, which deletes the
  cached file before re-enqueuing. Because this page is driven by cache-file
  enumeration (unlike the ABS-driven listing), the row drops out of the list
  until the reconversion completes, then reappears as "EPUB ✓" on a later
  reload. Self-healing; accepted for v1.
- **Empty-cache path returns the page before any ABS call**, so an
  unauthenticated visitor with an empty cache sees the page chrome +
  "Nothing converted…" rather than the `/login` redirect the other pages
  give. No data leaks (the cache is empty by definition on that branch; a
  non-empty cache still triggers the batch call → 401 → `/login`). Accepted
  for v1.

## Non-goals (v1)

- No per-library split, no sidecar index, no pagination, no sort controls.
- No new row partial or new download endpoint — reuse `_ItemRow` and
  `/convert/{id}` / `/download/{id}`.
- No change to the cache key or the conversion path.

## Testing

- `EpubCache`: `PathFor(...)` → `ListVariants()` round-trips all fields,
  including grayscale and a hyphenated (UUID-style) item id; a non-matching
  filename is skipped.
- ABS client: `GetItemsBatchAsync` posts `{libraryItemIds}` to
  `/api/items/batch/get` and deserializes id, libraryId, title, coverPath,
  authors/series, ebookFile; `GetItemsMetadataBatchAsync` still returns the same
  dict as before (regression).
- Convert-state helper: the extracted helper returns the same states as before
  for cbz/cbr/other, cached/converting/failed/convert (move/retarget the
  existing coverage).
- `ConvertedModel`: given a stubbed cache + stubbed batch, produces rows only for
  device-matching variants, deduped by item id (newest mtime), sorted
  series→seq→title; empty cache → empty state; batch failure → notice, no throw.
- Render test: `/converted` emits `_ItemRow` markup (cover, title, series link
  into the item's library, EPUB action) for a cached item.
- Full suite (`dotnet test`) stays green.
