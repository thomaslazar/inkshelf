# Conversion Memory Footprint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the sidecar's post-conversion memory — resting idle back near fresh and the transient peak to low-hundreds MiB — without changing EPUB output.

**Architecture:** Four changes. (1) Bake Workstation GC + conserve into the csproj. (2) In `ConvertWorker`, spool the downloaded archive to a temp file instead of a `MemoryStream` and release ImageSharp's unmanaged pool after each job. (3) Stream pages into the EPUB one at a time (`EpubWriter.WriteAsync(IAsyncEnumerable<Page>)`) instead of holding all ~280 in a `List`. (4) Document a container memory ceiling.

**Tech Stack:** .NET 10, ASP.NET Core, xUnit, ImageSharp, SharpCompress. No new NuGet; no AOT.

**Spec:** `docs/superpowers/specs/2026-07-17-conversion-memory-footprint-design.md`

## Global Constraints

- **No AOT; no new NuGet dependency.**
- **EPUB output must stay byte-identical** — same zip entries in the same order, same OPF/NCX. `EpubConverterTests` + `EpubWriterTests` are the guard.
- **String-built EPUB XML and the file-backed zip stay** (load-bearing conventions). Do not introduce an XML library.
- **Page processing stays serial**; do NOT parallelise (it fights the memory goal). `MaxConcurrentConversions` stays default 1.
- `dotnet test` green after every task. Conventional Commits; **no** `Co-Authored-By` / "Generated with" lines. Ask before committing (per-task commit steps are the plan's intent; confirm with the owner).
- Server-only change: near-zero client JS untouched → no device re-test.
- Final GC-conserve level and container-limit value are tuned by re-measuring on the Zimaboard after this lands — not pinned here.

---

## File Structure

**Modify:**
- `src/Inkshelf/Inkshelf.csproj` — GC properties.
- `src/Inkshelf/Convert/ConvertWorker.cs` — temp-file archive spool + ImageSharp release.
- `src/Inkshelf/Convert/EpubWriter.cs` — `WriteAsync(IAsyncEnumerable<Page>)` + `PageMeta`; remove `Write`.
- `src/Inkshelf/Convert/EpubConverter.cs` — lazy `IAsyncEnumerable<Page>` producer.
- `tests/Inkshelf.Tests/EpubWriterTests.cs` — migrate to `WriteAsync`.
- `tests/Inkshelf.Tests/ConvertWorkerTests.cs` — temp-spool + sweep assertions.
- `docker-compose.example.yml`, `README.md` — memory ceiling.
- `docs/ARCHITECTURE.md`, `docs/ROADMAP.md` — docs.

**No change needed:** `EpubCache.SweepTemp()` already globs `*.tmp` (covers `.dl.tmp`); `EpubConverterTests` (call `ConvertAsync`, unchanged signature — the primary output-equality guard).

---

## Task 1: GC configuration in the csproj

**Files:**
- Modify: `src/Inkshelf/Inkshelf.csproj:3-8`

**Interfaces:**
- Produces: the published/built `Inkshelf.runtimeconfig.json` carries `System.GC.Server=false`, `System.GC.Concurrent=false`, `System.GC.ConserveMemory=5`.

- [ ] **Step 1: Add the GC properties**

In `src/Inkshelf/Inkshelf.csproj`, inside the existing `<PropertyGroup>` (after `<Version>`):

```xml
    <!-- Single-user sidecar with sequential CPU-bound conversions: Workstation GC
         (one heap, returns memory to the OS) beats Server GC's per-core heaps.
         Validated on-box: resting footprint −38%. See the memory-footprint spec. -->
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
```

Then add a `RuntimeHostConfigurationOption` (a new `<ItemGroup>` is fine, or reuse one):

```xml
  <ItemGroup>
    <!-- Trade a little CPU to keep the heap compact and hand pages back to the OS. -->
    <RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5" Trim="false" />
  </ItemGroup>
```

- [ ] **Step 2: Build and verify the runtimeconfig**

Run:
```bash
dotnet build src/Inkshelf/Inkshelf.csproj -c Release
grep -E "System.GC" src/Inkshelf/bin/Release/net10.0/Inkshelf.runtimeconfig.json
```
Expected: shows `"System.GC.Server": false`, `"System.GC.Concurrent": false`, `"System.GC.ConserveMemory": 5`.

- [ ] **Step 3: Full test suite (nothing regressed)**

Run: `dotnet test`
Expected: PASS (107).

- [ ] **Step 4: Commit**

```bash
git add src/Inkshelf/Inkshelf.csproj
git commit -m "perf: use Workstation GC + conserve memory for the sidecar"
```

---

## Task 2: `ConvertWorker` — temp-file archive spool + ImageSharp pool release

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs` (the `ProcessAsync` body)
- Test: `tests/Inkshelf.Tests/ConvertWorkerTests.cs`

**Interfaces:**
- Consumes: `AbsDownloadClient.DownloadEbookAsync`, `EpubConverter.ConvertAsync(Stream, …)`, `EpubCache` (`EnforceCap`, `PathFor`), `ConvertQueue`, `ConvertLock` (all existing).
- Produces: no signature change; the archive is spooled to `<cacheDir>/<guid>.dl.tmp`, deleted in `finally`; ImageSharp's pool is released after each job.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ConvertWorkerTests.cs` (reuse its existing `Cbz()`, `ScopeFactoryReturning`, `Worker`, `Job`, `WaitUntil`, `TempDir` helpers):

```csharp
    [Fact]
    public async Task Deletes_the_archive_temp_file_after_a_successful_conversion()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.True(File.Exists(path));                                  // epub produced
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dl.tmp"));          // archive temp cleaned up
    }

    [Fact]
    public void SweepTemp_also_removes_orphaned_dl_tmp()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "abc.dl.tmp"), "partial download");
        File.WriteAllText(Path.Combine(dir.Path, "keep.epub"), "real");
        cache.SweepTemp();
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dl.tmp"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "keep.epub")));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter ConvertWorkerTests`
Expected: `Deletes_the_archive_temp_file…` FAILS (a `.dl.tmp` doesn't exist yet / no temp file is created — the assertion on `*.dl.tmp` being empty may pass trivially, so this test only becomes meaningful after Step 3; if it passes now, that's fine — it's a guard). `SweepTemp_also_removes_orphaned_dl_tmp` PASSES already (the `*.tmp` glob covers `.dl.tmp`) — keep it as a regression guard that the glob stays broad.

(Note: the meaningful RED here is behavioral — after Step 3 the worker actually creates and cleans a `.dl.tmp`. If you want a strict RED, temporarily assert `Directory.GetFiles(dir.Path,"*.dl.tmp").Length >= 0` is not enough; rely on the code review that the spool path is exercised.)

- [ ] **Step 3: Rewrite `ProcessAsync` to spool + release**

In `src/Inkshelf/Convert/ConvertWorker.cs`, replace the body of `ProcessAsync` (the `MemoryStream` buffering block) with the temp-file version. The full method:

```csharp
    private async Task ProcessAsync(ConvertJob job, CancellationToken ct)
    {
        var dlTmp = Path.Combine(Path.GetDirectoryName(job.CachePath)!,
            Guid.NewGuid().ToString("N") + ".dl.tmp");
        try
        {
            if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }
            _queue.MarkRunning(job.CachePath);

            using (await _lock.AcquireAsync(job.CachePath, ct))
            {
                if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }

                var sw = Stopwatch.StartNew();
                using var scope = _scopes.CreateScope();
                var download = scope.ServiceProvider.GetRequiredService<AbsDownloadClient>();

                // Spool the download to a temp FILE (not a MemoryStream) so the ~220 MiB
                // archive never sits in the managed heap. Ceiling enforced during the copy.
                await using (var archive = await download.DownloadEbookAsync(job.ItemId, job.AccessToken, ct))
                await using (var spool = new FileStream(dlTmp, FileMode.Create, FileAccess.Write))
                {
                    if (!await CopyWithLimitAsync(archive, spool, _options.MaxArchiveBytes, ct))
                    {
                        _logger.LogWarning("Archive for {Id} exceeds {Limit} bytes — refusing.", job.ItemId, _options.MaxArchiveBytes);
                        _queue.MarkFailed(job.CachePath);
                        return;
                    }
                }

                await using (var read = new FileStream(dlTmp, FileMode.Open, FileAccess.Read))
                    await _converter.ConvertAsync(read, job.Meta, job.CachePath, job.MaxW, job.MaxH, job.Dpr, ct);

                _cache.EnforceCap(_options.MaxCacheBytes);
                _logger.LogInformation("Converted {Id} in {Ms} ms", job.ItemId, sw.ElapsedMilliseconds);
            }
            _queue.MarkDone(job.CachePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // app stopping — leave .tmp for the next startup sweep, don't mark Failed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversion failed for {Id}", job.ItemId);
            _queue.MarkFailed(job.CachePath);
        }
        finally
        {
            try { if (File.Exists(dlTmp)) File.Delete(dlTmp); } catch (IOException) { }
            // Return ImageSharp's retained UNMANAGED pool to the OS between jobs
            // (GC config can't reclaim it). Safe across jobs — trims free buffers,
            // not ones a concurrent convert is renting.
            SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
        }
    }
```

Keep the existing `CopyWithLimitAsync` private static helper as-is (it already copies stream→stream with the ceiling; a `FileStream` destination works unchanged).

- [ ] **Step 4: Run the worker tests**

Run: `dotnet test --filter ConvertWorkerTests`
Expected: PASS (existing worker tests + the two new ones). The archive-ceiling test still passes (ceiling enforced during the spool copy).

- [ ] **Step 5: Full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/ConvertWorker.cs tests/Inkshelf.Tests/ConvertWorkerTests.cs
git commit -m "perf: spool archive to temp file and release ImageSharp pool per job"
```

---

## Task 3: Stream pages into the EPUB (the peak fix)

**Files:**
- Modify: `src/Inkshelf/Convert/EpubWriter.cs`
- Modify: `src/Inkshelf/Convert/EpubConverter.cs`
- Modify: `tests/Inkshelf.Tests/EpubWriterTests.cs`

**Interfaces:**
- Consumes: `ComicArchiveReader.ReadAsync`, `PageImageProcessor.ProcessAsync` (unchanged).
- Produces:
  - `EpubWriter.WriteAsync(string outPath, EbookMeta meta, IAsyncEnumerable<Page> pages, double dpr, CancellationToken ct) : Task` — the old `Write(...)` is removed.
  - `EpubConverter.ConvertAsync(...)` — same signature; internally streams.

- [ ] **Step 1: Migrate `EpubWriterTests` to the streaming API (write the failing tests first)**

Replace `tests/Inkshelf.Tests/EpubWriterTests.cs` with the async form (same assertions), and add a streaming-behaviour test:

```csharp
using System.IO.Compression;
using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Inkshelf.Tests;

public class EpubWriterTests
{
    private static byte[] Jpg(int w, int h)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream(); img.Save(ms, new JpegEncoder()); return ms.ToArray();
    }

    private static async IAsyncEnumerable<EpubWriter.Page> Stream(params EpubWriter.Page[] pages)
    {
        foreach (var p in pages) { yield return p; }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAsync_produces_valid_fixed_layout_epub()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("Vol 1", "Artist", "Saga", "1"),
            Stream(new("page-0001.jpg", Jpg(80, 120), 80, 120)), dpr: 1, default);

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        Assert.Equal("mimetype", epub.Entries[0].FullName);
        Assert.Equal(epub.Entries[0].Length, epub.Entries[0].CompressedLength);
        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains(names, n => n.EndsWith("content.opf"));
        Assert.Contains(names, n => n.EndsWith("toc.ncx"));
        Assert.Contains(names, n => n.EndsWith("nav.xhtml"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("Vol 1", opf); Assert.Contains("Artist", opf);
        Assert.Contains("pre-paginated", opf); Assert.Contains("dcterms:modified", opf);
        Assert.Contains("toc=\"ncx\"", opf);
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=80, height=120", page);
        File.Delete(outPath);
    }

    [Fact]
    public async Task WriteAsync_sets_viewport_to_css_size_via_dpr()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null),
            Stream(new("page-0001.jpg", Jpg(400, 600), 400, 600)), dpr: 2, default);
        using var epub = ZipFile.OpenRead(outPath);
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=200, height=300", page);
        File.Delete(outPath);
    }

    // Guards the streaming invariant: the writer must write each page into the
    // tmp zip as it pulls it, NOT buffer all pages first. We record the tmp file
    // size at the moment each page is requested; if the writer streams, the file
    // has already grown with the previous page's (large) image by the next pull.
    [Fact]
    public async Task WriteAsync_writes_incrementally_not_buffered()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var tmp = outPath + ".tmp";
        var sizesWhenAsked = new List<long>();
        // Large images so each write exceeds FileStream's internal buffer and grows the file.
        var big = Jpg(1600, 2400);

        async IAsyncEnumerable<EpubWriter.Page> Produce()
        {
            for (var i = 1; i <= 3; i++)
            {
                sizesWhenAsked.Add(File.Exists(tmp) ? new FileInfo(tmp).Length : 0);
                yield return new EpubWriter.Page($"page-{i:D4}.jpg", big, 1600, 2400);
            }
            await Task.CompletedTask;
        }

        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null), Produce(), 1, default);

        // Buffered-then-write would show all three sizes equal (only mimetype+container
        // present at each pull). Streaming shows strict growth as prior pages land.
        Assert.True(sizesWhenAsked[1] > sizesWhenAsked[0], $"expected growth, got {string.Join(",", sizesWhenAsked)}");
        Assert.True(sizesWhenAsked[2] > sizesWhenAsked[1], $"expected growth, got {string.Join(",", sizesWhenAsked)}");
        File.Delete(outPath);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter EpubWriterTests`
Expected: FAIL — `WriteAsync` doesn't exist (compile error).

- [ ] **Step 3: Rewrite `EpubWriter` to stream**

In `src/Inkshelf/Convert/EpubWriter.cs`: keep the `Page` record, `PageXhtml`, `Nav`, `Ncx`, `Esc`, `Uid` helpers. **Remove** the `Write(...)` method. **Add** `WriteAsync`, a private `PageMeta` record, and retarget `Opf` to `PageMeta`:

```csharp
    // Lightweight per-page record kept for the manifest/spine after the page's
    // bytes have already been streamed into the zip and released.
    private sealed record PageMeta(string Name);

    public static async Task WriteAsync(string outPath, EbookMeta meta,
        IAsyncEnumerable<Page> pages, double dpr, CancellationToken ct)
    {
        var tmp = outPath + ".tmp";
        var metas = new List<PageMeta>();
        using (var fs = new FileStream(tmp, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var s = mt.Open()) s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));

            void Write(string name, string content)
            { using var s = zip.CreateEntry(name).Open(); var b = Encoding.UTF8.GetBytes(content); s.Write(b); }

            Write("META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

            var i = 0;
            await foreach (var p in pages.WithCancellation(ct))
            {
                // Write the image + its xhtml, then keep only the light metadata so
                // the page's bytes become collectable — one page live at a time.
                using (var s = zip.CreateEntry($"OEBPS/img/{p.Name}").Open()) s.Write(p.Bytes);
                var vw = Math.Max(1, (int)Math.Round(p.Width / dpr));
                var vh = Math.Max(1, (int)Math.Round(p.Height / dpr));
                i++;
                Write($"OEBPS/page-{i:D4}.xhtml", PageXhtml(p.Name, vw, vh, i));
                metas.Add(new PageMeta(p.Name));
            }
            Write("OEBPS/content.opf", Opf(meta, metas));
            Write("OEBPS/nav.xhtml", Nav(metas.Count));
            Write("OEBPS/toc.ncx", Ncx(meta, metas.Count));
        }
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
    }
```

Change `Opf`'s signature from `IReadOnlyList<Page>` to `IReadOnlyList<PageMeta>` (the body is unchanged — it only reads `pages[i].Name` and `pages.Count`):

```csharp
    private static string Opf(EbookMeta m, IReadOnlyList<PageMeta> pages)
```

(Its two `for` loops and the `Path.GetExtension(pages[i].Name)` mime logic stay exactly as they are.)

- [ ] **Step 4: Rewrite `EpubConverter` to produce pages lazily**

Replace `src/Inkshelf/Convert/EpubConverter.cs`'s `ConvertAsync` body and add the private iterator:

```csharp
using System.Runtime.CompilerServices;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

// Orchestrates CBZ/CBR → fixed-layout EPUB conversion: read pages in order,
// process each image, stream it into the EPUB (one page held at a time).
public class EpubConverter
{
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath,
        int maxWidth, int maxHeight, double dpr, CancellationToken ct)
    {
        if (dpr <= 0) dpr = 1;
        await EpubWriter.WriteAsync(outPath, meta, ProcessPagesAsync(archive, maxWidth, maxHeight, ct), dpr, ct);
    }

    // Lazily decode → downscale → transcode each page and yield it, so the writer
    // pulls one page at a time and only one page's bytes are ever live.
    private static async IAsyncEnumerable<EpubWriter.Page> ProcessPagesAsync(
        Stream archive, int maxWidth, int maxHeight, [EnumeratorCancellation] CancellationToken ct)
    {
        var idx = 0;
        await foreach (var raw in ComicArchiveReader.ReadAsync(archive, ct))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(raw.Key).ToLowerInvariant();
            var img = await PageImageProcessor.ProcessAsync(raw.Bytes, ext, maxWidth, maxHeight, ct);
            idx++;
            yield return new EpubWriter.Page($"page-{idx:D4}{img.Extension}", img.Bytes, img.Width, img.Height);
        }
    }
}
```

(Note: `EbookMeta` is declared at the top of `EpubConverter.cs` today — **keep that declaration exactly as shown** in the block above; it is used by both `EpubConverter` and `EpubWriter`. Do not move or duplicate it.)

- [ ] **Step 5: Run the writer + converter tests**

Run: `dotnet test --filter "EpubWriterTests|EpubConverterTests"`
Expected: PASS. `EpubConverterTests` (unchanged) confirm byte-identical output through the streamed path; `EpubWriterTests` confirm the writer + incremental behaviour.

- [ ] **Step 6: Full suite**

Run: `dotnet test`
Expected: PASS (107 + the new streaming test). Confirm no reference to the removed `EpubWriter.Write` remains: `grep -rn "EpubWriter.Write(" src tests` → only `WriteAsync`.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Convert/EpubWriter.cs src/Inkshelf/Convert/EpubConverter.cs tests/Inkshelf.Tests/EpubWriterTests.cs
git commit -m "perf: stream pages into the EPUB instead of buffering all"
```

---

## Task 4: Deployment memory ceiling

**Files:**
- Modify: `docker-compose.example.yml`
- Modify: `README.md`

- [ ] **Step 1: Add a memory limit to the example compose**

In `docker-compose.example.yml`, under the `inkshelf` service, add a memory limit (Compose v2 top-level `mem_limit` works with `docker compose`):

```yaml
    # Cap the sidecar so a conversion can't balloon the host. Sized above the
    # per-conversion peak; conversions run one at a time (MaxConcurrentConversions=1).
    # Re-tune after measuring on your host.
    mem_limit: 1536m
```

(Use `1536m` = 1.5 GiB as the safe starting cap per the on-box report; the maintainer tightens it toward ~512 MiB after re-measuring with the streaming changes.)

- [ ] **Step 2: Note it in the README**

In `README.md`, near the existing `CachePath`/volume deployment notes, add a short line: Inkshelf's memory peaks during a conversion (bounded, one convert at a time); set a container memory limit (start 1.5 GiB, tighten after measuring) so a convert can't pressure the host; `.NET` reads the cgroup limit and self-tunes.

- [ ] **Step 3: Commit**

```bash
git add docker-compose.example.yml README.md
git commit -m "docs: recommend a container memory limit for conversions"
```

---

## Task 5: Documentation

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Update `ARCHITECTURE.md`** (present-tense steady-state voice — NO changelog/"now"/"previously" framing; match surrounding entries)

Reflect, wherever conversion is described:
- Conversion **streams pages** into the EPUB (`EpubWriter.WriteAsync` consuming an `IAsyncEnumerable<Page>` from `EpubConverter`); only one page's bytes are held at a time, and the archive is spooled to a temp file rather than buffered in memory — the footprint is bounded by one page + the file-backed zip.
- After each conversion the worker releases ImageSharp's retained pool.
- The runtime uses **Workstation GC** (single-user sidecar; sequential CPU-bound conversions) — a load-bearing choice, not a default.
- Conversions run **serially** (`MaxConcurrentConversions`), deliberately not parallelised, to bound memory.

- [ ] **Step 2: Update `ROADMAP.md`**

Move the **Runtime footprint** item and the **Conversion memory footprint** bullet (under *Conversion / rendering*) out of their backlog sections and into the **`## Done`** section as concise one-liners (per the roadmap convention: shipped items move to Done, not deleted). Leave the *Conversion speed* item in the backlog with a note that it trades against memory (parallel page processing raises the peak).

- [ ] **Step 3: Commit**

```bash
git add docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "docs: document streaming conversion and Workstation GC"
```

---

## Final verification

- [ ] `dotnet test` green (full suite).
- [ ] `grep -rn "EpubWriter.Write(" src tests` shows only `WriteAsync`.
- [ ] Published `runtimeconfig.json` shows `System.GC.Server=false`.
- [ ] Open the PR (ask the owner first). After merge + image publish, **re-measure on the Zimaboard** (resting after an N-batch; transient peak) vs the ~554 MiB / ~936 MiB post-GC baseline; tighten the container limit if the peak allows.
