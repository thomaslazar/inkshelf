# Inkshelf Conversion Memory Footprint — Design

**Date:** 2026-07-17
**Status:** Approved for planning
**Source:** Roadmap *Runtime footprint* item, refined in a brainstorming session
with the owner after on-box measurement on the Zimaboard (4 cores / 7.6 GiB,
~1.5 GiB headroom, behind Cosmos).

## Goal & scope

Bring the sidecar's memory back down after conversions. On-box measurement:

| State | Memory (cgroup) |
|---|---|
| Fresh start | ~90 MiB |
| Idle, no conversions | ~247 MiB |
| During conversion (plateau) | 700 → 915 MiB |
| During conversion (transient peak) | ~1190 MiB |
| **Resting after a 5-convert batch (idle)** | **~897 MiB, never released until restart** |

Fresh footprint is fine; the problem is **(a) a ~1.2 GiB transient peak per
conversion** and **(b) ~897 MiB that ratchets up and is never returned** (anon
heap ~810 MiB). On a box with ~1.5 GiB headroom the peak is a real OOM risk, and
the retention wastes most of RAM at idle.

**Goal:** resting idle returns near fresh after any number of conversions, and
the transient peak drops to low-hundreds MiB so a tight container limit is safe.

**In scope** (one PR):
1. Stream pages into the EPUB (kills the held-all-pages peak).
2. Spool the downloaded archive to a temp file (kills the `MemoryStream` peak).
3. Release ImageSharp's unmanaged pool after each conversion.
4. GC config for a single-user sidecar (Workstation GC + conserve), baked into
   the image.
5. Deployment: document a container memory ceiling; keep conversions sequential.

**Out of scope / non-goals:**
- No change to EPUB **output** — byte-identical entries and OPF/NCX (existing
  `EpubConverterTests` are the guard). This is a pure memory refactor.
- No parallelised page processing (a separate roadmap *speed* item) — it would
  *raise* the peak (N images decoded at once). On this RAM-constrained box we
  keep page processing serial: **memory over speed**, consistent with
  `MaxConcurrentConversions=1`.
- No new dependency; no AOT. String-built EPUB XML and the file-backed zip stay.
- Final tuning (exact `GCConserveMemory` level, container-limit value) is settled
  by re-measuring on the Zimaboard after this lands.

Ground rules (`CLAUDE.md` / `ARCHITECTURE.md`): .NET 10, no AOT; `dotnet test`
green after each step; Conventional Commits; ask before committing; near-zero
client JS untouched (this is server-only — no device re-test needed).

## Background: where the memory goes

Per-conversion allocation, confirmed against the code:

- **`ConvertWorker`** copies the ABS download into a `MemoryStream` under the
  `MaxArchiveBytes` ceiling (~220 MiB typical, up to 500 MiB) before converting.
- **`EpubConverter.ConvertAsync`** decodes + downscales + re-encodes every page
  and accumulates them all in a `List<EpubWriter.Page>` — every page's encoded
  bytes held at once (~300–560 MiB for ~280 pages).
- **`EpubWriter.Write`** already streams the zip to a **temp file** (`FileStream`),
  not RAM — but it consumes the full `List<Page>` because `Opf()`/`Nav()`/`Ncx()`
  need per-page metadata at the end.
- **ImageSharp** decodes/resizes using its default allocator, which pools
  **unmanaged** memory and retains it across operations — invisible to GC config.
- **Server GC** (Web SDK default) keeps per-core committed heaps and is reluctant
  to return them — pointless for sequential CPU-bound converts.

## 1. Stream pages into the EPUB (primary peak fix)

Invert the write from "collect all pages → write zip" to a single streaming pass.

- `EpubConverter.ConvertAsync` keeps its current public signature
  (`Stream archive, EbookMeta, outPath, maxW, maxH, dpr, ct`); internally it now
  builds a **lazy `IAsyncEnumerable<EpubWriter.Page>`** (a private async iterator:
  read entry → decode → downscale → encode → `yield`) instead of a `List`, and
  passes it to the streaming writer. `ConvertWorker`'s call site is unchanged
  (the archive it passes is now a `FileStream`, still a `Stream`).
- `EpubWriter.WriteAsync(string outPath, EbookMeta meta,
  IAsyncEnumerable<Page> pages, double dpr, CancellationToken ct)` consumes it:
  opens the temp-file zip, writes `mimetype` + `container.xml`, then per page
  **writes the image entry + the page xhtml and records only a lightweight
  `PageMeta(Name, Width, Height)`** (the page's `byte[]` is released after its
  entry is written), and finally writes `content.opf` / `nav.xhtml` / `toc.ncx`
  from the recorded `PageMeta` list, closes, and `File.Move`s tmp → outPath.
- Only **one page's bytes** are live at a time; the retained `List<PageMeta>` is
  ~280 tiny records.
- **Output is byte-identical** — same entries in the same order, same OPF/NCX
  strings (the `i+1` indexing, mime mapping, and dpr→viewport math are unchanged;
  they just read from `PageMeta` instead of `Page`). The static string-builder
  helpers (`PageXhtml`, `Opf`, `Nav`, `Ncx`) are reused, retargeted to `PageMeta`.

The old `EpubWriter.Write(...)` synchronous list-based method is removed.
`EpubWriterTests` (which call `Write` directly with a `List<Page>`) are migrated
to `WriteAsync` with an `IAsyncEnumerable<Page>` — same assertions on the produced
zip. `EpubConverterTests` call `ConvertAsync` (unchanged) and need no edits; they
are the primary output-equality guard.

## 2. Spool the archive to a temp file

In `ConvertWorker`, replace the `MemoryStream` buffer with a temp file on the
cache volume:

- Copy the download into a temp file (e.g. `<cache>/<guid>.dl.tmp`) enforcing the
  same `MaxArchiveBytes` byte-ceiling during the copy (over-limit → mark `Failed`,
  as today), then open it as a `FileStream` for `EpubConverter`.
- `try/finally` deletes the temp file after conversion (success or failure).
- Extend `EpubCache.SweepTemp()` (startup) to also delete orphaned `*.dl.tmp`
  archive temps, not just `*.epub.tmp`.
- `ComicArchiveReader` already takes a `Stream`; a seekable `FileStream` satisfies
  SharpCompress for both CBZ (zip) and CBR (rar).

## 3. Release ImageSharp's pool after each conversion

After a conversion completes in `ConvertWorker` (in the `finally`, or right after
`WriteAsync` returns), call
`SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources()`
so the pooled unmanaged pixel buffers return to the OS. (Pooling still helps
*within* a conversion across its pages; we only release *between* conversions.)

## 4. GC configuration (baked into the image)

- `src/Inkshelf/Inkshelf.csproj`: `<ServerGarbageCollection>false</ServerGarbageCollection>`
  (Workstation GC — one heap, returns memory more readily) and
  `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`.
- Add `GCConserveMemory` via a runtime host option
  (`<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5" />`),
  starting at 5 and tuned by the Zimaboard re-measure.
- Baked into the csproj → `runtimeconfig.json` so it ships in the published image
  and is versioned — not a Cosmos-only env var. (Operators can still override via
  `DOTNET_*` env for experiments.)

These are the cheap reclaim wins; §1–§3 are what make them stick by not
allocating the mountain in the first place.

**Validated on-box (2026-07-17, applied as Cosmos env, to be baked in here):**
`gcServer=0` + `GCConserveMemory=5` took **resting 897 → ~554 MiB (−38%)** and
made idle memory *decay and return* to the OS instead of pinning; the transient
peak barely moved (1190 → ~936 MiB, −21%) — that's genuine allocation for §1/§2
to cut. A **residual ~500 MiB anon persists at idle** even with GC on; the on-box
test did not exercise ImageSharp's **unmanaged** pool, which GC config cannot
reclaim, so §3 is expected to account for a real slice of that residual.

## 5. Deployment ceiling (docs + example)

- `docker-compose.example.yml`: add a memory limit sized above the **new** peak
  (measure first; expected ~512 MiB) and a short README note explaining it caps
  the balloon and makes .NET self-tune down. The real limit is set in Cosmos
  (operator ops); the example documents the recommendation.
- Keep `MaxConcurrentConversions=1`. Do **not** raise it on this box — two
  parallel converts would multiply the peak past the host's headroom.

## Success criteria

Measured against the **post-§4 baseline** (GC already on: resting ~554 MiB, peak
~936 MiB) — the code work (§1–§3) must close the remaining gap:

- Resting idle after an N-conversion batch stays within ~2× fresh
  (target ≲ ~150–200 MiB), down from ~554 MiB — i.e. §3 + streaming reclaim the
  residual anon that GC alone left behind.
- Transient peak per conversion in low-hundreds MiB (target < ~400 MiB), down
  from ~936 MiB, enabling a ~512 MiB container limit with headroom (vs the
  1.5 GiB the report says to keep until the peak drops).
- **EPUB output byte-identical**; full `dotnet test` green.
- Verified by re-measuring on the Zimaboard with the existing method (cgroup
  `memory.current` + `memory.stat` anon/file split, before/after a fixed batch).

## Testing

- **Output equality (the guard for §1):** the existing `EpubConverterTests`
  (entry list, ordering, no-webp, OPF fixed-layout, viewport math) must pass
  unchanged against the streamed writer. Add a focused test that a multi-page
  archive still yields the correct entry set and `page-0001.xhtml` viewport, i.e.
  the stream path is output-equivalent to the old list path.
- **Streaming holds one page:** a test asserting the producer is lazy — e.g. an
  `IAsyncEnumerable<Page>` whose materialised count never exceeds 1 concurrently
  (wrap the producer to track concurrent live pages), so a regression that
  re-buffers is caught without an on-box memory probe.
- **Archive temp-file spool (§2):** conversion succeeds from a temp-file-backed
  archive; the `*.dl.tmp` is deleted on success and on failure; `SweepTemp`
  removes an orphaned `*.dl.tmp` at startup and leaves `.epub` intact.
- **GC config (§4):** a check that the published/effective `runtimeconfig.json`
  has `System.GC.Server=false` (guards against a regression re-enabling Server
  GC). ImageSharp release is a single call verified by inspection + the on-box
  measure (the unmanaged pool isn't unit-observable).

## Sequencing (one PR on `perf/conversion-memory-footprint`)

Each step ends `dotnet test` green.

1. **GC config** in the csproj (+ conserve option). Trivial; verify
   `runtimeconfig.json`.
2. **ImageSharp release** in `ConvertWorker` after each convert.
3. **Archive temp-file spool** in `ConvertWorker` + `SweepTemp` extension + tests.
4. **Streaming EPUB write** — `EpubWriter.WriteAsync(IAsyncEnumerable<Page>)` +
   `PageMeta`, `EpubConverter` lazy producer; remove the old list-based `Write`;
   output-equality + lazy-materialisation tests. The big one.
5. **Deployment**: `docker-compose.example.yml` memory limit + README note.
6. **Docs**: `ARCHITECTURE.md` (conversion streams pages / low-peak; Workstation
   GC for a sequential single-user sidecar; archive spooled to temp) in
   present-tense steady-state voice; `ROADMAP.md` — move the *Runtime footprint*
   and *Conversion memory footprint* items to Done.

Then re-measure on the Zimaboard and, if resting/peak are acceptable, tighten the
container limit; otherwise iterate (that data-gathering is why final tuning is
deliberately deferred, not specced to a fixed number here).
