# Enlarge Small Pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-device setting, default off, that resamples comic pages UP to the screen box when the scans are smaller than the screen.

**Architecture:** A `bool Upscale` flag travels the existing per-device settings path (`DeviceSettings` cookie to `ScreenTarget.FromCookie` to `RenderTarget`) and flips the two guards that currently encode "never enlarge": `EpubConverter.PageBox` and `PageImageProcessor.FinishAsync`. It also becomes part of the cache key so an upscaled EPUB never overwrites a plain one.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, ImageSharp, xUnit.

## Global Constraints

- **No em dashes (U+2014) and no en dashes (U+2013) anywhere**: not in code, comments, docs, or commit messages. Use a hyphen, a comma, or two sentences.
- **No AOT.** Plain server-rendered HTML, no client JavaScript.
- Conventional Commits: `type: subject`, imperative, lowercase, no period, max ~72 chars. No `Co-Authored-By` and no "Generated with Claude Code" lines.
- Per-task commits on branch `feat/upscale-small-pages` are pre-authorized by the user for this plan.
- Run `dotnet test` from the repo root. All tests must pass before each commit.
- Spec: `docs/superpowers/specs/2026-08-30-upscale-small-pages-design.md`.

---

### Task 1: Upscale in the image processor

`PageImageProcessor.FinishAsync` currently resizes only when the image is bigger than the cap. This task adds an `upscale` flag that makes it resize whenever a cap exists, in either direction, and threads it through `ProcessAsync`. `RenderTarget` gains the property that later tasks read.

**Files:**
- Modify: `src/Inkshelf/Convert/RenderTarget.cs`
- Modify: `src/Inkshelf/Convert/PageImageProcessor.cs`
- Modify: `src/Inkshelf/Convert/EpubConverter.cs` (two call sites only, to keep compiling)
- Test: `tests/Inkshelf.Tests/PageImageProcessorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `RenderTarget.Upscale` (`bool`, init property, default `false`).
  - `PageImageProcessor.ProcessAsync(byte[] bytes, string extension, int maxWidth, int maxHeight, bool grayscale, SpreadMode spread = SpreadMode.Fit, bool padToBox = false, bool upscale = false, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/PageImageProcessorTests.cs`:

```csharp
    [Fact]
    public async Task ProcessAsync_leaves_undersized_alone_without_upscale()
    {
        var r = (await PageImageProcessor.ProcessAsync(Img(1125, 1600, new JpegEncoder()), ".jpg",
            1442, 1787, grayscale: false))[0];
        Assert.Equal(1125, r.Width);
        Assert.Equal(1600, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_enlarges_undersized_with_upscale_keeping_aspect()
    {
        var r = (await PageImageProcessor.ProcessAsync(Img(1125, 1600, new JpegEncoder()), ".jpg",
            1442, 1787, grayscale: false, upscale: true))[0];
        // Fit factor is min(1442/1125, 1787/1600) = 1.116875, limited by the height.
        Assert.Equal(1787, r.Height);
        Assert.Equal(1256, r.Width);
    }

    [Fact]
    public async Task ProcessAsync_upscale_without_a_cap_changes_nothing()
    {
        var r = (await PageImageProcessor.ProcessAsync(Img(80, 120, new JpegEncoder()), ".jpg",
            0, 0, grayscale: false, upscale: true))[0];
        Assert.Equal(80, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public async Task ProcessAsync_upscale_still_downscales_oversized()
    {
        var r = (await PageImageProcessor.ProcessAsync(Img(2644, 3713, new JpegEncoder()), ".jpg",
            1442, 1787, grayscale: false, upscale: true))[0];
        Assert.Equal(1787, r.Height);
        Assert.Equal(1272, r.Width);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PageImageProcessorTests`
Expected: build failure, `ProcessAsync` has no argument named `upscale`.

- [ ] **Step 3: Add the property to RenderTarget**

In `src/Inkshelf/Convert/RenderTarget.cs`, after the `Scale` property inside `RenderTarget`:

```csharp
    // Resample pages UP to the box when the scans are smaller than the screen.
    // Off by default: a reader that honours the declared viewport already enlarges
    // them for free (see EpubConverter.Viewport), so this exists only for the
    // readers that size a page from the image and never enlarge one.
    public bool Upscale { get; init; }
```

- [ ] **Step 4: Thread the flag through PageImageProcessor**

In `src/Inkshelf/Convert/PageImageProcessor.cs`, change the `ProcessAsync` signature to:

```csharp
    public static async Task<ProcessedImage[]> ProcessAsync(byte[] bytes, string extension,
        int maxWidth, int maxHeight, bool grayscale, SpreadMode spread = SpreadMode.Fit,
        bool padToBox = false, bool upscale = false, CancellationToken ct = default)
```

In the split-spread branch, pass it to both halves:

```csharp
            return
            [
                await FinishAsync(first, maxWidth, maxHeight, grayscale, padToBox, upscale, ct),
                await FinishAsync(second, maxWidth, maxHeight, grayscale, padToBox, upscale, ct),
            ];
```

Add `upscale` to the condition that decides whether to decode at all, and to the single-image call. Replace:

```csharp
        if (oversized || rotate || needsPad || extension == ".webp" || grayscale)
        {
            var img = Image.Load(bytes);
            if (rotate) img.Mutate(x => x.Rotate(
                spread == SpreadMode.RotateLeft ? RotateMode.Rotate270 : RotateMode.Rotate90));
            return [await FinishAsync(img, maxWidth, maxHeight, grayscale, padToBox, ct)];
        }
```

with:

```csharp
        // `upscale && box` joins the list: an undersized page is not oversized and
        // needs no pad once it fills the box, so without this it would take the
        // pass-through path and never be enlarged.
        if (oversized || rotate || needsPad || (upscale && box) || extension == ".webp" || grayscale)
        {
            var img = Image.Load(bytes);
            if (rotate) img.Mutate(x => x.Rotate(
                spread == SpreadMode.RotateLeft ? RotateMode.Rotate270 : RotateMode.Rotate90));
            return [await FinishAsync(img, maxWidth, maxHeight, grayscale, padToBox, upscale, ct)];
        }
```

Change `FinishAsync` to take the flag and resize in both directions:

```csharp
    // Resize to fit the cap (aspect preserved), optionally letterbox onto the full
    // cap box, desaturate, encode as JPEG. Takes ownership of img.
    //
    // Without `upscale` this only ever SHRINKS: a page smaller than the cap keeps
    // its pixels and the declared viewport does the enlarging. With it, the same
    // fit factor is applied whichever side of 1 it falls, which is the whole point
    // of the setting.
    private static async Task<ProcessedImage> FinishAsync(Image img,
        int maxWidth, int maxHeight, bool grayscale, bool pad, bool upscale, CancellationToken ct)
    {
        using (img)
        {
            var cap = maxWidth > 0 && maxHeight > 0;
            if (cap && (upscale || img.Width > maxWidth || img.Height > maxHeight))
            {
                var scale = Math.Min((double)maxWidth / img.Width, (double)maxHeight / img.Height);
                img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * scale)),
                                         Math.Max(1, (int)Math.Round(img.Height * scale))));
            }
```

Leave the rest of `FinishAsync` (the `Pad`, `Grayscale`, `SaveAsJpegAsync` block) exactly as it is.

- [ ] **Step 5: Fix the two EpubConverter call sites**

`ct` is currently passed positionally, and the new parameter sits in front of it. In `src/Inkshelf/Convert/EpubConverter.cs`, in `ProcessCoverAsync` change:

```csharp
            var img = (await PageImageProcessor.ProcessAsync(c.Bytes, c.Ext, target.MaxW, target.MaxH,
                target.Grayscale, SpreadMode.Fit, padToBox: false, ct))[0];
```

to:

```csharp
            var img = (await PageImageProcessor.ProcessAsync(c.Bytes, c.Ext, target.MaxW, target.MaxH,
                target.Grayscale, SpreadMode.Fit, padToBox: false, ct: ct))[0];
```

and in `ProcessPagesAsync` change:

```csharp
            foreach (var img in await PageImageProcessor.ProcessAsync(raw.Bytes, ext,
                boxW, boxH, target.Grayscale, target.Spread, padToBox: true, ct))
```

to:

```csharp
            foreach (var img in await PageImageProcessor.ProcessAsync(raw.Bytes, ext,
                boxW, boxH, target.Grayscale, target.Spread, padToBox: true, ct: ct))
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Convert/RenderTarget.cs src/Inkshelf/Convert/PageImageProcessor.cs src/Inkshelf/Convert/EpubConverter.cs tests/Inkshelf.Tests/PageImageProcessorTests.cs
git commit -m "feat: let the page processor enlarge undersized pages"
```

---

### Task 2: Upscale the page box, and never the cover

`EpubConverter.PageBox` returns the scan's own size when it already fits, so the box stays small even with Task 1 in place. This task makes the box honour the flag, passes the flag to the page pipeline, and pins the cover exclusion and the unchanged viewport with tests.

**Files:**
- Modify: `src/Inkshelf/Convert/EpubConverter.cs`
- Test: `tests/Inkshelf.Tests/EpubConverterTests.cs`

**Interfaces:**
- Consumes: `RenderTarget.Upscale` and the `upscale:` parameter from Task 1.
- Produces: nothing new; `EpubConverter.ConvertAsync` keeps its signature.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubConverterTests.cs`. The helpers `Img` and a fresh single-size CBZ are needed, so add this private helper next to `Cbz()`:

```csharp
    // A CBZ of three identical undersized portrait pages, for the upscale tests.
    private static MemoryStream SmallCbz()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            add("page-01.jpg", Img(1125, 1600, new JpegEncoder()));
            add("page-02.jpg", Img(1125, 1600, new JpegEncoder()));
            add("page-03.jpg", Img(1125, 1600, new JpegEncoder()));
        }
        ms.Position = 0; return ms;
    }

    // Pixel size of the first page image in a converted EPUB.
    private static (int W, int H) FirstPageSize(string epubPath)
    {
        using var epub = ZipFile.OpenRead(epubPath);
        var entry = epub.Entries.First(e => e.FullName.EndsWith("page-0001.jpg", StringComparison.Ordinal));
        using var s = entry.Open();
        using var mem = new MemoryStream();
        s.CopyTo(mem);
        var info = Image.Identify(mem.ToArray());
        return (info.Width, info.Height);
    }

    // The viewport declared by the first page's xhtml, as "width=W, height=H".
    private static string FirstPageViewport(string epubPath)
    {
        using var epub = ZipFile.OpenRead(epubPath);
        var entry = epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml", StringComparison.Ordinal));
        using var r = new StreamReader(entry.Open());
        var html = r.ReadToEnd();
        var i = html.IndexOf("content=\"width=", StringComparison.Ordinal) + "content=\"".Length;
        return html[i..html.IndexOf('"', i)];
    }
```

Then the tests:

```csharp
    [Fact]
    public async Task Convert_without_upscale_keeps_undersized_pages_small()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(SmallCbz(), new EbookMeta("Vol 1", "Artist", null, null),
            outPath, new RenderTarget(1442, 1787, 1.875, false), default);

        Assert.Equal((1125, 1600), FirstPageSize(outPath));
    }

    [Fact]
    public async Task Convert_with_upscale_enlarges_pages_to_the_box()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(SmallCbz(), new EbookMeta("Vol 1", "Artist", null, null),
            outPath, new RenderTarget(1442, 1787, 1.875, false) { Upscale = true }, default);

        // Fit factor 1.116875, limited by the height.
        Assert.Equal((1256, 1787), FirstPageSize(outPath));
    }

    [Fact]
    public async Task Convert_with_upscale_declares_the_same_viewport()
    {
        var noUp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var up = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var target = new RenderTarget(1442, 1787, 1.875, false);
        await new EpubConverter().ConvertAsync(SmallCbz(), new EbookMeta("Vol 1", "Artist", null, null), noUp, target, default);
        await new EpubConverter().ConvertAsync(SmallCbz(), new EbookMeta("Vol 1", "Artist", null, null), up,
            target with { Upscale = true }, default);

        // The whole safety argument for the setting: a reader that honours the
        // declared viewport sees an identical layout, only denser pixels.
        Assert.Equal(FirstPageViewport(noUp), FirstPageViewport(up));
    }

    [Fact]
    public async Task Convert_with_upscale_leaves_the_cover_alone()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(SmallCbz(), new EbookMeta("Vol 1", "Artist", null, null),
            outPath, new RenderTarget(1442, 1787, 1.875, false) { Upscale = true }, default,
            cover: (Img(600, 853, new JpegEncoder()), ".jpg"));

        using var epub = ZipFile.OpenRead(outPath);
        var entry = epub.Entries.First(e => e.FullName.Contains("cover", StringComparison.Ordinal)
            && e.FullName.EndsWith(".jpg", StringComparison.Ordinal));
        using var s = entry.Open();
        using var mem = new MemoryStream();
        s.CopyTo(mem);
        var info = Image.Identify(mem.ToArray());
        Assert.Equal(600, info.Width);
        Assert.Equal(853, info.Height);
    }
```

If the cover entry name in this repo's `EpubWriter` is not matched by that `Contains("cover")` filter, open the produced EPUB once, read the actual entry name, and pin the test to it rather than loosening the filter.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EpubConverterTests`
Expected: FAIL. `Convert_with_upscale_enlarges_pages_to_the_box` reports 1125 x 1600 instead of 1256 x 1787.

- [ ] **Step 3: Make the page box honour the flag**

In `src/Inkshelf/Convert/EpubConverter.cs`, replace the last two lines of `PageBox`:

```csharp
        var scale = Math.Min((double)target.MaxW / w, (double)target.MaxH / h);
        return scale >= 1 ? (w, h)
            : (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
```

with:

```csharp
        var scale = Math.Min((double)target.MaxW / w, (double)target.MaxH / h);
        // Upscale off: a page already inside the cap keeps its own size, and the
        // declared viewport does the enlarging. On: the box grows to the cap so the
        // pixels can be resampled into it. Both guards flip together, or the pages
        // would just get a white border padded around an unchanged image.
        return scale >= 1 && !target.Upscale ? (w, h)
            : (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
```

- [ ] **Step 4: Pass the flag to the page pipeline, and not to the cover**

In `ProcessPagesAsync`:

```csharp
            foreach (var img in await PageImageProcessor.ProcessAsync(raw.Bytes, ext,
                boxW, boxH, target.Grayscale, target.Spread, padToBox: true,
                upscale: target.Upscale, ct: ct))
```

In `ProcessCoverAsync` leave the call as Task 1 left it, and add the reason above it:

```csharp
            // upscale is deliberately NOT passed: blowing a small cover up to page
            // size costs bytes for a thumbnail nobody reads.
            var img = (await PageImageProcessor.ProcessAsync(c.Bytes, c.Ext, target.MaxW, target.MaxH,
                target.Grayscale, SpreadMode.Fit, padToBox: false, ct: ct))[0];
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/EpubConverter.cs tests/Inkshelf.Tests/EpubConverterTests.cs
git commit -m "feat: grow the page box when upscaling is on"
```

---

### Task 3: Put the flag in the cache key

Without this an upscaled EPUB and a plain one share a path, so whichever converted last wins and the row state lies about which file is on disk.

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs`
- Modify: `src/Inkshelf/Convert/ConvertService.cs:106`
- Modify: `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs:28`
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs:80-81`
- Test: `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Consumes: `RenderTarget.Upscale` from Task 1.
- Produces:
  - `EpubCache.PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100, double dpr = 1, bool upscale = false)`.
  - `EpubCache.CachedVariant(... SpreadMode Spread = SpreadMode.Fit, int Scale = 100, double Dpr = 1, bool Upscale = false)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubCacheTests.cs`:

```csharp
    [Fact]
    public void PathFor_upscale_differs_from_plain()
    {
        var c = new EpubCache(TempDirPath());
        Assert.NotEqual(
            c.PathFor("i1", 100, 200, 1442, 1787, upscale: false),
            c.PathFor("i1", 100, 200, 1442, 1787, upscale: true));
    }

    [Fact]
    public void PathFor_marks_upscale_between_grayscale_and_spread()
    {
        var c = new EpubCache(TempDirPath());
        Assert.EndsWith("i1-100-200-1442x1787-g-u-f.epub",
            c.PathFor("i1", 100, 200, 1442, 1787, grayscale: true, upscale: true));
    }

    [Fact]
    public void ListVariants_round_trips_upscale()
    {
        using var d = new TempDir();
        var c = new EpubCache(d.Path);
        File.WriteAllText(c.PathFor("i1", 100, 200, 1442, 1787, grayscale: true,
            spread: SpreadMode.RotateLeft, scale: 98, dpr: 1.875, upscale: true), "x");
        var v = Assert.Single(c.ListVariants());
        Assert.True(v.Upscale);
        Assert.Equal(SpreadMode.RotateLeft, v.Spread);
        Assert.Equal(98, v.Scale);
        Assert.Equal(1.875, v.Dpr);
        Assert.True(v.Grayscale);
    }

    [Fact]
    public void ListVariants_reads_a_name_without_the_marker_as_not_upscaled()
    {
        using var d = new TempDir();
        var c = new EpubCache(d.Path);
        File.WriteAllText(c.PathFor("i1", 100, 200, 1442, 1787, grayscale: true), "x");
        Assert.False(Assert.Single(c.ListVariants()).Upscale);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EpubCacheTests`
Expected: build failure, `PathFor` has no argument named `upscale`.

- [ ] **Step 3: Write and parse the marker**

In `src/Inkshelf/Convert/EpubCache.cs`, replace `PathFor` with:

```csharp
    public string PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH,
        bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100, double dpr = 1,
        bool upscale = false) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}{(grayscale ? "-g" : "")}"
            + $"{(upscale ? "-u" : "")}-{Letter(spread)}{(scale == 100 ? "" : $"-s{scale}")}"
            + (dpr == 1 ? "" : $"-d{dpr.ToString(CultureInfo.InvariantCulture)}") + ".epub");
```

`u` is safe here: it is none of the spread letters (l, m, a, c, f) and cannot be confused with the `-s` scale or `-d` ratio suffixes, which carry digits.

Add the field to `CachedVariant`:

```csharp
    public sealed record CachedVariant(
        string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path,
        DateTime ConvertedAtUtc, SpreadMode Spread = SpreadMode.Fit, int Scale = 100, double Dpr = 1,
        bool Upscale = false);
```

In `TryParse`, the marker is stripped between the spread letter and grayscale, mirroring the write order. After the block that strips the spread letter and before the grayscale block, insert:

```csharp
        // Absent in every file written before this setting existed, and absent is
        // exactly right for those: they were converted without upscaling.
        var upscale = name.EndsWith("-u", StringComparison.Ordinal);
        if (upscale) name = name[..^2];
```

and extend the return:

```csharp
        return new CachedVariant(itemId, size, mtimeMs, maxW, maxH, grayscale, path,
            file.LastWriteTimeUtc, spread, scale, dpr, upscale);
```

Also update the comment above the parse block from `// Parsed in the reverse of PathFor's order: dpr, scale, spread, grayscale, dims.` to `// Parsed in the reverse of PathFor's order: dpr, scale, spread, upscale, grayscale, dims.`

- [ ] **Step 4: Update the two PathFor call sites and the variant match**

In `src/Inkshelf/Convert/ConvertService.cs`, line 106:

```csharp
        var path = _cache.PathFor(id, size, mtime, target.MaxW, target.MaxH, target.Grayscale, target.Spread, target.Scale, target.Dpr, target.Upscale);
```

In `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs`, line 28:

```csharp
        var path = cache.PathFor(itemId, size, mtimeMs, target.MaxW, target.MaxH, target.Grayscale, target.Spread, target.Scale, target.Dpr, target.Upscale);
```

In `src/Inkshelf/Pages/Converted.cshtml.cs`, extend the variant filter:

```csharp
            if (v.MaxW != target.MaxW || v.MaxH != target.MaxH || v.Grayscale != target.Grayscale
                || v.Spread != target.Spread || v.Scale != target.Scale || v.Dpr != target.Dpr
                || v.Upscale != target.Upscale) continue;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs src/Inkshelf/Convert/ConvertService.cs src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs src/Inkshelf/Pages/Converted.cshtml.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: key the epub cache on the upscale flag"
```

---

### Task 4: Carry the flag in the settings cookie

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Modify: `src/Inkshelf/Auth/DeviceSettingsTargetExtensions.cs`
- Modify: `src/Inkshelf/Convert/ScreenTarget.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`
- Test: `tests/Inkshelf.Tests/ScreenTargetTests.cs`

**Interfaces:**
- Consumes: `RenderTarget.Upscale` from Task 1.
- Produces:
  - `DeviceSettings.Upscale` (`bool`, init property, default `false`), cookie key `up`.
  - `ScreenTarget.FromCookie(string? scr, bool retina = false, bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100, ScreenOverride? over = null, bool upscale = false)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`:

```csharp
    [Fact]
    public void Upscale_defaults_off()
    {
        Assert.False(DeviceSettings.Default.Upscale);
    }

    [Fact]
    public void Upscale_round_trips_through_the_wire_format()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery(
            (DeviceSettings.Default with { Upscale = true }).Serialize()));
        Assert.True(DeviceSettings.FromQuery(q)!.Upscale);
    }

    [Fact]
    public void Upscale_absent_from_an_older_cookie_reads_as_off()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("retina=1&gray=0&lang=&fav="));
        Assert.False(DeviceSettings.FromQuery(q)!.Upscale);
    }

    [Fact]
    public void Upscale_alone_is_enough_to_recognise_a_settings_query()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("up=1"));
        Assert.True(DeviceSettings.FromQuery(q)!.Upscale);
    }
```

If `DeviceSettingsTests.cs` lacks them, add `using Microsoft.AspNetCore.Http;` and `using Microsoft.AspNetCore.WebUtilities;` at the top. Match whatever helper the neighbouring tests in that file already use for building a query, rather than introducing a second style.

Add to `tests/Inkshelf.Tests/ScreenTargetTests.cs`:

```csharp
    [Fact]
    public void FromCookie_carries_upscale_into_the_target()
    {
        Assert.True(ScreenTarget.FromCookie("769x953x1.875", retina: true, upscale: true).Upscale);
        Assert.False(ScreenTarget.FromCookie("769x953x1.875", retina: true).Upscale);
    }

    [Fact]
    public void FromCookie_carries_upscale_on_the_override_path()
    {
        var over = new ScreenOverride(1442, 1787, 1.875);
        Assert.True(ScreenTarget.FromCookie(null, retina: true, over: over, upscale: true).Upscale);
    }

    [Fact]
    public void FromCookie_carries_upscale_when_there_is_no_probe()
    {
        Assert.True(ScreenTarget.FromCookie(null, upscale: true).Upscale);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests|FullyQualifiedName~ScreenTargetTests"`
Expected: build failure, no `Upscale` member and no `upscale` argument.

- [ ] **Step 3: Add the setting**

In `src/Inkshelf/Auth/DeviceSettings.cs`, after the `Scale` / `MinScale` block:

```csharp
    // Resample pages UP to the screen box when the scans are smaller than it. An
    // init property for the same reason as Fav: the existing three-argument
    // construction sites keep compiling.
    //
    // Off by default and deliberately NOT folded into Retina. Retina is on by
    // default, so folding it in would change every existing conversion, including
    // for readers that honour the declared viewport and already get the
    // enlargement for free. This is only for the readers that do not.
    public bool Upscale { get; init; }
```

Add the key to `Serialize`, on the line that already carries spread and scale:

```csharp
        + $"&spread={Spread.ToString().ToLowerInvariant()}&scale={Scale}&up={(Upscale ? 1 : 0)}"
```

Add `"up"` to `Keys`:

```csharp
    private static readonly string[] Keys =
        ["retina", "gray", "lang", "fav", "did", "spread", "scale", "up", "ovr", "ovrw", "ovrh", "ovrd"];
```

And read it in `Parse`, next to `Scale`:

```csharp
            Upscale = Flag(q, "up", Default.Upscale),
```

- [ ] **Step 4: Carry it to the render target**

In `src/Inkshelf/Auth/DeviceSettingsTargetExtensions.cs`:

```csharp
    public static RenderTarget ToRenderTarget(this DeviceSettings s, string? scr) =>
        ScreenTarget.FromCookie(scr, s.Retina, s.Grayscale, s.Spread, s.Scale, s.ActiveOverride, s.Upscale);
```

In `src/Inkshelf/Convert/ScreenTarget.cs`, extend the signature:

```csharp
    public static RenderTarget FromCookie(string? scr, bool retina = false, bool grayscale = false,
        SpreadMode spread = SpreadMode.Fit, int scale = 100, ScreenOverride? over = null,
        bool upscale = false)
```

Then add `Upscale = upscale` to the object initialiser of every one of the five `return new RenderTarget(...)` statements in that method, including the final no-probe one. For example the first becomes:

```csharp
            return retina
                ? new RenderTarget(ow, oh, od, grayscale) { Spread = spread, Scale = scale, Upscale = upscale }
                : new RenderTarget(Math.Max(1, (int)Math.Round(ow / od)),
                                   Math.Max(1, (int)Math.Round(oh / od)), 1, grayscale)
                { Spread = spread, Scale = scale, Upscale = upscale };
```

Missing one is a silent bug, not a compile error, which is what the three `ScreenTargetTests` above are guarding.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs src/Inkshelf/Auth/DeviceSettingsTargetExtensions.cs src/Inkshelf/Convert/ScreenTarget.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs tests/Inkshelf.Tests/ScreenTargetTests.cs
git commit -m "feat: carry the upscale flag in the device settings"
```

---

### Task 5: The checkbox

**Files:**
- Modify: `src/Inkshelf/Pages/Settings.cshtml`
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Inkshelf/locales/de.json`
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `DeviceSettings.Upscale` from Task 4.
- Produces: form field `upscale`, following the existing absent-means-off convention.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/EndpointTests.cs`. There is no shared settings-POST
helper in that file; every settings test inlines the client, the antiforgery
token and the form, so these follow the same shape as
`Saving_with_the_override_off_keeps_the_numbers` at line 400.

```csharp
    [Fact]
    public async Task Saving_upscale_records_it_and_absence_clears_it()
    {
        // Unchecked checkboxes submit nothing, so absent means off - the same
        // convention retina and grayscale already rely on.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var on = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["upscale"] = "on",
        }));
        Assert.Contains("up=1", on.Headers.Location!.OriginalString);

        var off = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
        }));
        Assert.Contains("up=0", off.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Settings_page_renders_the_upscale_checkbox()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"upscale\"", html);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~Saving_upscale_records_it|FullyQualifiedName~Settings_page_renders_the_upscale"`
Expected: FAIL. The redirect carries no `up=` key at all, and the settings HTML has no `upscale` field.

- [ ] **Step 3: Read the field in the POST handler**

In `src/Inkshelf/Endpoints/SettingsEndpoints.cs`, in the `stored with` block, next to `Grayscale`:

```csharp
                Upscale = form.ContainsKey("upscale"),
```

- [ ] **Step 4: Add the checkbox**

In `src/Inkshelf/Pages/Settings.cshtml`, after the grayscale paragraph:

```html
    <p>
        <label>
            <input type="checkbox" name="upscale" value="on" @(Model.Settings.Upscale ? "checked" : "") />
            @L["Enlarge small pages (for readers that draw pages at their own size)"]
        </label>
    </p>
```

- [ ] **Step 5: Add the German string**

In `src/Inkshelf/locales/de.json`, add an entry keyed by the exact English string above. Match the file's existing formatting and key ordering:

```json
  "Enlarge small pages (for readers that draw pages at their own size)": "Kleine Seiten vergroessern (fuer Reader, die Seiten in ihrer eigenen Groesse zeichnen)",
```

Use the umlauts the rest of the file uses; the transliteration above is only to keep this plan plain ASCII. Check how neighbouring German strings spell it and follow them.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Pages/Settings.cshtml src/Inkshelf/Endpoints/SettingsEndpoints.cs src/Inkshelf/locales/de.json tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: add the enlarge-small-pages checkbox"
```

---

### Task 6: Documentation and the browser pass

**Files:**
- Modify: `docs/FAQ.md`
- Modify: `docs/tolino.md`
- Modify: `docs/DEVICES.md`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/ROADMAP.md`
- Modify: `tools/uicheck/run.sh` if it asserts on the settings page contents

**Interfaces:**
- Consumes: the finished setting from Tasks 1 to 5.
- Produces: nothing code-facing.

- [ ] **Step 1: Fix the wrong FAQ entry**

`docs/FAQ.md` currently says, under "Pages are much smaller than the screen", that turning retina on or raising the screen override makes the images bigger. Both only raise a ceiling, which does nothing to a scan already below it. Replace that entry's body with:

```markdown
Page images are only ever shrunk to fit, never enlarged, so a reader that draws
them at their own size shows a small page. Tick **Enlarge small pages** in
Settings: it resamples the pages up to the screen instead of leaving them small.
Files get bigger, which is the trade. Retina must stay on for it to have room to
work, since the enlargement target is the screen in physical pixels.
```

- [ ] **Step 2: Note it on the standard reader**

In `docs/tolino.md`, extend the **standard** reader bullet with one sentence: it is the reader that "Enlarge small pages" exists for, because it sizes pages from the image and never enlarges one.

- [ ] **Step 3: Record the epos setting**

In `docs/DEVICES.md`, the Tolino epos 2 row's *Working settings* cell gains a mention that "Enlarge small pages" is what fills the screen on the standard reader. Keep the existing text about page scale 98 on the beta reader.

- [ ] **Step 4: Qualify the viewport invariant**

In `docs/ARCHITECTURE.md`, the bullet beginning "**The declared viewport is scaled up to the cap**" ends with the claim that the image keeps its own pixels. Add one clause noting that "Enlarge small pages" opts out of that for readers which ignore the declared viewport, and that the two guards in `EpubConverter.PageBox` and `PageImageProcessor.FinishAsync` must always flip together. Do NOT add a new bullet: `CLAUDE.md` is explicit that shipped features do not each earn an architecture entry.

- [ ] **Step 5: Move it to Done**

Add the setting to `docs/ROADMAP.md`'s `## Done` section, matching the format of the entries already there. Do NOT touch `CHANGELOG.md`; that belongs to the release skill.

- [ ] **Step 6: Run the browser pass**

Run: `tools/uicheck/run.sh`
Expected: exit 0. Then actually open the settings screenshots in `tools/uicheck/shots/` and confirm the new checkbox renders in both English and German without pushing the layout wide. If the script asserts on settings-page strings, extend it to cover the new label.

- [ ] **Step 7: Verify no em or en dashes were introduced**

Run: `git diff main --stat && ! git diff main | grep -nP '^\+.*[\x{2013}\x{2014}]'`
Expected: the grep finds nothing, so the command exits 0.

- [ ] **Step 8: Commit**

```bash
git add docs/ tools/
git commit -m "docs: document the enlarge-small-pages setting"
```

---

## After the plan

The headless pass does not reproduce the old e-ink reader engine, so a real device pass by the user stays mandatory: convert one comic on the epos with the setting on, confirm the page fills the screen, and confirm the beta reader still looks unchanged. Then use `superpowers:finishing-a-development-branch`.
