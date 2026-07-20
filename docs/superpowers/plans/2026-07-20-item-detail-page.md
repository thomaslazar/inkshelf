# Item Detail Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-item page at `/item/{id}` showing full metadata, every ebook file (download + per-file convert), genre/tag/narrator filter jump-off, and the read toggle.

**Architecture:** Fetch the expanded ABS item (`?expanded=1`) — it carries full metadata + `libraryFiles[]`. Reuse existing facet links and the convert/read plumbing. Per-file convert threads an optional `file={ino}` through the convert/download pipeline; the cache key is unchanged (keyed by the chosen file's size+mtime), so the **primary** file shares the listing's cache entry. Extract the convert-action markup into a shared partial.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit + WebApplicationFactory render tests. Defensive CSS (no flex `gap`, no `object-fit`); near-zero JS.

## Global Constraints

- **No AOT.** .NET 10, Razor Pages for HTML, minimal APIs for streams/actions.
- **Cache-key format is unchanged** (`{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}[-g].epub`). The `ino` is NEVER in the key — the primary ebook uses its own size+mtime, identical to what the listing writes, so its cache entry is shared. Only non-primary files use `?file={ino}` (keyed by that file's size+mtime).
- **`file={ino}` is optional everywhere** — absent = primary, so all existing convert/download links are unchanged.
- **DTO additions are additive** (trailing optional params); never declare `series` as an array on `AbsMetadata` (only on detail/batch shapes).
- **Regen (↻) stays a plain link** (no `data-warm`) — guarded by `ListingRenderTests`.
- **`LibraryLinks` is the single URL authority** for library/facet links.
- **Never touch `CHANGELOG.md`** (release-only). Record shipped work in ROADMAP "## Done" + ARCHITECTURE.
- **Description shown as `descriptionPlain`** (HTML-stripped), never raw HTML.
- All work on branch `feat/item-detail-page`. `dotnet test` from repo root (inside the devcontainer) must stay green.
- Conventional Commits, subject imperative/lowercase/no period/max ~72 chars; NO `Co-Authored-By:` / "Generated with Claude Code" lines.

---

### Task 1: Expand detail DTOs + `?expanded=1` + per-ino ebook stream

**Files:**
- Modify: `src/Inkshelf/Abs/AbsModels.cs`
- Modify: `src/Inkshelf/Abs/AbsApiClient.cs`
- Test: `tests/Inkshelf.Tests/AbsApiClientTests.cs`

**Interfaces:**
- Produces (DTOs):
  - `AbsItemDetail(AbsDetailMedia? Media, string? LibraryId = null, List<AbsLibraryFile>? LibraryFiles = null)`
  - `AbsDetailMedia(AbsDetailMetadata? Metadata, AbsEbookFile? EbookFile, List<string>? Tags = null, string? CoverPath = null)`
  - `AbsDetailMetadata(... existing ..., string? Subtitle, List<string>? Narrators, List<string>? Genres, string? Publisher, string? PublishedYear, string? Language, string? DescriptionPlain)`
  - `AbsEbookFile(string? EbookFormat, AbsEbookFileMetadata? Metadata, string? Ino = null)`
  - `AbsLibraryFile(string? Ino, string? FileType, AbsLibraryFileMetadata? Metadata)`
  - `AbsLibraryFileMetadata(string? Filename, string? Ext, long Size, long MtimeMs)`
- Produces (client): `GetItemDetailAsync` now requests `?expanded=1`; new `Task<(Stream Content, string ContentType)> GetEbookFileStreamAsync(string itemId, string fileIno, CancellationToken ct = default)` → `/api/items/{id}/ebook/{ino}`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/AbsApiClientTests.cs`:

```csharp
    [Fact]
    public async Task GetItemDetailAsync_requests_expanded_and_parses_files_and_metadata()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryId":"lib1","libraryFiles":[
                {"ino":"11","fileType":"ebook","metadata":{"filename":"a.cbz","ext":".cbz","size":10,"mtimeMs":20}},
                {"ino":"12","fileType":"ebook","metadata":{"filename":"a.pdf","ext":".pdf","size":30,"mtimeMs":40}},
                {"ino":"13","fileType":"image","metadata":{"filename":"cover.jpg","ext":".jpg","size":5,"mtimeMs":6}}],
              "media":{"coverPath":"/c.jpg","tags":["owned","fav"],
                "ebookFile":{"ino":"11","ebookFormat":"cbz","metadata":{"filename":"a.cbz","size":10,"mtimeMs":20}},
                "metadata":{"title":"My Comic","subtitle":"Sub","authors":[{"id":"a1","name":"Auth One"},{"id":"a2","name":"Auth Two"}],
                  "series":[{"id":"s1","name":"S One","sequence":"3"},{"id":"s2","name":"S Two","sequence":"1"}],
                  "narrators":["Nar A","Nar B"],"genres":["Fantasy","Horror"],
                  "publisher":"Pub","publishedYear":"2021","language":"en","descriptionPlain":"Plain desc"}}}"""));
        var d = await Client(h).GetItemDetailAsync("i1");

        Assert.Equal("/api/items/i1", h.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("1", System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query)["expanded"]);
        Assert.Equal("lib1", d.LibraryId);
        Assert.Equal(3, d.LibraryFiles!.Count);
        Assert.Equal("11", d.Media!.EbookFile!.Ino);
        Assert.Equal(2, d.Media!.Metadata!.Authors!.Count);
        Assert.Equal(2, d.Media!.Metadata!.Series!.Count);
        Assert.Equal(new[] { "Nar A", "Nar B" }, d.Media!.Metadata!.Narrators!.ToArray());
        Assert.Equal(new[] { "Fantasy", "Horror" }, d.Media!.Metadata!.Genres!.ToArray());
        Assert.Equal(new[] { "owned", "fav" }, d.Media!.Tags!.ToArray());
        Assert.Equal("Plain desc", d.Media!.Metadata!.DescriptionPlain);
        Assert.Equal("2021", d.Media!.Metadata!.PublishedYear);
    }

    [Fact]
    public async Task GetEbookFileStreamAsync_hits_ebook_ino_path()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 1, 2 }) });
        var (stream, _) = await Client(h).GetEbookFileStreamAsync("i1", "12");
        await using var _s = stream;
        Assert.Equal("/api/items/i1/ebook/12", h.Last!.RequestUri!.AbsolutePath);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AbsApiClientTests`
Expected: FAIL — new DTO members / `GetEbookFileStreamAsync` don't exist; `expanded` query missing (compile + assertion failures).

- [ ] **Step 3: Extend the detail DTOs**

In `src/Inkshelf/Abs/AbsModels.cs`, replace the detail records (`AbsItemDetail` … `AbsEbookFileMetadata`) with:

```csharp
// Item detail (GET /api/items/{id}?expanded=1)
public record AbsItemDetail(
    [property: JsonPropertyName("media")] AbsDetailMedia? Media,
    [property: JsonPropertyName("libraryId")] string? LibraryId = null,
    [property: JsonPropertyName("libraryFiles")] List<AbsLibraryFile>? LibraryFiles = null);
public record AbsDetailMedia(
    [property: JsonPropertyName("metadata")] AbsDetailMetadata? Metadata,
    [property: JsonPropertyName("ebookFile")] AbsEbookFile? EbookFile,
    [property: JsonPropertyName("tags")] List<string>? Tags = null,
    [property: JsonPropertyName("coverPath")] string? CoverPath = null);
public record AbsDetailMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName,
    [property: JsonPropertyName("authors")] List<AbsRef>? Authors = null,
    [property: JsonPropertyName("series")] List<AbsSeriesRef>? Series = null,
    [property: JsonPropertyName("subtitle")] string? Subtitle = null,
    [property: JsonPropertyName("narrators")] List<string>? Narrators = null,
    [property: JsonPropertyName("genres")] List<string>? Genres = null,
    [property: JsonPropertyName("publisher")] string? Publisher = null,
    [property: JsonPropertyName("publishedYear")] string? PublishedYear = null,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("descriptionPlain")] string? DescriptionPlain = null);
public record AbsEbookFile(
    [property: JsonPropertyName("ebookFormat")] string? EbookFormat,
    [property: JsonPropertyName("metadata")] AbsEbookFileMetadata? Metadata,
    [property: JsonPropertyName("ino")] string? Ino = null);
public record AbsEbookFileMetadata(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtimeMs")] long MtimeMs);
public record AbsLibraryFile(
    [property: JsonPropertyName("ino")] string? Ino,
    [property: JsonPropertyName("fileType")] string? FileType,
    [property: JsonPropertyName("metadata")] AbsLibraryFileMetadata? Metadata);
public record AbsLibraryFileMetadata(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("ext")] string? Ext,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtimeMs")] long MtimeMs);
```

(`AbsEbookFile` gaining `Ino` is used to match the primary against `libraryFiles`.)

- [ ] **Step 4: Add `?expanded=1` and the per-ino stream method**

In `src/Inkshelf/Abs/AbsApiClient.cs`, change `GetItemDetailAsync`'s URL:

```csharp
        using var res = await SendAsync(HttpMethod.Get, $"/api/items/{Uri.EscapeDataString(itemId)}?expanded=1", ct);
```

And add, next to `GetEbookStreamAsync`:

```csharp
    // A specific ebook file by its libraryFile ino (multi-format items).
    public async Task<(Stream Content, string ContentType)> GetEbookFileStreamAsync(
        string itemId, string fileIno, CancellationToken ct = default)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/ebook/{Uri.EscapeDataString(fileIno)}";
        var res = await SendAsync(HttpMethod.Get, url, ct); // caller owns the stream
        var type = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (await res.Content.ReadAsStreamAsync(ct), type);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AbsApiClientTests`
Expected: PASS (new tests + existing — the existing detail test asserts `AbsolutePath`, unaffected by the query).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS (`ConvertServiceTests` stub matches all URLs, so `?expanded=1` doesn't affect it).

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Abs/AbsModels.cs src/Inkshelf/Abs/AbsApiClient.cs tests/Inkshelf.Tests/AbsApiClientTests.cs
git commit -m "feat: fetch expanded item detail + per-file ebook stream"
```

---

### Task 2: Per-file convert/download plumbing

Thread an optional `file={ino}` through convert + download. Absent = primary.

**Files:**
- Modify: `src/Inkshelf/Abs/AbsDownloadClient.cs`
- Modify: `src/Inkshelf/Convert/ConvertJob.cs`
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs`
- Modify: `src/Inkshelf/Convert/ConvertService.cs`
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs`
- Modify: `src/Inkshelf/Endpoints/DownloadEndpoints.cs`
- Test: `tests/Inkshelf.Tests/ConvertServiceTests.cs`, `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`

**Interfaces:**
- Consumes: `AbsItemDetail.LibraryFiles`, `AbsLibraryFile`, `GetEbookFileStreamAsync` (Task 1).
- Produces:
  - `AbsDownloadClient.DownloadEbookAsync(string itemId, string accessToken, CancellationToken ct, string? fileIno = null)` → `/api/items/{id}/ebook[/{ino}]`.
  - `ConvertJob(..., string? FileIno = null)`.
  - `ConvertService.KickAsync(id, fresh, target, ct, string? fileIno = null)` and `StatusAsync(id, target, ct, string? fileIno = null)`.
  - `/convert/{id}?file={ino}` and `/download/{id}?file={ino}`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`:

```csharp
    [Fact]
    public async Task DownloadEbook_with_fileIno_hits_ebook_ino_path()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 1 }) });
        await using var s = await Client(stub).DownloadEbookAsync("item9", "TOK", default, fileIno: "77");
        Assert.Equal("/api/items/item9/ebook/77", stub.Last!.RequestUri!.AbsolutePath);
    }
```

Add to `tests/Inkshelf.Tests/ConvertServiceTests.cs`, using the file's existing helpers (`Service(api, cache, queue, store)`, `DetailClient(json)`, `TempDir`, `TokenStoreWith`):

```csharp
    [Fact]
    public async Task KickAsync_with_fileIno_keys_cache_on_that_file()
    {
        // Primary ebook is a PDF (ino 1); a non-primary CBZ is ino 2.
        var detail = """
        {"libraryId":"lib1",
         "libraryFiles":[
            {"ino":"1","fileType":"ebook","metadata":{"filename":"b.pdf","ext":".pdf","size":10,"mtimeMs":20}},
            {"ino":"2","fileType":"ebook","metadata":{"filename":"b.cbz","ext":".cbz","size":99,"mtimeMs":88}}],
         "media":{"ebookFile":{"ino":"1","ebookFormat":"pdf","metadata":{"filename":"b.pdf","size":10,"mtimeMs":20}},
           "metadata":{"title":"T","authorName":"A"}}}
        """;
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var svc = Service(DetailClient(detail), cache, queue, TokenStoreWith("TOK"));
        var target = new RenderTarget(100, 200, 1.0, false);

        // Primary (pdf) → not convertible.
        Assert.Equal(ConvertStatus.None, (await svc.KickAsync("i1", false, target, default)).Status);

        // The non-primary cbz (ino 2) → queued, keyed by size 99 / mtime 88, carrying the ino.
        var r = await svc.KickAsync("i1", false, target, default, fileIno: "2");
        Assert.Equal(ConvertStatus.Queued, r.Status);
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal(cache.PathFor("i1", 99, 88, 100, 200), job!.CachePath);
        Assert.Equal("2", job.FileIno);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AbsDownloadClientTests|FullyQualifiedName~ConvertServiceTests"`
Expected: FAIL — `DownloadEbookAsync` has no `fileIno`; `KickAsync` has no `fileIno` (compile errors).

- [ ] **Step 3: `AbsDownloadClient` + `ConvertJob` + `ConvertWorker`**

`src/Inkshelf/Abs/AbsDownloadClient.cs` — change the signature and URL:

```csharp
    public async Task<Stream> DownloadEbookAsync(string itemId, string accessToken, CancellationToken ct, string? fileIno = null)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/ebook"
            + (string.IsNullOrEmpty(fileIno) ? "" : $"/{Uri.EscapeDataString(fileIno)}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new HttpRequestException($"ebook download failed for {itemId}: {(int)res.StatusCode}");
        }
        return await res.Content.ReadAsStreamAsync(ct);
    }
```

`src/Inkshelf/Convert/ConvertJob.cs` — add the trailing field:

```csharp
public sealed record ConvertJob(
    string ItemId, string AccessToken, string CachePath,
    EbookMeta Meta, RenderTarget Target, string? FileIno = null);
```

`src/Inkshelf/Convert/ConvertWorker.cs` — pass `job.FileIno` to the download (the only call site):

```csharp
                await using (var archive = await download.DownloadEbookAsync(job.ItemId, job.AccessToken, ct, job.FileIno))
```

- [ ] **Step 4: `ConvertService` — resolve a specific file**

In `src/Inkshelf/Convert/ConvertService.cs`, add `fileIno` to `KickAsync`/`StatusAsync`/`ResolveAsync` and select the file. Replace the three methods' relevant parts:

```csharp
    public async Task<KickResult> KickAsync(string id, bool fresh, RenderTarget target,
        CancellationToken ct, string? fileIno = null)
    {
        var r = await ResolveAsync(id, target, fileIno, ct);
        if (r is null) return new KickResult(ConvertStatus.None);
        var (path, meta, downloadName) = r.Value;

        if (fresh) _cache.RemoveForItem(id);
        if (System.IO.File.Exists(path)) { _cache.Touch(path); return new KickResult(ConvertStatus.Done, path, downloadName); }

        var tokens = _tokens.Read();
        if (tokens is null) return new KickResult(ConvertStatus.None);
        var status = _queue.Enqueue(new ConvertJob(id, tokens.Access, path, meta, target, fileIno));
        return new KickResult(status);
    }

    public async Task<KickResult> StatusAsync(string id, RenderTarget target,
        CancellationToken ct, string? fileIno = null)
    {
        var r = await ResolveAsync(id, target, fileIno, ct);
        if (r is null) return new KickResult(ConvertStatus.None);
        var (path, _, downloadName) = r.Value;
        var status = _queue.Status(path);
        return status == ConvertStatus.Done
            ? new KickResult(ConvertStatus.Done, path, downloadName)
            : new KickResult(status);
    }

    private async Task<(string Path, EbookMeta Meta, string DownloadName)?> ResolveAsync(
        string id, RenderTarget target, string? fileIno, CancellationToken ct)
    {
        AbsItemDetail detail;
        try { detail = await _api.GetItemDetailAsync(id, ct); }
        catch (HttpRequestException) { return null; }

        // Pick the file to convert: a specific libraryFile by ino, else the primary.
        string? fmt; long size; long mtime;
        if (!string.IsNullOrEmpty(fileIno))
        {
            var lf = detail.LibraryFiles?.FirstOrDefault(f => f.Ino == fileIno && f.FileType == "ebook");
            if (lf?.Metadata is null) return null;
            fmt = lf.Metadata.Ext?.TrimStart('.').ToLowerInvariant();
            size = lf.Metadata.Size; mtime = lf.Metadata.MtimeMs;
        }
        else
        {
            var ef = detail.Media?.EbookFile;
            if (ef?.Metadata is null) return null;
            fmt = ef.EbookFormat;
            size = ef.Metadata.Size; mtime = ef.Metadata.MtimeMs;
        }
        if (fmt != "cbz" && fmt != "cbr") return null;

        var md = detail.Media?.Metadata;
        var title = md?.Title ?? "Untitled";
        var author = md?.AuthorName is { Length: > 0 } an ? an
            : (md?.Authors is { Count: > 0 } ? md.Authors[0].Name : "Unknown");
        var seq = md?.Series is { Count: > 0 } ? md.Series[0].Sequence : null;
        var seriesName = md?.Series is { Count: > 0 } ? md.Series[0].Name : md?.SeriesName;

        var path = _cache.PathFor(id, size, mtime, target.MaxW, target.MaxH, target.Grayscale);
        var meta = new EbookMeta(title, author, seriesName, seq, id);
        var downloadName = Sanitize($"{author} - {title}") + ".epub";
        return (path, meta, downloadName);
    }
```

(Add `using System.Linq;` if not present.)

- [ ] **Step 5: Endpoints — accept `file`**

`src/Inkshelf/Endpoints/ConvertEndpoints.cs` — add the `file` param and pass it:

```csharp
        app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm,
            string? status, string? file, string? @return, HttpContext httpContext, ConvertService convert, CancellationToken ct) =>
        {
            var ds = DeviceSettings.Read(httpContext.Request);
            var t = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"], ds.Retina, ds.Grayscale);

            if (status is "1")
            {
                var s = await convert.StatusAsync(id, t, ct, file);
                return s.Status == ConvertStatus.None ? Results.NotFound() : Results.Text(Text(s.Status));
            }

            var result = await convert.KickAsync(id, fresh is "1" or "true", t, ct, file);
            if (result.Status == ConvertStatus.None) return Results.NotFound();

            if (warm is "1")
                return result.Status == ConvertStatus.Done
                    ? Results.Text("done")
                    : Results.Text(Text(result.Status), statusCode: StatusCodes.Status202Accepted);

            return result.Status == ConvertStatus.Done
                ? Results.File(result.FilePath!, "application/epub+zip", fileDownloadName: result.DownloadName)
                : Results.Redirect(LocalReturn(@return));
        });
```

`src/Inkshelf/Endpoints/DownloadEndpoints.cs` — add the `file` param:

```csharp
        app.MapGet("/download/{id}", async (string id, string? file, AbsApiClient api, CancellationToken ct) =>
        {
            try
            {
                var detail = await api.GetItemDetailAsync(id, ct);
                if (!string.IsNullOrEmpty(file))
                {
                    var lf = detail.LibraryFiles?.FirstOrDefault(f => f.Ino == file && f.FileType == "ebook");
                    var fname = lf?.Metadata?.Filename;
                    if (string.IsNullOrEmpty(fname)) return Results.NotFound();
                    var (fs, ftype) = await api.GetEbookFileStreamAsync(id, file, ct);
                    return Results.File(fs, ftype, fileDownloadName: fname);
                }
                var name = detail.Media?.EbookFile?.Metadata?.Filename;
                if (string.IsNullOrEmpty(name)) return Results.NotFound();
                var (stream, contentType) = await api.GetEbookStreamAsync(id, ct);
                return Results.File(stream, contentType, fileDownloadName: name);
            }
            catch (HttpRequestException) { return Results.NotFound(); }
        });
```

(Add `using System.Linq;` if needed.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AbsDownloadClientTests|FullyQualifiedName~ConvertServiceTests|FullyQualifiedName~ConvertWorkerTests"`
Expected: PASS — new tests + existing (existing convert paths pass `fileIno = null`).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Abs/AbsDownloadClient.cs src/Inkshelf/Convert/ConvertJob.cs src/Inkshelf/Convert/ConvertWorker.cs src/Inkshelf/Convert/ConvertService.cs src/Inkshelf/Endpoints/ConvertEndpoints.cs src/Inkshelf/Endpoints/DownloadEndpoints.cs tests/Inkshelf.Tests/
git commit -m "feat: thread optional file ino through convert and download"
```

---

### Task 3: Shared `_ConvertAction` partial + `_ItemRow` title/cover link

Extract the convert-action markup so the detail page can reuse it; make the row title + cover link to the detail page.

**Files:**
- Create: `src/Inkshelf/Pages/Support/ConvertActionModel.cs`
- Create: `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Produces: `ConvertActionModel(string Id, string? FileIno, ConvertRowState State, string ReturnUrl)` and the `_ConvertAction` partial rendering the convert `<span>` (states + plain regen). Base convert href: `/convert/{Id}?[file={ino}&]return={enc}`; regen href adds `fresh=1`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs` (uses the existing harness):

```csharp
    [Fact]
    public async Task Row_title_and_cover_link_to_the_item_detail_page()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();
        Assert.Contains($"href=\"/item/{ItemId}\"", html);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ListingRenderTests.Row_title_and_cover_link"`
Expected: FAIL — no `/item/{id}` link in the row yet.

- [ ] **Step 3: Create the model + partial**

Create `src/Inkshelf/Pages/Support/ConvertActionModel.cs`:

```csharp
namespace Inkshelf.Pages;

// Inputs for the shared _ConvertAction partial: which item (and optional specific
// ebook file by ino), the precomputed convert state, and where a no-JS convert
// navigation returns to. FileIno = null → the primary ebook (the listing case).
public record ConvertActionModel(string Id, string? FileIno, ConvertRowState State, string ReturnUrl);
```

Create `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`:

```cshtml
@model Inkshelf.Pages.ConvertActionModel
@{
    var ret = Uri.EscapeDataString(Model.ReturnUrl);
    var fileQ = string.IsNullOrEmpty(Model.FileIno) ? "" : $"file={Uri.EscapeDataString(Model.FileIno)}&";
    var baseHref = $"/convert/{Model.Id}?{fileQ}return={ret}";
    var freshHref = $"/convert/{Model.Id}?{fileQ}fresh=1&return={ret}";
}
<span class="convert">
    @switch (Model.State)
    {
        case ConvertRowState.Cached:
            <a href="@baseHref" title="Already converted — downloads right away">EPUB &#10003;</a>
            break;
        case ConvertRowState.Converting:
            <a href="@baseHref" data-warm data-poll>Converting&#8230;</a>
            break;
        case ConvertRowState.Failed:
            <a href="@baseHref" data-warm>Convert (retry)</a>
            break;
        default:
            <a href="@baseHref" data-warm>Convert</a>
            break;
    }
    @* Plain link (no data-warm): the ↻ glyph must not become status text. *@
    <a class="regen" href="@freshHref" title="Regenerate">&#8635;</a>
</span>
```

- [ ] **Step 4: Rewrite `_ItemRow` to use the partial + link title/cover**

In `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`:

(a) Wrap the cover in a link — replace the cover `@if/else` block so both branches are inside an anchor:

```cshtml
    <a class="cover-link" href="/item/@item.Id">
    @if (!string.IsNullOrEmpty(item.Media?.CoverPath))
    {
        <span class="cover"><img src="/cover/@item.Id?w=120" alt="" /></span>
    }
    else
    {
        <span class="cover placeholder">@(string.IsNullOrEmpty(m?.Title) ? "?" : m!.Title!.Substring(0, 1).ToUpperInvariant())</span>
    }
    </a>
```

(b) Link the title — replace `<strong>@(m?.Title ?? "(untitled)")</strong><br />` with:

```cshtml
        <a href="/item/@item.Id"><strong>@(m?.Title ?? "(untitled)")</strong></a><br />
```

(c) Replace the convert `<span class="convert">…</span>` block (the `@if (Model.State != ConvertRowState.NotConvertible)` body) with a call to the partial:

```cshtml
            @if (Model.State != ConvertRowState.NotConvertible)
            {
                <partial name="_ConvertAction" model="new Inkshelf.Pages.ConvertActionModel(item.Id, null, Model.State, Model.ReturnUrl)" />
            }
```

The rendered convert markup is unchanged (FileIno=null → `baseHref` = `/convert/{id}?return={enc}`), so the existing regen/cached/converting assertions still hold.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ListingRenderTests`
Expected: PASS — the new link test plus ALL existing listing tests (regen stays plain, cached/converting markup identical, read toggle intact).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Pages/Support/ConvertActionModel.cs src/Inkshelf/Pages/Shared/_ConvertAction.cshtml src/Inkshelf/Pages/Shared/_ItemRow.cshtml tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "refactor: extract _ConvertAction partial; link row title/cover to detail"
```

---

### Task 4: Genre/tag/narrator facet labels in `LibraryModel`

These facets filter by name; teach the label to show them.

**Files:**
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:** none new (behavioral).

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`:

```csharp
    [Fact]
    public async Task Filter_by_genre_shows_type_and_name()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory);
        // base64("Fantasy") = "RmFudGFzeQ=="
        req.RequestUri = new Uri($"/library/{LibId}?filter=genres.RmFudGFzeQ==", UriKind.Relative);
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("Filtered by <strong>Genre: Fantasy</strong>", html);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ListingRenderTests.Filter_by_genre"`
Expected: FAIL — genre facet currently labels as "Genres" (raw `Humanize`) with no resolved name.

- [ ] **Step 3: Implement the label**

In `src/Inkshelf/Pages/Library.cshtml.cs`, in `ResolveFilterAsync`, extend the `if (!string.IsNullOrEmpty(Filter))` branch so name-valued facets set `FilterName` directly. Replace that branch's body with:

```csharp
        if (!string.IsNullOrEmpty(Filter))
        {
            if (AbsFilter.Decode(Filter) is { } d)
            {
                _filterGroup = d.Group; _filterValue = d.Value;
                FilterType = Humanize(d.Group);
                // genres/tags/narrators filter by NAME — the decoded value IS the label.
                if (d.Group is "genres" or "tags" or "narrators") FilterName = d.Value;
            }
            else { FilterType = "Filter"; }
            return Filter;
        }
```

And extend `Humanize`:

```csharp
    private static string Humanize(string group) => group switch
    {
        "authors" => "Author",
        "series" => "Series",
        "genres" => "Genre",
        "tags" => "Tag",
        "narrators" => "Narrator",
        _ => group.Length > 0 ? char.ToUpperInvariant(group[0]) + group[1..] : group
    };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ListingRenderTests`
Expected: PASS (new genre test + existing author/series label tests unaffected).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Pages/Library.cshtml.cs tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "feat: label genre/tag/narrator facet filters"
```

---

### Task 5: `/item/{id}` detail page + `ItemModel` + `ResolveFor`

**Files:**
- Modify: `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs`
- Create: `src/Inkshelf/Pages/Item.cshtml`
- Create: `src/Inkshelf/Pages/Item.cshtml.cs`
- Test: `tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs`, `tests/Inkshelf.Tests/ItemRenderTests.cs`

**Interfaces:**
- Consumes: expanded `AbsItemDetail` (Task 1), `ConvertActionModel`/`_ConvertAction` (Task 3), `LibraryLinks`, `_ReadToggle`-style read form.
- Produces: `ConvertRowStateResolver.ResolveFor(string itemId, long size, long mtimeMs, string? fmt, RenderTarget target, EpubCache cache, ConvertQueue queue)`; the `/item/{id}` route.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs`:

```csharp
    [Fact]
    public void ResolveFor_returns_cached_when_file_present()
    {
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        File.WriteAllText(cache.PathFor("i1", 99, 88, 800, 1000), "e");
        var r = ConvertRowStateResolver.ResolveFor("i1", 99, 88, "cbz", new RenderTarget(800, 1000, 1.0, false), cache, new ConvertQueue());
        Assert.Equal(ConvertRowState.Cached, r);
    }

    [Fact]
    public void ResolveFor_non_comic_is_not_convertible()
    {
        var r = ConvertRowStateResolver.ResolveFor("i1", 1, 2, "pdf", new RenderTarget(800, 1000, 1.0, false), new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.NotConvertible, r);
    }
```

Create `tests/Inkshelf.Tests/ItemRenderTests.cs` (mirror `ConvertedRenderTests`' harness — TempDir, CreateFactory dropping ConvertWorker, protected session cookie + `scr` cookie). Stub answers `/api/items/{id}` (expanded detail) and `/api/me`:

```csharp
using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

public class ItemRenderTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "item-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string ItemId = "item1";
    private const string LibId = "lib1";
    private const long PSize = 12345, PMtime = 67890; // primary cbz
    private const int W = 375, H = 812;

    private static string DetailJson() => $$"""
    {"libraryId":"{{LibId}}",
     "libraryFiles":[
        {"ino":"1","fileType":"ebook","metadata":{"filename":"My Comic.cbz","ext":".cbz","size":{{PSize}},"mtimeMs":{{PMtime}}}},
        {"ino":"2","fileType":"ebook","metadata":{"filename":"My Comic.pdf","ext":".pdf","size":50,"mtimeMs":60}}],
     "media":{"coverPath":"/c.jpg","tags":["owned"],
        "ebookFile":{"ino":"1","ebookFormat":"cbz","metadata":{"filename":"My Comic.cbz","size":{{PSize}},"mtimeMs":{{PMtime}}}},
        "metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"},{"id":"a2","name":"Author Two"}],
          "series":[{"id":"s1","name":"The Sandman","sequence":"3"}],
          "narrators":["Nar A"],"genres":["Fantasy"],"descriptionPlain":"A plain description."}}}
    """;

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/items/{ItemId}") return StubHandler.Json(DetailJson());
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static WebApplicationFactory<Program> CreateFactory(StubHandler stub, string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    private static HttpRequestMessage Request(WebApplicationFactory<Program> factory, string url)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; scr={W}x{H}x1");
        return req;
    }

    [Fact]
    public async Task Shows_metadata_files_and_cached_primary()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, PSize, PMtime, W, H), "epub"); // primary cbz already converted

        var html = await (await client.SendAsync(Request(factory, $"/item/{ItemId}"))).Content.ReadAsStringAsync();

        Assert.Contains("My Comic", html);
        Assert.Contains("A plain description.", html);
        Assert.Contains(">Author One<", html);
        Assert.Contains(">Author Two<", html);                       // multiple authors
        Assert.Contains($"/library/{LibId}?filter=", html);          // facet links (author/series/genre)
        Assert.Contains("My Comic.pdf", html);                       // every ebook file listed
        Assert.Contains($"/download/{ItemId}?file=2", html);         // non-primary download by ino
        Assert.Contains($"/download/{ItemId}\"", html);              // primary download (no file=)
        Assert.Contains("EPUB &#10003;", html);                      // primary cbz cached (shared key)
        Assert.Contains($"action=\"/read/{ItemId}\"", html);         // read toggle
    }

    [Fact]
    public async Task Primary_convert_link_has_no_file_param()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // No cache seeded → primary cbz shows plain "Convert" (no file= param).
        var html = await (await client.SendAsync(Request(factory, $"/item/{ItemId}"))).Content.ReadAsStringAsync();
        Assert.Contains($"/convert/{ItemId}?return=", html);         // primary → no file=
        Assert.DoesNotContain($"/convert/{ItemId}?file=1", html);    // primary is NOT keyed by its ino
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ItemRenderTests|FullyQualifiedName~ConvertRowStateResolverTests.ResolveFor"`
Expected: FAIL — `ResolveFor` missing (compile); no `/item/{id}` route (404 → assertion failures).

- [ ] **Step 3: Add `ResolveFor` and delegate `Resolve`**

In `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs`, add `ResolveFor` and make `Resolve` call it:

```csharp
    public static ConvertRowState Resolve(AbsItem item, AbsBatchMedia? media,
        RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        var fmt = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat ?? media?.EbookFile?.EbookFormat;
        var efm = media?.EbookFile?.Metadata;
        if (efm is null) return ConvertRowState.NotConvertible; // can't key the cache
        return ResolveFor(item.Id, efm.Size, efm.MtimeMs, fmt, target, cache, queue);
    }

    // Lower-level: state for one specific (itemId, file size+mtime, format).
    public static ConvertRowState ResolveFor(string itemId, long size, long mtimeMs,
        string? fmt, RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        if (fmt != "cbz" && fmt != "cbr") return ConvertRowState.NotConvertible;
        var path = cache.PathFor(itemId, size, mtimeMs, target.MaxW, target.MaxH, target.Grayscale);
        return queue.Status(path) switch
        {
            ConvertStatus.Done => ConvertRowState.Cached,
            ConvertStatus.Queued or ConvertStatus.Running => ConvertRowState.Converting,
            ConvertStatus.Failed => ConvertRowState.Failed,
            _ => ConvertRowState.Convert,
        };
    }
```

This preserves `Resolve`'s original semantics: no ebook-file metadata → `NotConvertible`; otherwise `ResolveFor` applies the same cbz/cbr check + cache/queue lookup as before, so `ListingRenderTests` stays green.

- [ ] **Step 4: Create `ItemModel`**

Create `src/Inkshelf/Pages/Item.cshtml.cs`:

```csharp
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class ItemModel : PageModel
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    public ItemModel(AbsApiClient api, EpubCache cache, ConvertQueue queue)
    { _api = api; _cache = cache; _queue = queue; }

    [FromRoute] public string Id { get; set; } = "";

    // One downloadable ebook file: its display name/format, the download href,
    // and (for cbz/cbr) the convert action to render via _ConvertAction.
    public record FileRow(string Name, string Format, string DownloadHref, ConvertActionModel? Convert);

    public string LibraryId { get; private set; } = "";
    public AbsDetailMetadata? Meta { get; private set; }
    public List<string> Tags { get; private set; } = new();
    public bool HasCover { get; private set; }
    public bool Read { get; private set; }
    public List<FileRow> Files { get; private set; } = new();
    public LibraryLinks Links => new(LibraryId, null, null, null, null, false);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        AbsItemDetail detail;
        try { detail = await _api.GetItemDetailAsync(Id, ct); }
        catch (HttpRequestException) { return NotFound(); }
        if (detail.Media is null) return NotFound();

        LibraryId = detail.LibraryId ?? "";
        Meta = detail.Media.Metadata;
        Tags = detail.Media.Tags ?? new();
        HasCover = !string.IsNullOrEmpty(detail.Media.CoverPath);

        var ds = Auth.DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], ds.Retina, ds.Grayscale);

        try { Read = (await _api.GetFinishedItemIdsAsync(ct)).Contains(Id); }
        catch (HttpRequestException) { Read = false; }

        var primaryIno = detail.Media.EbookFile?.Ino;
        foreach (var f in detail.LibraryFiles ?? new())
        {
            if (f.FileType != "ebook" || f.Metadata is null) continue;
            var isPrimary = f.Ino is not null && f.Ino == primaryIno;
            var fmt = f.Metadata.Ext?.TrimStart('.').ToLowerInvariant() ?? "";
            var name = f.Metadata.Filename ?? f.Ino ?? "file";
            var dl = isPrimary ? $"/download/{Id}" : $"/download/{Id}?file={Uri.EscapeDataString(f.Ino!)}";

            ConvertActionModel? convert = null;
            if (fmt is "cbz" or "cbr")
            {
                var state = ConvertRowStateResolver.ResolveFor(
                    Id, f.Metadata.Size, f.Metadata.MtimeMs, fmt, target, _cache, _queue);
                convert = new ConvertActionModel(Id, isPrimary ? null : f.Ino, state, $"/item/{Id}");
            }
            Files.Add(new FileRow(name, fmt.ToUpperInvariant(), dl, convert));
        }
        return Page();
    }
}
```

- [ ] **Step 5: Create `Item.cshtml`**

Create `src/Inkshelf/Pages/Item.cshtml`:

```cshtml
@page "{id}"
@model Inkshelf.Pages.ItemModel
@{
    Response.Headers["Cache-Control"] = "no-store";
    var m = Model.Meta;
}
<div class="page-head" id="top">
    <h1 class="page-title">
        <img src="~/img/icon-black.png" alt="" class="title-icon" />
        <a href="/?all=1">Libraries</a> <span class="crumb-sep">›</span> @(m?.Title ?? "Item")
    </h1>
    <a class="settings-link" href="/settings" title="Settings"><img src="~/img/gear-black.png" alt="Settings" class="settings-icon" width="24" height="24" /></a>
</div>

<div class="detail">
    @if (Model.HasCover)
    {
        <span class="detail-cover"><img src="/cover/@Model.Id?w=240" alt="" /></span>
    }
    <div class="detail-meta">
        <h2>@(m?.Title ?? "(untitled)")</h2>
        @if (!string.IsNullOrWhiteSpace(m?.Subtitle)) { <p class="subtitle">@m!.Subtitle</p> }

        @if (m?.Authors is { Count: > 0 } authors)
        {
            <p>Authors:
            @for (var i = 0; i < authors.Count; i++)
            {
                if (i > 0) { <text>, </text> }
                <a href="@Model.Links.FilterHref("authors", authors[i].Id)">@authors[i].Name</a>
            }
            </p>
        }
        @if (m?.Series is { Count: > 0 } series)
        {
            <p>Series:
            @for (var i = 0; i < series.Count; i++)
            {
                if (i > 0) { <text>, </text> }
                <a href="@Model.Links.FilterHref("series", series[i].Id)">@series[i].Name@(string.IsNullOrEmpty(series[i].Sequence) ? "" : $" #{series[i].Sequence}")</a>
            }
            </p>
        }
        @if (m?.Narrators is { Count: > 0 } narrators)
        {
            <p>Narrators:
            @for (var i = 0; i < narrators.Count; i++)
            {
                if (i > 0) { <text>, </text> }
                <a href="@Model.Links.FilterHref("narrators", narrators[i])">@narrators[i]</a>
            }
            </p>
        }
        @{ var pubBits = new List<string>(); if (!string.IsNullOrWhiteSpace(m?.Publisher)) pubBits.Add(m!.Publisher!); if (!string.IsNullOrWhiteSpace(m?.PublishedYear)) pubBits.Add(m!.PublishedYear!); if (!string.IsNullOrWhiteSpace(m?.Language)) pubBits.Add(m!.Language!); }
        @if (pubBits.Count > 0) { <p>@string.Join(" · ", pubBits)</p> }
        @if (m?.Genres is { Count: > 0 } genres)
        {
            <p>Genres:
            @foreach (var g in genres) { <a href="@Model.Links.FilterHref("genres", g)">@g</a> <text> </text> }
            </p>
        }
        @if (Model.Tags is { Count: > 0 } tags)
        {
            <p>Tags:
            @foreach (var tg in tags) { <a href="@Model.Links.FilterHref("tags", tg)">@tg</a> <text> </text> }
            </p>
        }
    </div>
</div>

<form class="read-form" method="post" action="/read/@Model.Id">
    @Html.AntiForgeryToken()
    <input type="hidden" name="read" value="@(Model.Read ? "0" : "1")" />
    <input type="hidden" name="return" value="/item/@Model.Id" />
    @if (Model.Read)
    {
        <button type="submit" class="read-btn" title="Mark as unread">&#10003; Read</button>
    }
    else
    {
        <button type="submit" class="read-btn" title="Mark as read">Mark read</button>
    }
</form>

@if (!string.IsNullOrWhiteSpace(m?.DescriptionPlain))
{
    <p class="description">@m!.DescriptionPlain</p>
}

<h2>Files</h2>
@if (Model.Files.Count == 0)
{
    <p>No downloadable files.</p>
}
else
{
    foreach (var f in Model.Files)
    {
        <div class="file-row">
            <span class="file-name">@f.Name</span>
            <span class="file-format">@f.Format</span>
            <a href="@f.DownloadHref">Download</a>
            @if (f.Convert is not null)
            {
                <partial name="_ConvertAction" model="f.Convert" />
            }
        </div>
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ItemRenderTests|FullyQualifiedName~ConvertRowStateResolverTests"`
Expected: PASS (metadata/files/cached-primary render; primary convert has no `file=`; resolver `ResolveFor` cases; existing resolver tests still green).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs src/Inkshelf/Pages/Item.cshtml src/Inkshelf/Pages/Item.cshtml.cs tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs tests/Inkshelf.Tests/ItemRenderTests.cs
git commit -m "feat: add the item detail page"
```

---

### Task 6: Documentation

Record the feature in ROADMAP and ARCHITECTURE. **Do not touch `CHANGELOG.md`.**

**Files:**
- Modify: `docs/ROADMAP.md`
- Modify: `docs/ARCHITECTURE.md`

**Interfaces:** none.

- [ ] **Step 1: Move the roadmap item to Done**

In `docs/ROADMAP.md`, under `## Browsing & reading`, delete the entire `- **Item detail page.** …` bullet (through its trailing `*Note:*` sentence). Add as the FIRST bullet under `## Done`:

```markdown
- **Item detail page** — a per-item page at `/item/{id}` (reached by the row
  title/cover) showing the full metadata (larger cover, multiple authors/series/
  narrators as filter links, genres, tags, publisher/year, plain description),
  every ebook file with its own download, and — for cbz/cbr files — the Convert
  action. Convert is per-file: the primary uses the item's existing cache entry
  (no `file=`), non-primary files use `/convert/{id}?file={ino}`; the cache key is
  unchanged. Also carries the read/unread toggle. Genre/tag/narrator links jump to
  a filtered library listing.
```

- [ ] **Step 2: Update ARCHITECTURE**

In `docs/ARCHITECTURE.md`:

(a) `Pages/` map line — add `Item`:
`Pages/  Razor Pages: Index, Login, Library, Converted, Item, Settings (+ models); Shared/ partials.`

(b) `Support/` map line — add `ConvertActionModel`:
`Support/  Non-page helper types: LibraryLinks, ItemRowModel, Pager, SortLinks, ConvertRowStateResolver, ConvertActionModel.`

(c) Add this bullet to `## Load-bearing conventions`, after the convert-row-state bullet:

```markdown
- **Convert is per-file, but the cache key never carries the file ino.** The
  detail page can convert any cbz/cbr in an item via `/convert/{id}?file={ino}`,
  but the key stays `{itemId}-{size}-{mtimeMs}-…` using the chosen file's
  size+mtime. The **primary** ebook uses no `file=` and its own size+mtime, so its
  cache entry is identical to the one the listing/converted view write — the badge
  agrees across pages. `_ConvertAction` renders the convert `<span>` (states +
  plain regen) for both the row and the detail formats list.
```

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md docs/ARCHITECTURE.md
git commit -m "docs: record the item detail page"
```

---

## Verification

After Task 6, from the repo root inside the devcontainer:

- [ ] Run `dotnet test` — all green.
- [ ] (Manual, optional) Run the dev server on 5099, open a listing, tap a title → `/item/{id}`; confirm metadata, multiple authors/series, genre/tag links jump to a filtered listing, every ebook file has a download, a cbz shows Convert (and "EPUB ✓" if already converted from the listing), and the read toggle works. Real-device check before merge (defensive-CSS convention).

## Notes on decisions (from the spec)

- **Per-file convert; primary shares the listing's cache entry** — the ino is never in the cache key.
- **`_ConvertAction` partial** keeps the regen-stays-plain rule in one place.
- **Genre/tag/narrator** filter by name (their value is the label); authors/series by id — all through the existing `?filter=` path.
- **`/converted` view unchanged** — still one row per item.
- Description is `descriptionPlain` (no raw HTML).
