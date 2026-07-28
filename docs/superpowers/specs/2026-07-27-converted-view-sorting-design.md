# Sort the Converted view, newest conversion first

**Status:** design approved, ready for implementation plan
**Date:** 2026-07-27
**Roadmap item:** Browsing & reading — "Sort the Converted view, newest first"

## Goal

`/converted` lists the comics already converted and cached for this device. It is
the page you open to fetch a conversion you just kicked off — but the one you
want is not reliably near the top, so you hunt for it.

Default the page to **newest conversion first**, and expose the other useful
orders as sort links.

Note the roadmap item's premise was wrong: it says the view "lists cached EPUBs
in whatever order the cache enumeration yields". It does not — `Converted.cshtml.cs`
already sorts series → sequence → title with unseried items last. So this changes
the default and adds sort links; it does not introduce sorting.

## Scope

**In:** a `converted` sort key defaulting to descending, plus `series`, `title`
and `author` keys; the sortbar markup; the conversion timestamp on
`CachedVariant`; tests.

**Out:** filtering (by series or otherwise) and paging. The list is per-device
and short, so newest-first plus sort links should cover it, and a filter UI costs
more than it saves. Revisit only if a real cache grows big enough to make
scrolling painful. Also out: persisting the chosen sort per device — a query
parameter is enough, as on the library listing.

## Design

### A. Where "converted at" comes from

**The cached `.epub`'s own filesystem write time**, not the `MtimeMs` already
parsed out of the filename.

This distinction is the main risk in the change. `EpubCache.PathFor` builds
`{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}.epub`, where `size` and `mtimeMs`
describe the **source ebook file in ABS** and exist to invalidate the cache when
that source changes. `CachedVariant.MtimeMs` therefore already sits right there
looking like the obvious field to sort on — and sorting on it would order by when
the comic was last touched in ABS, which is silently, plausibly wrong.

So `CachedVariant` gains a `ConvertedAtUtc` field:

- `ListVariants` switches from `Directory.EnumerateFiles` to
  `new DirectoryInfo(_dir).EnumerateFiles("*.epub")`, so the `FileInfo` comes out
  of the directory walk rather than costing a second stat per file.
- `ConvertedAtUtc` is `FileInfo.LastWriteTimeUtc`.
- Regen (`?fresh=1`) rewrites the file, so a re-converted comic correctly returns
  to the top. That is the desired behaviour, not an artefact.

Where one item has more than one cached variant matching this device's target —
possible when the source file changed and an older variant has not been evicted —
take the **newest** `ConvertedAtUtc`. The existing code already dedupes to a set
of item ids and recomputes row state from the current source file, so only the
timestamp needs this reduction.

### A2. Remove touch-on-serve; evict FIFO by conversion time

`EpubCache.Touch` (called from `ConvertService.KickAsync` on every cache hit)
re-stamped a served file's write time so `EnforceCap` behaved as approximate LRU.
It is removed, along with its call site and its test.

**Why.** LRU is right when a cache exists to serve repeated reads. This one
exists to bridge exactly one gap — an expensive conversion to a single download —
and once that download lands, the EPUB is on the e-reader and has served its
purpose. Re-downloading is rare: a book you want to re-read stays on the device.

Worse, touch-on-serve is actively inverted for the dominant workflow. Convert
volumes 1–10 in a batch, then download one per day as you read. Each download
marks the volume you have **already consumed** as hot, so when the cap trips
eviction picks the coldest entries — the volumes not yet fetched, which are the
ones still needed. FIFO evicts the oldest conversions, which are the ones most
likely already pulled down.

The counter-argument, recorded for honesty: a user whose workflow *does* involve
re-downloading (two devices sharing one render target, or deleting finished books
and re-reading weeks later) will occasionally pay a 60–90 s reconversion. It is
non-destructive, and with `MaxCacheBytes` defaulting to 5 GiB most deployments
never evict at all. Judged not worth keeping an inverted policy for.

**Consequence, and why it matters here:** with nothing rewriting a cached EPUB
after conversion, its `LastWriteTimeUtc` genuinely *is* its conversion time. So
`ConvertedAtUtc` is an accurate name and the "Converted" sort label is truthful —
without this, the field would have meant "converted, then bumped on each fetch".
Regen via `?fresh=1` rewrites the file, so a re-converted entry correctly becomes
the newest.

`EnforceCap`'s ordering code is unchanged — it already orders by
`LastWriteTimeUtc`. Only its comment and the `MaxCacheBytes` documentation change,
from "least recently used" to "oldest conversion".

### B. Query binding

Mirrors `Library.cshtml.cs`:

```csharp
[FromQuery(Name = "sort")] public string? Sort { get; set; }
[FromQuery(Name = "desc")] public string? DescParam { get; set; }
public bool Desc => DescParam == "1";
```

`desc` **must** bind as `string?`. Razor's bool binder rejects `"1"`, and `"1"`
is the form already used across the app. Typed as `bool` the descending
directions become unreachable — the same bug the library listing hit.

### C. Toggle semantics — two-state, deliberately unlike the library listing

The library listing cycles off → asc → desc → off, because "off" there means
*let ABS apply its own default ordering*. `/converted` sorts a local list, so
there is no server-side order to fall back to and an "off" state would be
meaningless.

So each link is a two-state toggle: click a field → ascending; click the field
that is already active → descending.

`converted` is the exception: it starts **descending**, because newest-first is
the entire point of the change. Clicking it while it is already descending
switches to ascending (oldest first).

`SortLinks.Arrow` is reused for the ↑/↓ indicator — it is field-name agnostic.
`SortLinks.Next` is **not** reused; its tri-state cycle does not apply. A small
local helper on the page model computes the next `desc` value.

### D. Sorting

| Key | Order |
|---|---|
| `converted` (default) | `ConvertedAtUtc`, then title |
| `series` | today's comparator, verbatim: unseried last, then series name, then sequence, then title |
| `title` | title |
| `author` | first author's name, then title |

`Desc` reverses the result. An unrecognised `sort` value falls back to the
default rather than throwing — it is a client-supplied string.

**Descending reverses the whole list, including the grouping — accepted
deliberately.** Under `?sort=series&desc=1` the "unseried last" grouping inverts
too, so items with no series appear *first*. Keeping them last in both
directions would mean per-key ordering instead of one `Reverse()`, and the
owner judged the extra branching not worth it: descending simply means "reverse
what you see". The same mechanism flips the title tiebreak within ties, which is
unreachable for `converted` (tick precision) and symmetric elsewhere. A test
pins this behaviour so a later "fix" cannot change it silently.

The `series` comparator is kept as-is rather than rewritten: it already handles
numeric sequence parsing and unseried items, and it is the current behaviour.

### E. Markup

An inline `<nav class="sortbar">` in `Converted.cshtml`, mirroring the library
listing's and reusing the existing `.sortbar` CSS so the two pages look
identical. Four links: Converted · Series · Title · Author.

No shared partial. The two pages' hrefs differ in kind — the library listing's
`SortHref` emits ABS field paths (`media.metadata.authorNameLF`), while these are
local keys — so factoring out a common sortbar now would abstract over a
difference that matters.

### F. Strings

Verified: all five keys already exist in `src/Inkshelf/locales/de.json` —
`"Sort:"` → `Sortierung:`, `"Title"` → `Titel`, `"Author"` → `Autor`,
`"Series"` → `Serien`, `"Converted"` → `Konvertiert`. German is the only locale
file. **No new translations needed**, and no new catalog keys to add.

### G. Tests

Extending `ConvertedRenderTests` (already renders `/converted` end to end against
a `WebApplicationFactory` with a stubbed ABS and an on-disk seeded cache):

- Default order is newest conversion first.
- Each of the four keys orders correctly.
- `desc=1` reverses.
- An unrecognised `sort` value falls back to the default and does not throw.
- **The trap test:** a fixture where the filename's `MtimeMs` would order the
  rows differently from the files' write times, asserting the write-time order
  wins. Without this, "simplifying" to the already-parsed `MtimeMs` passes every
  other test.

## Risks

| Risk | Mitigation |
|---|---|
| Sorting on `CachedVariant.MtimeMs` (source mtime) instead of conversion time | The trap test above; a comment on the field explaining what it is for |
| `desc` typed as `bool`, making descending unreachable | Bind as `string?`; a test covering `desc=1` |
| An extra stat per cached file | `DirectoryInfo.EnumerateFiles` yields `FileInfo` from the directory walk |
| Two variants for one item giving an arbitrary timestamp | Reduce to the newest per item id |
