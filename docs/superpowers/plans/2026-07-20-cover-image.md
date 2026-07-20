# Cover Image Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The converted EPUB declares a real cover (thumbnail) so strict readers (Apple Books) show it instead of a blank placeholder.

**Architecture:** The token-less background `ConvertWorker` best-effort fetches the ABS cover via a new `AbsDownloadClient.DownloadCoverAsync` (captured bearer, no `HttpContext`). It hands the raw bytes to `EpubConverter`, which runs them through the existing `PageImageProcessor` and passes the result to `EpubWriter`. The writer declares the cover with **both** the EPUB3 `properties="cover-image"` manifest flag and the EPUB2 `<meta name="cover">`. When no ABS cover is usable, the first page image is flagged as the cover instead — no extra file, no reading-flow change.

**Tech Stack:** ASP.NET Core / .NET 10, ImageSharp (SixLabors), xUnit. String-built EPUB XML.

## Global Constraints

- **No AOT.** .NET 10, ASP.NET Core Razor Pages + minimal APIs.
- **String-built EPUB XML** in `EpubWriter` — do NOT introduce an XML library.
- **Thumbnail only** — the cover is metadata, never a spine entry; reading still opens on page 1.
- **Fixed request width 600px**, independent of the device page cap. The device cap (`target.MaxW/MaxH`) still acts as an upper bound inside `PageImageProcessor` (it only downscales, never upscales).
- **The cover must never fail the conversion** — any fetch/decode failure falls back to the first page.
- **No cache-key change** — the cover derives deterministically from the item; `EpubCache.PathFor` is untouched.
- All work on branch `feat/cover-image`. `dotnet test` from repo root (inside the devcontainer) must stay green.
- Conventional Commits; no `Co-Authored-By`/"Generated with" lines.

---

### Task 1: `AbsDownloadClient.DownloadCoverAsync`

Add the worker's token-less cover fetch, mirroring `DownloadEbookAsync`. No DI change needed — `AbsDownloadClient` is already registered (`Program.cs:57`).

**Files:**
- Modify: `src/Inkshelf/Abs/AbsDownloadClient.cs`
- Test: `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Task<(Stream Content, string ContentType)> AbsDownloadClient.DownloadCoverAsync(string itemId, string accessToken, int width, CancellationToken ct)` — returns the cover stream + its content-type; throws `HttpRequestException` on non-2xx. Caller owns/disposes the stream.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/AbsDownloadClientTests.cs` (the `Client(...)` helper and `using System.Net;` / `using Inkshelf.Abs;` already exist at the top of the file):

```csharp
    [Fact]
    public async Task DownloadCover_sends_bearer_and_width_and_returns_stream_and_type()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 9, 8, 7 })
            { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") } }
        });
        var client = Client(stub);

        var (stream, type) = await client.DownloadCoverAsync("item9", "TOKEN123", 600, default);
        await using var _ = stream;

        Assert.Equal("/api/items/item9/cover", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("width=600", stub.Last!.RequestUri!.Query.TrimStart('?'));
        Assert.Equal("Bearer", stub.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("TOKEN123", stub.Last!.Headers.Authorization!.Parameter);
        Assert.Equal("image/png", type);
        Assert.Equal(3, stream.Length);
    }

    [Fact]
    public async Task DownloadCover_throws_on_404()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DownloadCoverAsync("item9", "tok", 600, default));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AbsDownloadClientTests`
Expected: FAIL — `AbsDownloadClient` does not contain a definition for `DownloadCoverAsync` (compile error).

- [ ] **Step 3: Implement `DownloadCoverAsync`**

In `src/Inkshelf/Abs/AbsDownloadClient.cs`, add this method after `DownloadEbookAsync` (inside the class):

```csharp
    // The worker's token-less cover fetch. Mirrors DownloadEbookAsync: handler-free,
    // caller-supplied bearer, NO 401 refresh. Caller owns (and must dispose) the stream.
    public async Task<(Stream Content, string ContentType)> DownloadCoverAsync(
        string itemId, string accessToken, int width, CancellationToken ct)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/cover?width={width}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new HttpRequestException($"cover download failed for {itemId}: {(int)res.StatusCode}");
        }
        var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return (await res.Content.ReadAsStreamAsync(ct), type);
    }
```

(`System.Net.Http.Headers` is already imported at the top of the file for `AuthenticationHeaderValue`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AbsDownloadClientTests`
Expected: PASS (all `AbsDownloadClientTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Abs/AbsDownloadClient.cs tests/Inkshelf.Tests/AbsDownloadClientTests.cs
git commit -m "feat: add token-less ABS cover download for the worker"
```

---

### Task 2: `EpubWriter` cover declaration

Teach the writer to declare a cover. Add a `Cover` record + optional `WriteAsync` parameter, extract a mime helper, and update `Opf(...)` to emit the EPUB3 `properties="cover-image"` flag and the EPUB2 `<meta name="cover">` — using the dedicated cover item when present, else flagging the first page, else nothing.

**Files:**
- Modify: `src/Inkshelf/Convert/EpubWriter.cs`
- Test: `tests/Inkshelf.Tests/EpubWriterTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public sealed record Cover(byte[] Bytes, string Ext);` (nested in `EpubWriter`; `Ext` includes the leading dot, e.g. `".jpg"`).
  - `EpubWriter.WriteAsync(string outPath, EbookMeta meta, IAsyncEnumerable<Page> pages, double dpr, CancellationToken ct, Cover? cover = null)` — the trailing optional `cover` is the only signature change; all existing calls keep compiling.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubWriterTests.cs` (the `Jpg(...)` and `Stream(...)` helpers already exist):

```csharp
    [Fact]
    public async Task WriteAsync_with_cover_declares_cover_image_both_ways()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null),
            Stream(new EpubWriter.Page("page-0001.jpg", Jpg(80, 120), 80, 120)),
            dpr: 1, default, new EpubWriter.Cover(Jpg(600, 900), ".jpg"));

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("OEBPS/cover.jpg", names);
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("id=\"cover-img\"", opf);
        Assert.Contains("href=\"cover.jpg\"", opf);
        Assert.Contains("properties=\"cover-image\"", opf);
        Assert.Contains("<meta name=\"cover\" content=\"cover-img\"/>", opf);
        File.Delete(outPath);
    }

    [Fact]
    public async Task WriteAsync_without_cover_flags_first_page_as_cover()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null),
            Stream(new EpubWriter.Page("page-0001.jpg", Jpg(80, 120), 80, 120),
                   new EpubWriter.Page("page-0002.jpg", Jpg(80, 120), 80, 120)),
            dpr: 1, default);

        using var epub = ZipFile.OpenRead(outPath);
        Assert.DoesNotContain(epub.Entries.Select(e => e.FullName), n => n.StartsWith("OEBPS/cover"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"img1\"/>", opf);
        Assert.Contains("id=\"img1\" href=\"img/page-0001.jpg\" media-type=\"image/jpeg\" properties=\"cover-image\"/>", opf);
        Assert.DoesNotContain("id=\"img2\" href=\"img/page-0002.jpg\" media-type=\"image/jpeg\" properties=\"cover-image\"", opf);
        File.Delete(outPath);
    }

    [Fact]
    public async Task WriteAsync_with_no_pages_and_no_cover_declares_no_cover()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null),
            Stream(), dpr: 1, default);

        using var epub = ZipFile.OpenRead(outPath);
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.DoesNotContain("cover-image", opf);
        Assert.DoesNotContain("name=\"cover\"", opf);
        File.Delete(outPath);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EpubWriterTests`
Expected: FAIL — `EpubWriter.Cover` does not exist / `WriteAsync` has no 6th parameter (compile error).

- [ ] **Step 3: Add the `Cover` record and thread it through `WriteAsync`**

In `src/Inkshelf/Convert/EpubWriter.cs`, add the record next to the existing `Page` record (after line 13):

```csharp
    // A processed cover image: its bytes and in-zip extension (with dot, e.g. ".jpg").
    // Metadata-only — declared as the cover, never added to the spine.
    public sealed record Cover(byte[] Bytes, string Ext);
```

Change the `WriteAsync` signature to add the trailing optional parameter:

```csharp
    public static async Task WriteAsync(string outPath, EbookMeta meta,
        IAsyncEnumerable<Page> pages, double dpr, CancellationToken ct, Cover? cover = null)
```

Inside `WriteAsync`, write the cover image entry right after the `META-INF/container.xml` `Write(...)` call (before the `var i = 0;` page loop):

```csharp
            // Dedicated cover image (ABS cover), when present. Order after mimetype
            // is flexible; the manifest item + <meta> are emitted in Opf below.
            if (cover is { } cv)
            { using var s = zip.CreateEntry($"OEBPS/cover{cv.Ext}").Open(); s.Write(cv.Bytes); }
```

Change the OPF write call (currently `Write("OEBPS/content.opf", Opf(meta, metas));`) to pass the cover:

```csharp
            Write("OEBPS/content.opf", Opf(meta, metas, cover));
```

- [ ] **Step 4: Add the mime helper and update `Opf(...)`**

In `src/Inkshelf/Convert/EpubWriter.cs`, add this helper (e.g. just below the `Esc` helper):

```csharp
    private static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
```

Replace the entire `Opf` method with this version (adds the `cover` parameter, the EPUB2 `<meta name="cover">`, the dedicated cover manifest item, and the first-page fallback flag; the per-page mime now goes through `MimeFor`):

```csharp
    private static string Opf(EbookMeta m, IReadOnlyList<PageMeta> pages, Cover? cover)
    {
        // The manifest id used as the cover: a dedicated cover item when an ABS
        // cover was embedded, else the first page image (fallback), else none.
        var coverId = cover is not null ? "cover-img" : (pages.Count > 0 ? "img1" : null);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" prefix=\"rendition: http://www.idpf.org/vocab/rendition/#\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        sb.Append($"<dc:identifier id=\"bookid\">{Esc(Uid(m))}</dc:identifier><dc:title>{Esc(m.Title)}</dc:title><dc:language>en</dc:language><dc:creator>{Esc(m.Author)}</dc:creator>");
        // dcterms:modified is required by EPUB3; without it readers flag the book.
        sb.Append($"<meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>");
        // Fixed-layout, single page per screen.
        sb.Append("<meta property=\"rendition:layout\">pre-paginated</meta><meta property=\"rendition:spread\">none</meta>");
        if (!string.IsNullOrEmpty(m.Series)) sb.Append($"<meta name=\"calibre:series\" content=\"{Esc(m.Series)}\"/>");
        if (!string.IsNullOrEmpty(m.Sequence)) sb.Append($"<meta name=\"calibre:series_index\" content=\"{Esc(m.Sequence)}\"/>");
        // EPUB2 cover pointer (references the manifest item id). Paired with the
        // EPUB3 properties="cover-image" flag on that same item in the manifest.
        if (coverId is not null) sb.Append($"<meta name=\"cover\" content=\"{coverId}\"/>");
        // NCX (EPUB2) alongside the EPUB3 nav for older readers (e.g. Tolino).
        sb.Append("</metadata><manifest><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        // Dedicated cover image (ABS cover), when present.
        if (cover is { } c)
            sb.Append($"<item id=\"cover-img\" href=\"cover{c.Ext}\" media-type=\"{MimeFor(c.Ext)}\" properties=\"cover-image\"/>");
        for (var i = 0; i < pages.Count; i++)
        {
            // Fallback: flag the FIRST page image as the cover when no ABS cover.
            var props = (cover is null && i == 0) ? " properties=\"cover-image\"" : "";
            sb.Append($"<item id=\"img{i + 1}\" href=\"img/{pages[i].Name}\" media-type=\"{MimeFor(Path.GetExtension(pages[i].Name))}\"{props}/>");
            sb.Append($"<item id=\"pg{i + 1}\" href=\"page-{i + 1:D4}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        }
        sb.Append("</manifest><spine toc=\"ncx\">");
        for (var i = 0; i < pages.Count; i++) sb.Append($"<itemref idref=\"pg{i + 1}\" properties=\"rendition:layout-pre-paginated\"/>");
        sb.Append("</spine></package>");
        return sb.ToString();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~EpubWriterTests`
Expected: PASS (new cover tests + the existing `WriteAsync_*` tests, which pass no cover and are unaffected).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/EpubWriter.cs tests/Inkshelf.Tests/EpubWriterTests.cs
git commit -m "feat: declare an EPUB cover (cover-image + EPUB2 meta) with first-page fallback"
```

---

### Task 3: `EpubConverter` cover processing

Accept a raw cover on `ConvertAsync`, run it through `PageImageProcessor` (same cap/grayscale/WebP→JPEG as pages), and hand the result to the writer. A cover that fails to decode is dropped so the writer falls back to the first page.

**Files:**
- Modify: `src/Inkshelf/Convert/EpubConverter.cs`
- Test: `tests/Inkshelf.Tests/EpubConverterTests.cs`

**Interfaces:**
- Consumes: `EpubWriter.Cover`, `PageImageProcessor.ProcessAsync(byte[], string, int, int, bool, CancellationToken)` → `ProcessedImage(byte[] Bytes, string Extension, int Width, int Height)`.
- Produces: `EpubConverter.ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target, CancellationToken ct, (byte[] Bytes, string Ext)? cover = null)` — trailing optional `cover` is the only signature change; existing calls keep compiling.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubConverterTests.cs` (the `Img(...)` helper and the Webp/Png/Jpeg encoder imports already exist at the top of the file):

```csharp
    [Fact]
    public async Task Convert_embeds_supplied_cover_transcoding_webp_to_jpg()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("T", "A", null, null),
            outPath, new RenderTarget(0, 0, 1, false), default,
            (Img(300, 450, new WebpEncoder()), ".webp"));

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("OEBPS/cover.jpg", names);              // webp transcoded to jpg
        Assert.DoesNotContain(names, n => n == "OEBPS/cover.webp");
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("id=\"cover-img\"", opf);
        Assert.Contains("<meta name=\"cover\" content=\"cover-img\"/>", opf);
        File.Delete(outPath);
    }

    [Fact]
    public async Task Convert_with_undecodable_cover_falls_back_to_first_page()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("T", "A", null, null),
            outPath, new RenderTarget(0, 0, 1, false), default,
            (new byte[] { 1, 2, 3, 4 }, ".jpg"));               // not a real image

        using var epub = ZipFile.OpenRead(outPath);
        Assert.DoesNotContain(epub.Entries.Select(e => e.FullName), n => n.StartsWith("OEBPS/cover"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"img1\"/>", opf);
        File.Delete(outPath);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EpubConverterTests`
Expected: FAIL — `ConvertAsync` has no 6th parameter (compile error).

- [ ] **Step 3: Thread the cover through `ConvertAsync`**

In `src/Inkshelf/Convert/EpubConverter.cs`, change `ConvertAsync` and add a private helper:

```csharp
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target,
        CancellationToken ct, (byte[] Bytes, string Ext)? cover = null)
    {
        var dpr = target.Dpr <= 0 ? 1 : target.Dpr;
        var processedCover = await ProcessCoverAsync(cover, target, ct);
        await EpubWriter.WriteAsync(outPath, meta,
            ProcessPagesAsync(archive, target.MaxW, target.MaxH, target.Grayscale, ct), dpr, ct, processedCover);
    }

    // Process the raw ABS cover through the same pipeline as pages (cap, grayscale,
    // WebP→JPEG). A cover that fails to decode is dropped (null) so the writer falls
    // back to flagging the first page — a bad cover must never fail the conversion.
    private static async Task<EpubWriter.Cover?> ProcessCoverAsync(
        (byte[] Bytes, string Ext)? cover, RenderTarget target, CancellationToken ct)
    {
        if (cover is not { } c) return null;
        try
        {
            var img = await PageImageProcessor.ProcessAsync(c.Bytes, c.Ext, target.MaxW, target.MaxH, target.Grayscale, ct);
            return new EpubWriter.Cover(img.Bytes, img.Extension);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~EpubConverterTests`
Expected: PASS (new cover tests + the existing `Convert_*` tests, which pass no cover and are unaffected).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/EpubConverter.cs tests/Inkshelf.Tests/EpubConverterTests.cs
git commit -m "feat: process and embed the ABS cover during conversion"
```

---

### Task 4: `ConvertWorker` cover fetch + wiring

Best-effort fetch the ABS cover at 600px inside the convert lock, then pass its bytes + extension to `ConvertAsync`. Any failure yields `null` (first-page fallback). Also add a test helper that routes `/ebook` vs `/cover` requests.

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs`
- Test: `tests/Inkshelf.Tests/ConvertWorkerTests.cs`

**Interfaces:**
- Consumes: `AbsDownloadClient.DownloadCoverAsync(...)` (Task 1), `EpubConverter.ConvertAsync(..., cover)` (Task 3).
- Produces: no public surface change (`ConvertWorker` behavior only).

- [ ] **Step 1: Write the failing tests**

In `tests/Inkshelf.Tests/ConvertWorkerTests.cs`, add a cover-capable JPEG helper next to `Cbz()`:

```csharp
    private static byte[] CoverJpg(int w = 300, int h = 450)
    {
        using var ms = new MemoryStream();
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        img.Save(ms, new JpegEncoder()); return ms.ToArray();
    }

    // A DI provider whose AbsDownloadClient returns `ebook` for /ebook and, for
    // /cover, either `cover` bytes (status 200) or the given error status.
    private static IServiceScopeFactory ScopeFactoryFor(
        byte[] ebook, byte[]? cover = null, string coverType = "image/jpeg", int coverStatus = 200)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/cover"))
                {
                    if (coverStatus != 200 || cover is null)
                        return new HttpResponseMessage((System.Net.HttpStatusCode)coverStatus);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(cover)
                        { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(coverType) } }
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(ebook) };
            }))
            { BaseAddress = new Uri("http://abs.local") }));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
```

Add these tests to the class:

```csharp
    [Fact]
    public async Task Embeds_the_ABS_cover_when_available()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryFor(Cbz(), CoverJpg(), "image/jpeg"), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        using var epub = ZipFile.OpenRead(path);
        Assert.Contains("OEBPS/cover.jpg", epub.Entries.Select(e => e.FullName));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"cover-img\"/>", opf);
    }

    [Fact]
    public async Task A_missing_ABS_cover_still_produces_an_epub_with_first_page_cover()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryFor(Cbz(), coverStatus: 404), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        using var epub = ZipFile.OpenRead(path);
        Assert.DoesNotContain(epub.Entries.Select(e => e.FullName), n => n.StartsWith("OEBPS/cover"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"img1\"/>", opf);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ConvertWorkerTests`
Expected: FAIL — `Embeds_the_ABS_cover_when_available` fails (no `OEBPS/cover.jpg` / no `cover-img`) because the worker does not yet fetch or pass a cover.

- [ ] **Step 3: Fetch the cover and pass it to the converter**

In `src/Inkshelf/Convert/ConvertWorker.cs`, add the cover width constant and helpers inside the class (e.g. above `CopyWithLimitAsync`):

```csharp
    // Thumbnail-appropriate cover width requested from ABS. Not the device page cap:
    // the cover is only ever a thumbnail, so page-resolution art would just bloat the
    // file. The device cap still bounds it on the way through PageImageProcessor.
    private const int CoverWidth = 600;

    // Best-effort ABS cover fetch. Any failure (no cover / 404 / transient) yields
    // null and the converter falls back to the first page — never fails the job.
    // Cancellation (app stopping) is allowed to propagate.
    private static async Task<(byte[] Bytes, string Ext)?> TryFetchCoverAsync(
        AbsDownloadClient download, ConvertJob job, CancellationToken ct)
    {
        try
        {
            var (stream, contentType) = await download.DownloadCoverAsync(job.ItemId, job.AccessToken, CoverWidth, ct);
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                return (ms.ToArray(), CoverExt(contentType));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string CoverExt(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };
```

Then, in `ProcessAsync`, replace the convert block (currently):

```csharp
                await using (var read = new FileStream(dlTmp, FileMode.Open, FileAccess.Read))
                    await _converter.ConvertAsync(read, job.Meta, job.CachePath, job.Target, ct);
```

with:

```csharp
                // Best-effort cover (small; never fails the job). Uses the same
                // captured token as the ebook download.
                var cover = await TryFetchCoverAsync(download, job, ct);

                await using (var read = new FileStream(dlTmp, FileMode.Open, FileAccess.Read))
                    await _converter.ConvertAsync(read, job.Meta, job.CachePath, job.Target, ct, cover);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ConvertWorkerTests`
Expected: PASS — including the existing tests (`ScopeFactoryReturning` returns the CBZ for the cover request too; it fails to decode and falls back to the first page, which those tests don't assert on).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS (all tests green).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/ConvertWorker.cs tests/Inkshelf.Tests/ConvertWorkerTests.cs
git commit -m "feat: fetch the ABS cover in the worker and embed it in the EPUB"
```

---

### Task 5: Documentation

Move the roadmap item to Done, record it in the changelog, and add a one-line convention to ARCHITECTURE. No code.

**Files:**
- Modify: `docs/ROADMAP.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/ARCHITECTURE.md`

**Interfaces:** none.

- [ ] **Step 1: Remove the roadmap backlog bullet**

In `docs/ROADMAP.md`, under `## Conversion / rendering`, delete the entire `- **Cover image.** …` bullet (the block ending with "flag the first page image as the cover instead."), leaving the `- **Conversion speed.**` bullet in place.

- [ ] **Step 2: Add the Done entry**

In `docs/ROADMAP.md`, add as the FIRST bullet under `## Done` (before `- **Background conversion**`):

```markdown
- **Cover image** — the converted EPUB declares a real cover (EPUB3
  `properties="cover-image"` + EPUB2 `<meta name="cover">`), so Apple Books and
  other strict readers show a thumbnail. Prefers the ABS cover art (fetched at
  600px), falling back to the first page when ABS has no usable cover. Metadata
  only — reading still opens on page 1.
```

- [ ] **Step 3: Add the changelog entry**

In `CHANGELOG.md`, add a new section directly below the `Format follows …` line and above `## v0.1.2 — 2026-07-17`:

```markdown
## Unreleased

### Added
- feat: declare a real cover in converted EPUBs (thumbnail in Apple Books etc.),
  preferring the ABS cover art and falling back to the first page.
```

- [ ] **Step 4: Add the ARCHITECTURE convention**

In `docs/ARCHITECTURE.md`, under `## Load-bearing conventions (do not "clean these up")`, add this bullet immediately after the **String-built EPUB XML** bullet:

```markdown
- **The EPUB declares a cover.** `EpubWriter` emits both the EPUB3
  `properties="cover-image"` manifest flag and the EPUB2 `<meta name="cover">`.
  The worker prefers the ABS cover art (`AbsDownloadClient.DownloadCoverAsync`,
  600px) and the converter falls back to flagging the first page when ABS has no
  usable cover. Metadata only — the cover is never a spine entry, so reading opens
  on page 1. It is not part of the cache key (it derives from the item).
```

- [ ] **Step 5: Commit**

```bash
git add docs/ROADMAP.md CHANGELOG.md docs/ARCHITECTURE.md
git commit -m "docs: record the cover-image feature"
```

---

## Verification

After Task 5, from the repo root inside the devcontainer:

- [ ] Run `dotnet test` — all green.
- [ ] (Optional, if epubcheck is available) Convert a comic locally on port 5099 and run epubcheck on the output — expect no new errors and a declared cover. Confirm on a real e-ink reader before merge (near-zero-JS / defensive-CSS convention).

## Notes on decisions (from the spec)

- **Thumbnail only** (no `cover.xhtml` spine page) — avoids double-cover for comics whose page 1 is already the cover.
- **Present + decodable** usability check — no min-dimension guard.
- **Always-try cover fetch** in the worker (one small request on an already-heavy path) rather than threading a `HasCover` flag from the detail metadata — keeps the job record and detail-shape coupling out of it.
- **No cache-key change** — existing cached EPUBs stay coverless until regenerated (↻) or evicted.
