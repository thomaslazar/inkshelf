# Sorting + ebook delivery — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add item sorting, a primary-ebook download, and on-the-fly CBZ/CBR→EPUB conversion (metadata-embedded, disk-cached, force-regenerable) — one PR.

**Architecture:** Sorting is an ABS query param surfaced as cycling sort-links that compose with filters + paging. Download proxies the ABS ebook file. Conversion reads the archive with SharpCompress, sizes/transcodes images with ImageSharp, writes a fixed-layout EPUB with `System.IO.Compression`, and caches it on disk at a volume-mounted path.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 10), `SharpCompress`, `SixLabors.ImageSharp`, `System.IO.Compression`. Near-zero client JS.

## Global Constraints

- Near-zero JavaScript; Tolino = Chrome-30/ES5 (`docs/tolino-browser.md`) — margins not `gap`, no `object-fit`, etc.
- Ebook links render only when `media.ebookFormat` is set. Download = any format; Convert = cbz/cbr only.
- Converted EPUB download name: `<Author> - <Title>.epub` (sanitized). Plain download keeps the ABS filename.
- Cache key: `{itemId}-{size}-{mtimeMs}.epub`. `?fresh=1` deletes `{itemId}-*.epub` then regenerates.
- Cache path from config `CachePath` (default `<ContentRoot>/.cache/epub`); persisted via a compose volume.
- Fixed-layout (pre-paginated) EPUB; WebP pages → JPEG, others pass through.
- Endpoints return 404 (not 500) when an item has no ebook.
- Conventional Commits; no attribution trailers.

## ABS contract (verified @ v2.35.1)

- Item detail `GET /api/items/{id}` → `media.metadata.{title, authorName, seriesName, authors:[{id,name}], series:[{id,name,sequence}]}` and `media.ebookFile.{ebookFormat, metadata:{filename,size,mtimeMs}}`.
- Ebook bytes: `GET /api/items/{id}/ebook` (primary ebook; no special permission).
- Items list sort: `?sort=<key>&desc=0|1` — keys `media.metadata.title`, `media.metadata.authorNameLF`, `addedAt`, `sequence`.

---

## Task 1: Dependencies, cache config, deploy wiring

**Files:**
- Modify: `src/Inkshelf/Inkshelf.csproj`, `src/Inkshelf/Program.cs`, `.gitignore`, `docker-compose.example.yml`, `README.md`

**Interfaces:**
- Produces: `SharpCompress` + `SixLabors.ImageSharp` referenced; `CachePath` config resolved + directory created at startup; cache volume documented.

- [ ] **Step 1: Add the NuGet packages**

```bash
cd /workspaces/inkshelf
dotnet add src/Inkshelf package SharpCompress
dotnet add src/Inkshelf package SixLabors.ImageSharp
```

- [ ] **Step 2: Resolve + create the cache path in `Program.cs`**

Near the Data Protection setup:

```csharp
var cachePath = builder.Configuration["CachePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, ".cache", "epub");
Directory.CreateDirectory(cachePath);
```
(Registration of `EpubCache`/`EpubConverter` comes in Tasks 5-7.)

- [ ] **Step 3: gitignore the cache**

Append to `.gitignore`:
```
# EPUB conversion cache (runtime)
.cache/
```

- [ ] **Step 4: Cache volume in the compose example**

In `docker-compose.example.yml`, add `CachePath: "/cache"` to `environment`, a
`inkshelf-cache:/cache` volume mount, and the `inkshelf-cache:` named volume
(mirroring the existing `/keys` volume).

- [ ] **Step 5: README**

Add a row to the config table: `CachePath` — optional; where converted EPUBs are
cached; default `<ContentRoot>/.cache/epub`; mount a volume to persist across
restarts. Note conversions are cached and survive restarts when the volume is mounted.

- [ ] **Step 6: Build + commit**

```bash
dotnet build src/Inkshelf   # restores the new packages
git add src/Inkshelf/Inkshelf.csproj src/Inkshelf/Program.cs .gitignore docker-compose.example.yml README.md
git commit -m "chore: add SharpCompress + ImageSharp, cache path config and volume"
```

---

## Task 2: Model + AbsClient (detail, ebook stream, sort param)

**Files:**
- Modify: `src/Inkshelf/Abs/AbsModels.cs`, `src/Inkshelf/Abs/AbsClient.cs`
- Modify: `tests/Inkshelf.Tests/AbsClientTests.cs`

**Interfaces:**
- Produces:
  - `AbsMedia` gains `EbookFormat` (`media.ebookFormat`, present in the listing).
  - `AbsItemDetail` DTOs (title/author/series + ebookFile size/mtime/filename/format).
  - `AbsClient.GetItemDetailAsync(token, id, ct)` → `AbsItemDetail`.
  - `AbsClient.GetEbookStreamAsync(token, id, ct)` → `(Stream Content, string ContentType)` (live).
  - `GetItemsAsync(token, libId, page, limit, filter, sort, desc, ct)` — appends `sort`/`desc` when set.

- [ ] **Step 1: Write failing tests (append to AbsClientTests)**

```csharp
[Fact]
public async Task GetItemsAsync_appends_sort_and_desc()
{
    var h = new StubHandler(_ => StubHandler.Json("""{"results":[],"total":0,"limit":10,"page":0}"""));
    await Client(h).GetItemsAsync("acc", "lib1", 0, 10, filter: null, sort: "addedAt", desc: true);
    var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
    Assert.Equal("addedAt", q["sort"]);
    Assert.Equal("1", q["desc"]);
}

[Fact]
public async Task GetItemDetailAsync_parses_ebook_and_metadata()
{
    var h = new StubHandler(_ => StubHandler.Json(
        """{"media":{"metadata":{"title":"Vol 1","authorName":"A Artist","authors":[{"id":"a1","name":"A Artist"}],"series":[{"id":"s1","name":"Saga","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"Vol1.cbz","size":123,"mtimeMs":999}}}}"""));
    var d = await Client(h).GetItemDetailAsync("acc", "i1");
    Assert.Equal("Vol 1", d.Media!.Metadata!.Title);
    Assert.Equal("cbz", d.Media!.EbookFile!.EbookFormat);
    Assert.Equal(123, d.Media!.EbookFile!.Metadata!.Size);
    Assert.Equal(999, d.Media!.EbookFile!.Metadata!.MtimeMs);
    Assert.Equal("Vol1.cbz", d.Media!.EbookFile!.Metadata!.Filename);
    Assert.Equal("/api/items/i1", h.Last!.RequestUri!.AbsolutePath);
}
```

- [ ] **Step 2: Run, verify fail** — `dotnet test tests/Inkshelf.Tests --filter AbsClientTests`

- [ ] **Step 3: Extend `AbsModels.cs`**

```csharp
public record AbsMedia(
    [property: JsonPropertyName("metadata")] AbsMetadata? Metadata,
    [property: JsonPropertyName("coverPath")] string? CoverPath = null,
    [property: JsonPropertyName("ebookFormat")] string? EbookFormat = null);

// Item detail (GET /api/items/{id})
public record AbsItemDetail(
    [property: JsonPropertyName("media")] AbsDetailMedia? Media);
public record AbsDetailMedia(
    [property: JsonPropertyName("metadata")] AbsDetailMetadata? Metadata,
    [property: JsonPropertyName("ebookFile")] AbsEbookFile? EbookFile);
public record AbsDetailMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName,
    [property: JsonPropertyName("authors")] List<AbsRef>? Authors = null,
    [property: JsonPropertyName("series")] List<AbsSeriesRef>? Series = null);
public record AbsEbookFile(
    [property: JsonPropertyName("ebookFormat")] string? EbookFormat,
    [property: JsonPropertyName("metadata")] AbsEbookFileMetadata? Metadata);
public record AbsEbookFileMetadata(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtimeMs")] long MtimeMs);
```

- [ ] **Step 4: Extend `AbsClient.cs`**

```csharp
public async Task<AbsItemDetail> GetItemDetailAsync(string accessToken, string itemId, CancellationToken ct = default)
{
    using var res = await SendAuthedAsync(HttpMethod.Get, $"/api/items/{Uri.EscapeDataString(itemId)}", accessToken, ct);
    return await res.Content.ReadFromJsonAsync<AbsItemDetail>(ct)
        ?? new AbsItemDetail(null);
}

public async Task<(Stream Content, string ContentType)> GetEbookStreamAsync(string accessToken, string itemId, CancellationToken ct = default)
{
    var url = $"/api/items/{Uri.EscapeDataString(itemId)}/ebook";
    var res = await SendAuthedAsync(HttpMethod.Get, url, accessToken, ct); // caller owns the stream
    var type = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
    return (await res.Content.ReadAsStreamAsync(ct), type);
}
```
And update `GetItemsAsync` signature to `(..., string? filter = null, string? sort = null, bool desc = false, CancellationToken ct = default)`, appending `&sort=<esc>` and `&desc=1` when `sort` is set.

- [ ] **Step 5: Run tests, verify pass; fix the existing `GetItemsAsync` callers/signature.**

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Abs tests/Inkshelf.Tests/AbsClientTests.cs
git commit -m "feat: item detail, ebook stream, and sort params in AbsClient"
```

---

## Task 3: Sorting UI

**Files:**
- Create: `src/Inkshelf/Pages/SortLinks.cs`, `tests/Inkshelf.Tests/SortLinksTests.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`, `src/Inkshelf/Pages/Library.cshtml`, `src/Inkshelf/wwwroot/app.css`

**Interfaces:**
- Produces:
  - `SortLinks.Next(field, currentSort, currentDesc)` → `(string? sort, bool desc)` cycling off→asc→desc→off.
  - `SortLinks.Arrow(field, currentSort, currentDesc)` → `""`/`" ↑"`/`" ↓"`.
  - `LibraryModel` exposes `Sort`, `Desc`, and a `SortHref(field)` that preserves the active facet and resets to page 1.

- [ ] **Step 1: Write failing tests (`SortLinksTests`)**

```csharp
using Inkshelf.Pages;

namespace Inkshelf.Tests;

public class SortLinksTests
{
    [Fact] public void Inactive_field_goes_ascending()
    { var (s, d) = SortLinks.Next("addedAt", "media.metadata.title", false); Assert.Equal("addedAt", s); Assert.False(d); }

    [Fact] public void Ascending_active_goes_descending()
    { var (s, d) = SortLinks.Next("t", "t", false); Assert.Equal("t", s); Assert.True(d); }

    [Fact] public void Descending_active_turns_off()
    { var (s, d) = SortLinks.Next("t", "t", true); Assert.Null(s); Assert.False(d); }

    [Theory]
    [InlineData("t", "t", false, " ↑")]
    [InlineData("t", "t", true, " ↓")]
    [InlineData("t", "x", false, "")]
    public void Arrow_reflects_state(string field, string cur, bool desc, string expected)
        => Assert.Equal(expected, SortLinks.Arrow(field, cur, desc));
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement `SortLinks.cs`**

```csharp
namespace Inkshelf.Pages;

public static class SortLinks
{
    // Cycle a field: inactive -> ascending -> descending -> off.
    public static (string? sort, bool desc) Next(string field, string? currentSort, bool currentDesc)
    {
        if (currentSort != field) return (field, false);
        if (!currentDesc) return (field, true);
        return (null, false);
    }

    public static string Arrow(string field, string? currentSort, bool currentDesc) =>
        currentSort == field ? (currentDesc ? " ↓" : " ↑") : "";
}
```

- [ ] **Step 4: Run tests, verify pass.**

- [ ] **Step 5: `LibraryModel` — sort state + href helpers**

Add `[FromQuery] public string? Sort { get; set; }` and `[FromQuery(Name="desc")] public bool Desc { get; set; }`. Pass `Sort`/`Desc` into `GetItemsAsync` (listing branch only). Add a single URL builder that any link uses, so facet + sort + page always compose:

```csharp
// Build a listing URL for this library carrying the active facet, plus the
// given sort/page overrides. page resets to 1 on a sort change.
public string ListingHref(string? sort, bool desc, int page)
{
    var qs = new List<string>();
    if (!string.IsNullOrEmpty(Filter)) qs.Add("filter=" + Uri.EscapeDataString(Filter));
    if (!string.IsNullOrEmpty(Author)) qs.Add("author=" + Uri.EscapeDataString(Author));
    if (!string.IsNullOrEmpty(Series)) qs.Add("series=" + Uri.EscapeDataString(Series));
    if (!string.IsNullOrEmpty(sort)) { qs.Add("sort=" + Uri.EscapeDataString(sort)); if (desc) qs.Add("desc=1"); }
    if (page > 1) qs.Add("page=" + page);
    return $"/library/{Id}" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
}

public string SortHref(string field)
{
    var (s, d) = SortLinks.Next(field, Sort, Desc);
    return ListingHref(s, d, 1);
}
```
Update the existing pager links to use `ListingHref(Sort, Desc, DisplayPage±1)` instead of the ad-hoc `filterQs`.

- [ ] **Step 6: Sort-links row in `Library.cshtml`**

In the listing branch (not search), above the pager:

```html
<nav class="sortbar">
    Sort:
    <a href="@Model.SortHref("media.metadata.title")">Title@(Inkshelf.Pages.SortLinks.Arrow("media.metadata.title", Model.Sort, Model.Desc))</a> ·
    <a href="@Model.SortHref("media.metadata.authorNameLF")">Author@(Inkshelf.Pages.SortLinks.Arrow("media.metadata.authorNameLF", Model.Sort, Model.Desc))</a> ·
    <a href="@Model.SortHref("addedAt")">Added@(Inkshelf.Pages.SortLinks.Arrow("addedAt", Model.Sort, Model.Desc))</a>
    @if (!string.IsNullOrEmpty(Model.Series) || Model.Filter?.StartsWith("series.") == true)
    {
        <text> · </text><a href="@Model.SortHref("sequence")">Sequence@(Inkshelf.Pages.SortLinks.Arrow("sequence", Model.Sort, Model.Desc))</a>
    }
</nav>
```
Add `.sortbar { margin: .5rem 0; }` to `app.css`.

- [ ] **Step 7: Build + full suite, then commit**

```bash
git add src/Inkshelf/Pages/SortLinks.cs tests/Inkshelf.Tests/SortLinksTests.cs src/Inkshelf/Pages/Library.cshtml.cs src/Inkshelf/Pages/Library.cshtml src/Inkshelf/wwwroot/app.css
git commit -m "feat: cycling sort links that compose with filters and paging"
```

---

## Task 4: Download primary ebook

**Files:**
- Modify: `src/Inkshelf/Program.cs`, `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, `src/Inkshelf/wwwroot/app.css`

**Interfaces:**
- Consumes: `AbsClient.GetItemDetailAsync`, `GetEbookStreamAsync`.
- Produces: `GET /download/{id}`; a `Download` link on ebook rows.

- [ ] **Step 1: `/download/{id}` endpoint (before `app.Run()`)**

```csharp
app.MapGet("/download/{id}", async (string id, AbsSession session, AbsClient client, CancellationToken ct) =>
{
    try
    {
        var detail = await session.ExecuteAsync((tok, c) => client.GetItemDetailAsync(tok, id, c), ct);
        var name = detail.Media?.EbookFile?.Metadata?.Filename;
        if (string.IsNullOrEmpty(name)) return Results.NotFound();
        var (stream, contentType) = await session.ExecuteAsync((tok, c) => client.GetEbookStreamAsync(tok, id, c), ct);
        return Results.File(stream, contentType, fileDownloadName: name);
    }
    catch (HttpRequestException) { return Results.NotFound(); }
});
```

- [ ] **Step 2: `Download` link in `_ItemRow.cshtml`**

After the `.body` metadata, gated on ebook presence:

```html
@{ var fmt = Model.Media?.EbookFormat; }
@if (!string.IsNullOrEmpty(fmt))
{
    <div class="actions">
        <a href="/download/@Model.Id">Download</a>
        @if (fmt == "cbz" || fmt == "cbr")
        {
            <a href="/convert/@Model.Id">Convert to epub</a>
            <a class="regen" href="/convert/@Model.Id?fresh=1" title="Regenerate">&#8635;</a>
        }
    </div>
}
```
Add `.actions { margin-top: .25rem; }` and `.actions a { margin-right: .75rem; }` to `app.css`. (The `/convert` links are wired in Task 7; the markup is fine now — they 404 until then.)

- [ ] **Step 3: Build + suite; commit**

```bash
git add src/Inkshelf/Program.cs src/Inkshelf/Pages/Shared/_ItemRow.cshtml src/Inkshelf/wwwroot/app.css
git commit -m "feat: download the primary ebook file"
```

---

## Task 5: EPUB cache

**Files:**
- Create: `src/Inkshelf/Convert/EpubCache.cs`, `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Produces `EpubCache`:
  - `string PathFor(string itemId, long size, long mtimeMs)` → `<dir>/{itemId}-{size}-{mtimeMs}.epub`
  - `bool TryGet(string itemId, long size, long mtimeMs, out string path)` (exists?)
  - `void RemoveForItem(string itemId)` (delete `{itemId}-*.epub`)

- [ ] **Step 1: Write failing tests**

```csharp
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class EpubCacheTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "ic-" + Path.GetRandomFileName()); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void PathFor_uses_id_size_mtime()
    {
        var c = new EpubCache(TempDir());
        Assert.EndsWith("i1-100-200.epub", c.PathFor("i1", 100, 200));
    }

    [Fact]
    public void RemoveForItem_deletes_all_variants()
    {
        var dir = TempDir();
        var c = new EpubCache(dir);
        File.WriteAllText(c.PathFor("i1", 1, 1), "a");
        File.WriteAllText(c.PathFor("i1", 2, 2), "b");
        File.WriteAllText(c.PathFor("i2", 1, 1), "c");
        c.RemoveForItem("i1");
        Assert.False(c.TryGet("i1", 1, 1, out _));
        Assert.False(c.TryGet("i1", 2, 2, out _));
        Assert.True(c.TryGet("i2", 1, 1, out _));
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement `EpubCache.cs`**

```csharp
namespace Inkshelf.Convert;

public class EpubCache
{
    private readonly string _dir;
    public EpubCache(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    public string PathFor(string itemId, long size, long mtimeMs) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}.epub");

    public bool TryGet(string itemId, long size, long mtimeMs, out string path)
    {
        path = PathFor(itemId, size, mtimeMs);
        return File.Exists(path);
    }

    public void RemoveForItem(string itemId)
    {
        foreach (var f in Directory.EnumerateFiles(_dir, $"{itemId}-*.epub"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
```

- [ ] **Step 4: Run tests, verify pass. Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: on-disk EPUB cache keyed by item id + size + mtime"
```

---

## Task 6: EPUB converter (CBZ/CBR → fixed-layout EPUB)

**Files:**
- Create: `src/Inkshelf/Convert/EpubConverter.cs`, `tests/Inkshelf.Tests/EpubConverterTests.cs`

**Interfaces:**
- Produces:
  - `record EbookMeta(string Title, string Author, string? Series, string? Sequence)`.
  - `EpubConverter.ConvertAsync(Stream archive, EbookMeta meta, string outPath, CancellationToken ct)` — writes a fixed-layout EPUB to `outPath`.

- [ ] **Step 1: Write failing test (builds a tiny in-memory CBZ incl. a WebP page)**

```csharp
using System.IO.Compression;
using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace Inkshelf.Tests;

public class EpubConverterTests
{
    private static byte[] Img(int w, int h, IImageEncoder enc)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream(); img.Save(ms, enc); return ms.ToArray();
    }

    private static MemoryStream Cbz()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            add("page-02.png", Img(80, 120, new PngEncoder()));
            add("page-01.jpg", Img(80, 120, new JpegEncoder()));
            add("page-03.webp", Img(80, 120, new WebpEncoder()));
        }
        ms.Position = 0; return ms;
    }

    [Fact]
    public async Task Convert_produces_fixed_layout_epub_pages_in_order_no_webp()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("Vol 1", "Artist", "Saga", "1"), outPath, default);

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        // mimetype first and stored uncompressed
        Assert.Equal("mimetype", epub.Entries[0].FullName);
        Assert.Equal(epub.Entries[0].Length, epub.Entries[0].CompressedLength);
        // three page images + xhtml, container + opf + nav
        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains(names, n => n.EndsWith("content.opf"));
        Assert.Equal(3, names.Count(n => n.EndsWith(".xhtml") && n.Contains("page")));
        // webp transcoded away
        Assert.DoesNotContain(names, n => n.EndsWith(".webp"));
        // opf references title/author and pre-paginated
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("Vol 1", opf);
        Assert.Contains("Artist", opf);
        Assert.Contains("pre-paginated", opf);
        File.Delete(outPath);
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement `EpubConverter.cs`**

Key points: read archive with SharpCompress (`ArchiveFactory.Open` needs a
seekable stream — the caller passes a `MemoryStream`); collect image entries
(`.jpg/.jpeg/.png/.gif/.webp`), order by `Key` (ordinal); per page read bytes,
`Image.Identify` for `Width`/`Height`; if the entry is WebP, `Image.Load` +
`SaveAsJpeg` and use `.jpg`; write the EPUB with `System.IO.Compression`.

```csharp
using System.IO.Compression;
using System.Text;
using System.Xml;
using SharpCompress.Archives;
using SixLabors.ImageSharp;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence);

public class EpubConverter
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, CancellationToken ct)
    {
        // Collect + normalise pages (transcode webp -> jpeg) in archive order.
        var pages = new List<(string Name, byte[] Bytes, int W, int H)>();
        using (var arc = ArchiveFactory.Open(archive))
        {
            var entries = arc.Entries
                .Where(e => !e.IsDirectory && ImageExts.Contains(Path.GetExtension(e.Key ?? "").ToLowerInvariant()))
                .OrderBy(e => e.Key, StringComparer.Ordinal);
            var idx = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                using var es = e.OpenEntryStream();
                using var mem = new MemoryStream();
                await es.CopyToAsync(mem, ct);
                var bytes = mem.ToArray();
                var ext = Path.GetExtension(e.Key ?? "").ToLowerInvariant();
                int w, h;
                if (ext == ".webp")
                {
                    using var img = Image.Load(bytes);
                    w = img.Width; h = img.Height;
                    using var outMs = new MemoryStream();
                    await img.SaveAsJpegAsync(outMs, ct);
                    bytes = outMs.ToArray(); ext = ".jpg";
                }
                else
                {
                    var info = Image.Identify(bytes);
                    w = info.Width; h = info.Height;
                }
                idx++;
                pages.Add(($"page-{idx:D4}{ext}", bytes, w, h));
            }
        }

        var tmp = outPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // mimetype MUST be first and stored uncompressed.
            var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var s = mt.Open()) s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));

            void Write(string name, string content)
            { using var s = zip.CreateEntry(name).Open(); var b = Encoding.UTF8.GetBytes(content); s.Write(b); }

            Write("META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

            // page images + xhtml
            for (var i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                using (var s = zip.CreateEntry($"OEBPS/img/{p.Name}").Open()) s.Write(p.Bytes);
                Write($"OEBPS/page-{i + 1:D4}.xhtml", PageXhtml(p.Name, p.W, p.H));
            }
            Write("OEBPS/content.opf", Opf(meta, pages));
            Write("OEBPS/nav.xhtml", Nav(pages.Count));
        }
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static string PageXhtml(string img, int w, int h) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta name=\"viewport\" content=\"width={w}, height={h}\"/><style>html,body{{margin:0;padding:0}}img{{width:100%;height:100%}}</style></head><body><img src=\"img/{img}\" alt=\"\"/></body></html>";

    private static string Nav(int n)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>nav</title></head><body><nav epub:type=\"toc\"><ol>");
        for (var i = 1; i <= n; i++) sb.Append($"<li><a href=\"page-{i:D4}.xhtml\">Page {i}</a></li>");
        sb.Append("</ol></nav></body></html>");
        return sb.ToString();
    }

    private static string Opf(EbookMeta m, List<(string Name, byte[] Bytes, int W, int H)> pages)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" prefix=\"rendition: http://www.idpf.org/vocab/rendition/#\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        sb.Append($"<dc:identifier id=\"bookid\">inkshelf</dc:identifier><dc:title>{Esc(m.Title)}</dc:title><dc:language>en</dc:language><dc:creator>{Esc(m.Author)}</dc:creator>");
        sb.Append("<meta property=\"rendition:layout\">pre-paginated</meta>");
        if (!string.IsNullOrEmpty(m.Series)) sb.Append($"<meta name=\"calibre:series\" content=\"{Esc(m.Series)}\"/>");
        if (!string.IsNullOrEmpty(m.Sequence)) sb.Append($"<meta name=\"calibre:series_index\" content=\"{Esc(m.Sequence)}\"/>");
        sb.Append("</metadata><manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        for (var i = 0; i < pages.Count; i++)
        {
            var mime = Path.GetExtension(pages[i].Name).ToLowerInvariant() == ".png" ? "image/png"
                : Path.GetExtension(pages[i].Name).ToLowerInvariant() == ".gif" ? "image/gif" : "image/jpeg";
            sb.Append($"<item id=\"img{i + 1}\" href=\"img/{pages[i].Name}\" media-type=\"{mime}\"/>");
            sb.Append($"<item id=\"pg{i + 1}\" href=\"page-{i + 1:D4}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        }
        sb.Append("</manifest><spine>");
        for (var i = 0; i < pages.Count; i++) sb.Append($"<itemref idref=\"pg{i + 1}\" properties=\"rendition:layout-pre-paginated\"/>");
        sb.Append("</spine></package>");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests, verify pass.** (If ImageSharp's WebP encoder needs a package, it's included in the main `SixLabors.ImageSharp` — no extra reference.)

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/EpubConverter.cs tests/Inkshelf.Tests/EpubConverterTests.cs
git commit -m "feat: CBZ/CBR to fixed-layout EPUB converter"
```

---

## Task 7: /convert endpoint + row wiring + DI

**Files:**
- Modify: `src/Inkshelf/Program.cs`

**Interfaces:**
- Consumes: `AbsSession`, `AbsClient`, `EpubCache`, `EpubConverter`.
- Produces: `GET /convert/{id}[?fresh=1]`; `EpubCache` (singleton) + `EpubConverter` (singleton) registered.

- [ ] **Step 1: Register services (with the cache path from Task 1)**

```csharp
builder.Services.AddSingleton(new Inkshelf.Convert.EpubCache(cachePath));
builder.Services.AddSingleton<Inkshelf.Convert.EpubConverter>();
```

- [ ] **Step 2: `/convert/{id}` endpoint**

```csharp
app.MapGet("/convert/{id}", async (string id, bool? fresh, AbsSession session, AbsClient client,
    Inkshelf.Convert.EpubCache cache, Inkshelf.Convert.EpubConverter converter, CancellationToken ct) =>
{
    Inkshelf.Abs.AbsItemDetail detail;
    try { detail = await session.ExecuteAsync((tok, c) => client.GetItemDetailAsync(tok, id, c), ct); }
    catch (HttpRequestException) { return Results.NotFound(); }

    var ef = detail.Media?.EbookFile;
    var fmt = ef?.EbookFormat;
    if (ef?.Metadata is null || (fmt != "cbz" && fmt != "cbr")) return Results.NotFound();

    var size = ef.Metadata.Size; var mtime = ef.Metadata.MtimeMs;
    if (fresh == true) cache.RemoveForItem(id);

    var path = cache.PathFor(id, size, mtime);
    if (!File.Exists(path))
    {
        var (archive, _) = await session.ExecuteAsync((tok, c) => client.GetEbookStreamAsync(tok, id, c), ct);
        using var buffered = new MemoryStream();
        await using (archive) await archive.CopyToAsync(buffered, ct);   // SharpCompress needs a seekable stream
        buffered.Position = 0;
        var md = detail.Media!.Metadata!;
        var author = md.AuthorName ?? (md.Authors is { Count: > 0 } ? md.Authors[0].Name : "Unknown");
        var seq = md.Series is { Count: > 0 } ? md.Series[0].Sequence : null;
        var seriesName = md.Series is { Count: > 0 } ? md.Series[0].Name : md.SeriesName;
        await converter.ConvertAsync(buffered, new Inkshelf.Convert.EbookMeta(md.Title ?? "Untitled", author, seriesName, seq), path, ct);
    }

    var title = detail.Media!.Metadata!.Title ?? "book";
    var authorName = detail.Media!.Metadata!.AuthorName ?? "Unknown";
    var fileName = Sanitize($"{authorName} - {title}") + ".epub";
    return Results.File(path, "application/epub+zip", fileDownloadName: fileName);

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
});
```

- [ ] **Step 3: Build + full suite (should stay green).**

- [ ] **Step 4: Commit**

```bash
git add src/Inkshelf/Program.cs
git commit -m "feat: /convert endpoint serving cached CBZ/CBR to EPUB"
```

---

## Task 8: Integration verify, smoke, PR

**Files:**
- Modify: `docker/smoke-test.sh`

- [ ] **Step 1: Extend the smoke** (after the cover check)

```bash
# Download a plain ebook (epub fixture) and convert a comic (cbz fixture).
EPUB_ID=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=200" -H "Authorization: Bearer $TOKEN" \
  | python3 -c "import sys,json;print(next(r['id'] for r in json.load(sys.stdin)['results'] if (r.get('media') or {}).get('ebookFormat')=='epub'))")
CBZ_ID=$(curl -sf "$ABS_URL/api/libraries/$LIBRARY_ID/items?limit=200" -H "Authorization: Bearer $TOKEN" \
  | python3 -c "import sys,json;print(next(r['id'] for r in json.load(sys.stdin)['results'] if (r.get('media') or {}).get('ebookFormat')=='cbz'))")
for p in "/download/$EPUB_ID" "/convert/$CBZ_ID" "/convert/$CBZ_ID?fresh=1"; do
    code=$(curl -s -o /dev/null -w "%{http_code}" -b "$JAR" "$INKSHELF_URL$p")
    [ "$code" = "200" ] || fail "GET $p expected 200 got $code"
    echo "  ok: $p ($code)"
done
```

- [ ] **Step 2: Seeded-ABS verification (manual, during implementation)**

Bring up + seed the stack (`docker compose -f docker/docker-compose.yml up -d`, resolve IP, `ABS_URL=... bash docker/seed.sh`), run Inkshelf against it, and confirm in a browser: sort links cycle (off→asc→desc→off) and keep the active filter + page; ebook rows show Download (all formats) and Convert+↻ (cbz/cbr); a converted EPUB downloads and opens; `↻` regenerates; audio-only rows show no link. Then `INKSHELF_URL=... ABS_URL=... bash docker/smoke-test.sh` → SMOKE PASS.

- [ ] **Step 3: Push, PR, CI**

```bash
git push -u origin feat/sort-and-download
gh pr create --base main --title "feat: sorting + ebook download and CBZ/CBR→EPUB conversion" \
  --body "Cycling sort links (compose with filters/paging), primary-ebook download, and cached on-the-fly CBZ/CBR→EPUB conversion with force-regen. See docs/superpowers/specs/2026-07-13-sorting-and-ebook-delivery-design.md."
RUN_ID=$(gh run list --branch feat/sort-and-download --limit 1 --json databaseId -q '.[0].databaseId'); gh run watch "$RUN_ID" --exit-status
```

- [ ] **Step 4: Final smoke against the real ABS**, then merge (human) → `:main` republishes.

---

## Self-Review notes

- **Spec coverage:** deps+cache config+volume (T1), models/detail/ebook-stream/sort param (T2), sort UI cycle + compose (T3), download (T4), cache (T5), converter incl. fixed-layout + webp (T6), /convert + regen + Author-Title filename (T7), smoke/verify/PR (T8). All covered.
- **First NuGets:** SharpCompress + ImageSharp added in T1; converter/tests depend on them (T6).
- **Seekable stream:** SharpCompress needs random access for RAR, so `/convert` buffers the downloaded archive into a `MemoryStream` before `ArchiveFactory.Open`. Download streams directly (no buffering).
- **mimetype-first/stored:** asserted in the converter test (`entry[0]` name + compressed==uncompressed length).
- **Row links gated on `ebookFormat`;** `/download` + `/convert` return 404 defensively for non-ebook/direct hits.
- **Sort composition:** one `ListingHref` builder used by both sort links and the pager so facet+sort+page never drift apart.
