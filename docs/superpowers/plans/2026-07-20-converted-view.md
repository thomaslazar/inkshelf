# Converted (this device) View — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single `/converted` page listing every comic already converted and cached for the current device, across all libraries, reusing the existing listing row.

**Architecture:** Enumerate the EPUB cache (filenames are the only record), filter to the current device's `RenderTarget`, dedupe by item id, fetch metadata for those ids in one cross-library `POST /api/items/batch/get`, and render each as the existing `_ItemRow`. The convert-state logic is extracted from `LibraryModel` into a shared static helper so both pages agree.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit + WebApplicationFactory render tests. No client JS beyond the existing layout scripts.

## Global Constraints

- **No AOT.** .NET 10, Razor Pages for HTML.
- **Reuse `_ItemRow`** — no new row partial, no new download endpoint (use `/convert/{id}` and `/download/{id}` as the listing does).
- **No cache-key change**, no sidecar index — reverse-parse the filename.
- **DTO additions are additive only** — never declare `series` as an array on `AbsMetadata` (that reintroduces a deserialization bug); add fields to the *batch* shapes.
- **Never touch `CHANGELOG.md`** — that is release-skill-only. Record shipped work in ROADMAP "## Done" and ARCHITECTURE.
- **`LibraryLinks` is the single URL authority** — build row links through it, never inline strings.
- Cache filename scheme (authoritative, from `EpubCache.PathFor`): `{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}[-g].epub`.
- All work on branch `feat/converted-view`. `dotnet test` from repo root (inside the devcontainer) must stay green.
- Conventional Commits, subject imperative/lowercase/no period/max ~72 chars; NO `Co-Authored-By:` or "Generated with Claude Code" lines.

---

### Task 1: ABS batch fetch — expose id, libraryId, title, coverPath

Extend the batch DTOs (additive) and add `GetItemsBatchAsync` returning the raw expanded items; reimplement the existing dict method on top of it so the listing is unchanged.

**Files:**
- Modify: `src/Inkshelf/Abs/AbsModels.cs`
- Modify: `src/Inkshelf/Abs/AbsApiClient.cs`
- Test: `tests/Inkshelf.Tests/AbsApiClientTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `AbsBatchItem(string Id, AbsBatchMedia? Media, string? LibraryId = null)`
  - `AbsBatchMedia(AbsBatchMetadata? Metadata, AbsEbookFile? EbookFile = null, string? CoverPath = null)`
  - `AbsBatchMetadata(List<AbsRef>? Authors = null, List<AbsSeriesRef>? Series = null, string? Title = null)`
  - `Task<List<AbsBatchItem>> AbsApiClient.GetItemsBatchAsync(IReadOnlyCollection<string> itemIds, CancellationToken ct = default)`
  - `GetItemsMetadataBatchAsync` keeps its existing signature/behavior.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/AbsApiClientTests.cs` (the `Client(...)` helper and `StubHandler.Json` already exist):

```csharp
    [Fact]
    public async Task GetItemsBatchAsync_posts_ids_and_parses_expanded_fields()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryItems":[{"id":"i1","libraryId":"lib1","media":{"metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"}],"series":[{"id":"s1","name":"The Sandman","sequence":"2"}]},"coverPath":"/covers/i1.jpg","ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":123,"mtimeMs":456}}}}]}"""));
        var items = await Client(h).GetItemsBatchAsync(new[] { "i1" });

        Assert.Equal("/api/items/batch/get", h.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, h.Last!.Method);
        var it = Assert.Single(items);
        Assert.Equal("i1", it.Id);
        Assert.Equal("lib1", it.LibraryId);
        Assert.Equal("My Comic", it.Media!.Metadata!.Title);
        Assert.Equal("/covers/i1.jpg", it.Media!.CoverPath);
        Assert.Equal("The Sandman", it.Media!.Metadata!.Series![0].Name);
        Assert.Equal("cbz", it.Media!.EbookFile!.EbookFormat);
        Assert.Equal(123, it.Media!.EbookFile!.Metadata!.Size);
    }

    [Fact]
    public async Task GetItemsBatchAsync_empty_ids_makes_no_call()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"libraryItems":[]}"""));
        var items = await Client(h).GetItemsBatchAsync(System.Array.Empty<string>());
        Assert.Empty(items);
        Assert.Null(h.Last); // no request issued
    }

    [Fact]
    public async Task GetItemsMetadataBatchAsync_still_returns_media_dict()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryItems":[{"id":"i1","media":{"metadata":{"series":[{"id":"s1","name":"S"}]}}}]}"""));
        var map = await Client(h).GetItemsMetadataBatchAsync(new[] { "i1" });
        Assert.True(map.ContainsKey("i1"));
        Assert.Equal("S", map["i1"].Metadata!.Series![0].Name);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AbsApiClientTests`
Expected: FAIL — `GetItemsBatchAsync` doesn't exist / `AbsBatchItem` has no `LibraryId` / `AbsBatchMetadata` has no `Title` (compile errors).

- [ ] **Step 3: Extend the DTOs**

In `src/Inkshelf/Abs/AbsModels.cs`, replace the three batch records with:

```csharp
public record AbsBatchItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("media")] AbsBatchMedia? Media,
    [property: JsonPropertyName("libraryId")] string? LibraryId = null);
public record AbsBatchMedia(
    [property: JsonPropertyName("metadata")] AbsBatchMetadata? Metadata,
    // Present on the expanded shape; lets the listing tell whether a convert is
    // already cached (needs the ebook file's size + mtime for the cache key).
    [property: JsonPropertyName("ebookFile")] AbsEbookFile? EbookFile = null,
    [property: JsonPropertyName("coverPath")] string? CoverPath = null);
public record AbsBatchMetadata(
    [property: JsonPropertyName("authors")] List<AbsRef>? Authors = null,
    [property: JsonPropertyName("series")] List<AbsSeriesRef>? Series = null,
    [property: JsonPropertyName("title")] string? Title = null);
```

- [ ] **Step 4: Add `GetItemsBatchAsync` and rebuild the dict method on it**

In `src/Inkshelf/Abs/AbsApiClient.cs`, replace `GetItemsMetadataBatchAsync` with these two methods:

```csharp
    // Fetch expanded items (id, libraryId, structured metadata, coverPath,
    // ebookFile) for a set of ids in ONE call. Cross-library — batch/get queries
    // by id only, not scoped to a library.
    public async Task<List<AbsBatchItem>> GetItemsBatchAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return new();
        using var content = JsonContent.Create(new { libraryItemIds = itemIds });
        using var res = await SendAsync(HttpMethod.Post, "/api/items/batch/get", ct, content);
        var body = await res.Content.ReadFromJsonAsync<AbsBatchItems>(ct);
        return body?.LibraryItems ?? new();
    }

    // Expanded metadata keyed by item id, for the listing's per-row links/state.
    public async Task<Dictionary<string, AbsBatchMedia>> GetItemsMetadataBatchAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken ct = default)
    {
        var map = new Dictionary<string, AbsBatchMedia>();
        foreach (var it in await GetItemsBatchAsync(itemIds, ct))
            if (it.Media is not null) map[it.Id] = it.Media;
        return map;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AbsApiClientTests`
Expected: PASS — new tests plus all pre-existing `AbsApiClientTests` (the dict method is behavior-identical).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Abs/AbsModels.cs src/Inkshelf/Abs/AbsApiClient.cs tests/Inkshelf.Tests/AbsApiClientTests.cs
git commit -m "feat: expose libraryId/title/cover from the ABS batch fetch"
```

---

### Task 2: `EpubCache.ListVariants` — enumerate + reverse-parse cached files

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs`
- Test: `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `EpubCache.CachedVariant(string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path)` (nested record)
  - `IEnumerable<CachedVariant> EpubCache.ListVariants()`

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubCacheTests.cs` (uses the existing `TempDirPath()` helper):

```csharp
    [Fact]
    public void ListVariants_round_trips_PathFor_including_hyphenated_id_and_grayscale()
    {
        var dir = TempDirPath();
        var c = new EpubCache(dir);
        // A UUID-style id contains hyphens — must survive right-to-left parsing.
        var idA = "3f2a1b6c-dead-beef-0001-abcdef123456";
        File.WriteAllText(c.PathFor(idA, 100, 200, 1730, 2246), "e");
        File.WriteAllText(c.PathFor("i2", 55, 66, 800, 1000, grayscale: true), "e");

        var v = c.ListVariants().ToList();
        Assert.Equal(2, v.Count);

        var a = v.Single(x => x.ItemId == idA);
        Assert.Equal(100, a.Size); Assert.Equal(200, a.MtimeMs);
        Assert.Equal(1730, a.MaxW); Assert.Equal(2246, a.MaxH); Assert.False(a.Grayscale);

        var b = v.Single(x => x.ItemId == "i2");
        Assert.Equal(55, b.Size); Assert.Equal(66, b.MtimeMs);
        Assert.Equal(800, b.MaxW); Assert.Equal(1000, b.MaxH); Assert.True(b.Grayscale);
    }

    [Fact]
    public void ListVariants_skips_files_that_dont_match_the_scheme()
    {
        var dir = TempDirPath();
        var c = new EpubCache(dir);
        File.WriteAllText(Path.Combine(dir, "garbage.epub"), "e");
        File.WriteAllText(Path.Combine(dir, "i1-100-200-1730x2246.epub"), "e"); // valid
        Assert.Single(c.ListVariants());
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EpubCacheTests`
Expected: FAIL — `ListVariants` / `CachedVariant` don't exist (compile error).

- [ ] **Step 3: Implement `ListVariants` + `CachedVariant`**

In `src/Inkshelf/Convert/EpubCache.cs`, add inside the class:

```csharp
    // One cached EPUB, decoded back into its cache-key parts. Mirrors PathFor.
    public sealed record CachedVariant(
        string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path);

    // Enumerate cached EPUBs, parsing each filename back into its parts. Parsed
    // RIGHT-TO-LEFT (dims, then mtime, then size) so an item id containing '-'
    // (a UUID) survives intact. Filenames that don't match PathFor are skipped.
    public IEnumerable<CachedVariant> ListVariants()
    {
        foreach (var path in Directory.EnumerateFiles(_dir, "*.epub"))
        {
            if (TryParse(path) is { } v) yield return v;
        }
    }

    private static CachedVariant? TryParse(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path); // drops ".epub"
        var grayscale = name.EndsWith("-g", StringComparison.Ordinal);
        if (grayscale) name = name[..^2];

        // remaining: {itemId}-{size}-{mtimeMs}-{maxW}x{maxH}
        var d1 = name.LastIndexOf('-');
        if (d1 < 0) return null;
        var dims = name[(d1 + 1)..];
        var xi = dims.IndexOf('x');
        if (xi <= 0
            || !int.TryParse(dims[..xi], out var maxW)
            || !int.TryParse(dims[(xi + 1)..], out var maxH)) return null;

        name = name[..d1]; // {itemId}-{size}-{mtimeMs}
        var d2 = name.LastIndexOf('-');
        if (d2 < 0 || !long.TryParse(name[(d2 + 1)..], out var mtimeMs)) return null;

        name = name[..d2]; // {itemId}-{size}
        var d3 = name.LastIndexOf('-');
        if (d3 < 0 || !long.TryParse(name[(d3 + 1)..], out var size)) return null;

        var itemId = name[..d3];
        if (itemId.Length == 0) return null;
        return new CachedVariant(itemId, size, mtimeMs, maxW, maxH, grayscale, path);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~EpubCacheTests`
Expected: PASS (new tests + existing `EpubCacheTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: enumerate cached epubs via reverse-parsed filenames"
```

---

### Task 3: Extract the convert-state resolver (shared by both pages)

Move `LibraryModel`'s per-item state computation into a static helper so `/converted` reuses the exact same logic. No behavior change for the listing.

**Files:**
- Create: `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`
- Test: `tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs`

**Interfaces:**
- Consumes: `AbsItem`, `AbsBatchMedia`, `RenderTarget`, `EpubCache`, `ConvertQueue`, `ConvertRowState`.
- Produces: `static ConvertRowState ConvertRowStateResolver.Resolve(AbsItem item, AbsBatchMedia? media, RenderTarget target, EpubCache cache, ConvertQueue queue)`

- [ ] **Step 1: Write the failing test**

Create `tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs`:

```csharp
using Inkshelf.Abs;
using Inkshelf.Convert;
using Inkshelf.Pages;

namespace Inkshelf.Tests;

public class ConvertRowStateResolverTests
{
    private static string TempDirPath()
    { var d = Path.Combine(Path.GetTempPath(), "crs-" + Path.GetRandomFileName()); Directory.CreateDirectory(d); return d; }

    private static AbsItem Item(string fmt) =>
        new("i1", new AbsMedia(new AbsMetadata("T", null, null), EbookFormat: fmt));

    private static AbsBatchMedia Media(long size = 10, long mtime = 20) =>
        new(new AbsBatchMetadata(), new AbsEbookFile("cbz", new AbsEbookFileMetadata("f.cbz", size, mtime)));

    private static readonly RenderTarget Target = new(800, 1000, 1.0, false);

    [Fact]
    public void Non_comic_is_not_convertible()
    {
        var r = ConvertRowStateResolver.Resolve(Item("epub"), Media(), Target, new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.NotConvertible, r);
    }

    [Fact]
    public void Comic_without_ebookfile_metadata_is_not_convertible()
    {
        var media = new AbsBatchMedia(new AbsBatchMetadata(), new AbsEbookFile("cbz", null));
        var r = ConvertRowStateResolver.Resolve(Item("cbz"), media, Target, new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.NotConvertible, r);
    }

    [Fact]
    public void Comic_with_cached_file_is_cached()
    {
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        File.WriteAllText(cache.PathFor("i1", 10, 20, 800, 1000), "e");
        var r = ConvertRowStateResolver.Resolve(Item("cbz"), Media(), Target, cache, new ConvertQueue());
        Assert.Equal(ConvertRowState.Cached, r);
    }

    [Fact]
    public void Comic_with_nothing_cached_is_convert()
    {
        var r = ConvertRowStateResolver.Resolve(Item("cbz"), Media(), Target, new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.Convert, r);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~ConvertRowStateResolverTests`
Expected: FAIL — `ConvertRowStateResolver` doesn't exist (compile error).

- [ ] **Step 3: Create the resolver**

Create `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs`:

```csharp
using Inkshelf.Abs;
using Inkshelf.Convert;

namespace Inkshelf.Pages;

// Shared per-item convert-state computation, used by the library listing AND the
// /converted view so the two never diverge. Keyed on the resolved RenderTarget
// (scr probe + settings), matching what a real conversion writes to the cache.
public static class ConvertRowStateResolver
{
    public static ConvertRowState Resolve(AbsItem item, AbsBatchMedia? media,
        RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        // Listing items (minified) carry media.ebookFormat; search/batch items
        // (expanded) carry the format only on the ebookFile. Check all.
        var fmt = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat ?? media?.EbookFile?.EbookFormat;
        if (fmt != "cbz" && fmt != "cbr") return ConvertRowState.NotConvertible;
        var efm = media?.EbookFile?.Metadata;
        if (efm is null) return ConvertRowState.NotConvertible; // can't key the cache
        var path = cache.PathFor(item.Id, efm.Size, efm.MtimeMs, target.MaxW, target.MaxH, target.Grayscale);
        return queue.Status(path) switch
        {
            ConvertStatus.Done => ConvertRowState.Cached,
            ConvertStatus.Queued or ConvertStatus.Running => ConvertRowState.Converting,
            ConvertStatus.Failed => ConvertRowState.Failed,
            _ => ConvertRowState.Convert,
        };
    }
}
```

- [ ] **Step 4: Delegate `LibraryModel.RowState` to the resolver**

In `src/Inkshelf/Pages/Library.cshtml.cs`, replace the entire private `RowState` method body with a delegating call (leave `ComputeConvertStates` untouched — it still calls `RowState(item, media, t)`):

```csharp
    private ConvertRowState RowState(AbsItem item, AbsBatchMedia? media, RenderTarget target)
        => ConvertRowStateResolver.Resolve(item, media, target, _cache, _queue);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConvertRowStateResolverTests|FullyQualifiedName~ListingRenderTests"`
Expected: PASS — the resolver's own tests, and the existing `ListingRenderTests` (which exercise the listing's states end-to-end) confirm no behavior change.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs src/Inkshelf/Pages/Library.cshtml.cs tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs
git commit -m "refactor: extract shared convert-row-state resolver"
```

---

### Task 4: `/converted` page + `ConvertedModel` + Index entry link

The page: enumerate → filter to device → dedupe → one batch call → build `ItemRowModel`s → render `_ItemRow`. Plus the Index entry-point link.

**Files:**
- Create: `src/Inkshelf/Pages/Converted.cshtml`
- Create: `src/Inkshelf/Pages/Converted.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Index.cshtml`
- Test: `tests/Inkshelf.Tests/ConvertedRenderTests.cs`

**Interfaces:**
- Consumes: `AbsApiClient.GetItemsBatchAsync` (Task 1), `EpubCache.ListVariants`/`CachedVariant` (Task 2), `ConvertRowStateResolver.Resolve` (Task 3), existing `ItemRowModel`, `LibraryLinks`, `_ItemRow`, `ScreenTarget`, `DeviceSettings`.
- Produces: the `/converted` route.

- [ ] **Step 1: Write the failing render tests**

Create `tests/Inkshelf.Tests/ConvertedRenderTests.cs`:

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

// Renders /converted end-to-end (WebApplicationFactory + stubbed ABS) and the
// Index entry link. The cache is seeded on disk so ListVariants finds a variant
// for the request's device target.
public class ConvertedRenderTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "converted-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string ItemId = "item1";
    private const string LibId = "lib1";
    private const long Size = 12345;
    private const long Mtime = 67890;
    private const int W = 375;
    private const int H = 812;

    private static string BatchJson() => $$"""
        {"libraryItems":[{"id":"{{ItemId}}","libraryId":"{{LibId}}","media":{"metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"}],"series":[{"id":"s1","name":"The Sandman","sequence":"1"}]},"coverPath":"/c.jpg","ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":{{Size}},"mtimeMs":{{Mtime}}}}}}]}
        """;
    private const string LibrariesJson = """{"libraries":[{"id":"lib1","name":"Test Library","mediaType":"book"}]}""";

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(BatchJson());
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
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
    public async Task Lists_a_cached_item_with_title_series_link_and_epub_action()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H), "epub"); // matches the request's device target

        var html = await (await client.SendAsync(Request(factory, "/converted"))).Content.ReadAsStringAsync();

        Assert.Contains("My Comic", html);
        Assert.Contains("EPUB &#10003;", html);                 // cached state (current ebook)
        Assert.Contains($"/library/{LibId}?filter=", html);     // series/author link into the item's library
    }

    [Fact]
    public async Task Empty_when_nothing_cached_for_this_device()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(Request(factory, "/converted"))).Content.ReadAsStringAsync();
        Assert.Contains("Nothing converted for this device yet.", html);
    }

    [Fact]
    public async Task A_grayscale_only_cache_file_is_not_listed_for_a_colour_device()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H, grayscale: true), "epub");

        // Request carries no settings cookie → colour target → the "-g" variant
        // doesn't match, so the page is empty.
        var html = await (await client.SendAsync(Request(factory, "/converted"))).Content.ReadAsStringAsync();
        Assert.Contains("Nothing converted for this device yet.", html);
    }

    [Fact]
    public async Task Batch_failure_shows_a_notice_not_a_500()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var stub = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/items/batch/get") return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var factory = CreateFactory(stub, cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H), "epub"); // non-empty → batch is attempted

        var response = await client.SendAsync(Request(factory, "/converted"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Couldn't load details", html);
    }

    [Fact]
    public async Task Index_shows_the_converted_entry_link()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // ?all=1 so a favorite cookie (none here) wouldn't redirect; renders the hub.
        var html = await (await client.SendAsync(Request(factory, "/?all=1"))).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/converted\"", html);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ConvertedRenderTests`
Expected: FAIL — no `/converted` route (404 → assertions fail) and no Index link.

- [ ] **Step 3: Create `ConvertedModel`**

Create `src/Inkshelf/Pages/Converted.cshtml.cs`:

```csharp
using System.Globalization;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

// Combined "already converted, on this device" view. The EPUB cache is the only
// record of what's converted; we enumerate it, keep the variants matching this
// device's RenderTarget, dedupe by item id, then fetch metadata for those ids in
// one cross-library batch call and render the standard listing row.
public class ConvertedModel : PageModel
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    public ConvertedModel(AbsApiClient api, EpubCache cache, ConvertQueue queue)
    { _api = api; _cache = cache; _queue = queue; }

    public List<ItemRowModel> Rows { get; private set; } = new();
    public bool LoadError { get; private set; }
    public bool AnyConverting { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        var settings = DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], settings.Retina, settings.Grayscale);

        // Cache entries for THIS device, newest variant per item.
        var byItem = new Dictionary<string, EpubCache.CachedVariant>();
        foreach (var v in _cache.ListVariants())
        {
            if (v.MaxW != target.MaxW || v.MaxH != target.MaxH || v.Grayscale != target.Grayscale) continue;
            if (!byItem.TryGetValue(v.ItemId, out var cur) || v.MtimeMs > cur.MtimeMs) byItem[v.ItemId] = v;
        }
        if (byItem.Count == 0) return Page();

        List<AbsBatchItem> items;
        try { items = await _api.GetItemsBatchAsync(byItem.Keys.ToList(), ct); }
        catch (HttpRequestException) { LoadError = true; return Page(); }

        var finished = await FetchFinishedAsync(ct);

        var built = new List<(ItemRowModel Row, AbsBatchMetadata? Meta)>();
        foreach (var it in items)
        {
            if (it.Media is null) continue;
            var m = it.Media;
            // Map the batch shape into the AbsItem the shared row/resolver expect.
            var item = new AbsItem(it.Id, new AbsMedia(
                new AbsMetadata(m.Metadata?.Title, null, null), m.CoverPath, null, m.EbookFile));
            var links = new LibraryLinks(it.LibraryId ?? "", null, null, null, null, false);
            var state = ConvertRowStateResolver.Resolve(item, m, target, _cache, _queue);
            if (state == ConvertRowState.Converting) AnyConverting = true;
            built.Add((new ItemRowModel(item, links, m.Metadata?.Authors, m.Metadata?.Series,
                state, "/converted", finished.Contains(it.Id)), m.Metadata));
        }

        // series → sequence → title; items with no series sort last.
        Rows = built
            .OrderBy(b => HasSeries(b.Meta) ? 0 : 1)
            .ThenBy(b => SeriesKey(b.Meta), StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => SeqKey(b.Meta))
            .ThenBy(b => b.Row.Item.Media?.Metadata?.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(b => b.Row)
            .ToList();
        return Page();
    }

    private async Task<HashSet<string>> FetchFinishedAsync(CancellationToken ct)
    { try { return await _api.GetFinishedItemIdsAsync(ct); } catch (HttpRequestException) { return new(); } }

    private static bool HasSeries(AbsBatchMetadata? m) => m?.Series is { Count: > 0 };

    private static string SeriesKey(AbsBatchMetadata? m) =>
        m?.Series is { Count: > 0 } s ? s[0].Name : "";

    private static double SeqKey(AbsBatchMetadata? m)
    {
        var seq = m?.Series is { Count: > 0 } s ? s[0].Sequence : null;
        return double.TryParse(seq, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;
    }
}
```

- [ ] **Step 4: Create `Converted.cshtml`**

Create `src/Inkshelf/Pages/Converted.cshtml`:

```cshtml
@page
@model Inkshelf.Pages.ConvertedModel
@{
    Response.Headers["Cache-Control"] = "no-store";
    if (Model.AnyConverting) { ViewData["MetaRefresh"] = 30; }
}
<div class="page-head" id="top">
    <h1 class="page-title">
        <img src="~/img/icon-black.png" alt="" class="title-icon" />
        <a href="/?all=1">Libraries</a> <span class="crumb-sep">›</span> Converted
    </h1>
    <a class="settings-link" href="/settings" title="Settings"><img src="~/img/gear-black.png" alt="Settings" class="settings-icon" width="24" height="24" /></a>
</div>
<p>Converted &amp; cached on this device.</p>
@if (Model.LoadError)
{
    <p>Couldn't load details from Audiobookshelf. Try again.</p>
}
else if (Model.Rows.Count == 0)
{
    <p>Nothing converted for this device yet.</p>
}
else
{
    foreach (var row in Model.Rows)
    {
        <partial name="_ItemRow" model="row" />
    }
}
```

- [ ] **Step 5: Add the Index entry link**

In `src/Inkshelf/Pages/Index.cshtml`, add this line between the closing `</div>` of `page-head` and the `<ul>`:

```cshtml
<p class="converted-link"><a href="/converted">Converted on this device &#8594;</a></p>
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ConvertedRenderTests`
Expected: PASS (cached item listed, empty state, grayscale mismatch excluded, batch-failure notice, Index link present).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS (all tests green).

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Pages/Converted.cshtml src/Inkshelf/Pages/Converted.cshtml.cs src/Inkshelf/Pages/Index.cshtml tests/Inkshelf.Tests/ConvertedRenderTests.cs
git commit -m "feat: add the converted-this-device view + index link"
```

---

### Task 5: Documentation

Record the feature in ROADMAP and ARCHITECTURE. **Do not touch `CHANGELOG.md`.**

**Files:**
- Modify: `docs/ROADMAP.md`
- Modify: `docs/ARCHITECTURE.md`

**Interfaces:** none.

- [ ] **Step 1: Move the roadmap item to Done**

In `docs/ROADMAP.md`, under `## Browsing & reading`, delete the entire `- **Per-library "already converted" view.** …` bullet (through its trailing `*Note:*` sentence). Then add, as the FIRST bullet under `## Done`:

```markdown
- **Converted (this device) view** — a `/converted` page listing every comic
  already converted and cached for the current device, across all libraries
  (the cache is enumerated by reverse-parsing filenames and filtered to the
  device's render target). Reuses the listing row (`_ItemRow`), with a metadata
  batch fetch for title/series/author + a series link into each item's library.
  Reached from a link on the home page. Shipped as a single combined view (not
  per-library): `POST /api/items/batch/get` is not library-scoped, so one call
  covers every library and carries `libraryId` per item.
```

- [ ] **Step 2: Update ARCHITECTURE**

In `docs/ARCHITECTURE.md`:

(a) In the `Pages/` line of the Layout tree, add `Converted` to the page list, e.g. change
`Pages/  Razor Pages: Index, Login, Library, Settings (+ models); Shared/ partials.`
to
`Pages/  Razor Pages: Index, Login, Library, Converted, Settings (+ models); Shared/ partials.`

(b) In the `Support/` line, add `ConvertRowStateResolver`, e.g.
`Support/  Non-page helper types: LibraryLinks, ItemRowModel, Pager, SortLinks, ConvertRowStateResolver.`

(c) Add this bullet to `## Load-bearing conventions`, after the `LibraryLinks` bullet:

```markdown
- **Convert-row state is computed in one place.** `ConvertRowStateResolver.Resolve`
  turns (item, batch media, device `RenderTarget`, cache, queue) into a
  `ConvertRowState`. Both the library listing and the `/converted` view call it, so
  a "converted" badge always agrees across pages. The `/converted` view is the EPUB
  cache read back: `EpubCache.ListVariants` reverse-parses filenames into item ids,
  filtered to the current device's target, then one cross-library
  `GET /api/items/batch/get` supplies metadata.
```

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md docs/ARCHITECTURE.md
git commit -m "docs: record the converted-this-device view"
```

---

## Verification

After Task 5, from the repo root inside the devcontainer:

- [ ] Run `dotnet test` — all green.
- [ ] (Manual, optional) Run the dev server on 5099, convert a comic, then open `/converted` — confirm the item appears with its cover, title, a working series link, and an EPUB download. Confirm the home page shows the entry link. A real-device check before merge matches the near-zero-JS / defensive-CSS convention.

## Notes on decisions (from the spec)

- **Single combined view**, reusing `_ItemRow` — no new row UI, no new download endpoint.
- **Reverse-parse filenames** (no sidecar index) — keeps "done = File.Exists", no dual source of truth.
- **Stale entries** (ebook changed since conversion) render as "Convert", since state is computed from the current ebook file — correct, if slightly odd for a "converted" list. Acceptable for v1.
- **Fixed sort** series→sequence→title, no pager/sort controls (cache is size-bounded).
- **CHANGELOG.md is untouched** — release-skill only.
