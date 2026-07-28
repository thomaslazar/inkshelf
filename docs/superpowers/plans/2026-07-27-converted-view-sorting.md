# Converted View Sorting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Default `/converted` to newest-conversion-first and add Converted / Series / Title / Author sort links.

**Architecture:** `CachedVariant` gains a `ConvertedAtUtc` read from the cached `.epub`'s own filesystem write time (NOT the source-ebook `MtimeMs` already in the filename). `ConvertedModel` binds `sort` and `desc` query parameters the same way the library listing does, sorts its already-materialised local list by the selected key, and renders a `<nav class="sortbar">` reusing the existing CSS and `SortLinks.Arrow`.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit. No new dependencies.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-27-converted-view-sorting-design.md`. Read it before starting.
- **No new dependencies. No new localisation keys** — `"Sort:"`, `"Title"`, `"Author"`, `"Series"`, `"Converted"` all already exist in `src/Inkshelf/locales/de.json` (the only locale file). Do not add or rename catalog keys.
- **All work happens inside the devcontainer.** There is no `dotnet` on the host.
- **Branch:** `feat/converted-sort` (already created, spec already committed).
- **Conventional Commits**, imperative lowercase subject, max ~72 chars. Types used here: `feat`, `test`.
- **Do NOT add `Co-Authored-By:` or "Generated with Claude Code" lines to commits.**
- **Do NOT edit `CHANGELOG.md`.** It is written only by the release skill. Shipped work is recorded in `ROADMAP.md`'s Done section and `ARCHITECTURE.md`.
- **`desc` MUST bind as `string?`, never `bool`.** Razor's bool binder rejects `"1"`, which is the form the app uses everywhere. Typed as `bool`, every descending direction silently becomes unreachable.
- **The conversion timestamp MUST come from the cached file's write time**, never from `CachedVariant.MtimeMs`. That field is the *source ebook's* mtime, part of the cache key for invalidation. Sorting on it orders by ABS activity instead of conversion time — wrong, and it looks right.
- `dotnet format Inkshelf.sln --verify-no-changes` runs in CI over the whole solution. Run it before the final commit.
- Run the suite with `dotnet test` from `/workspaces/inkshelf`. It should report **243 passed** before you start.

---

### Task 1: `ConvertedAtUtc` on `CachedVariant`

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs`
- Test: `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `EpubCache.CachedVariant` gains a final positional member `DateTime ConvertedAtUtc`. Full shape becomes `CachedVariant(string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path, DateTime ConvertedAtUtc)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/EpubCacheTests.cs`. Read the file first to reuse its existing temp-directory helper rather than adding another one.

```csharp
    [Fact]
    public void ListVariants_reports_the_files_own_write_time_not_the_source_mtime()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);

        // The filename's mtime field is the SOURCE ebook's mtime (cache-key input).
        // Give the file a write time deliberately unrelated to it, so a mix-up shows.
        var path = cache.PathFor("item1", 111, 222, 375, 812);
        File.WriteAllText(path, "epub");
        var written = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        var v = Assert.Single(cache.ListVariants());
        Assert.Equal(222, v.MtimeMs);                  // still the source mtime
        Assert.Equal(written, v.ConvertedAtUtc);       // and the real conversion time
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EpubCacheTests"`
Expected: FAIL to compile — `'CachedVariant' has no member 'ConvertedAtUtc'`. A compile error is a legitimate red here.

- [ ] **Step 3: Add the field**

In `src/Inkshelf/Convert/EpubCache.cs`, extend the record and note what the two timestamps mean, because they are easy to confuse:

```csharp
    // One cached EPUB, decoded back into its cache-key parts. Mirrors PathFor.
    // NOTE two different timestamps live here: MtimeMs is the SOURCE ebook file's
    // mtime in ABS, part of the cache key so a changed source invalidates the
    // entry. ConvertedAtUtc is when WE wrote this EPUB. Anything user-facing about
    // "when was this converted" wants ConvertedAtUtc.
    public sealed record CachedVariant(
        string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path,
        DateTime ConvertedAtUtc);
```

Then change `ListVariants` to enumerate `FileInfo` so the write time comes from the directory walk instead of a second stat per file, and pass it to `TryParse`:

```csharp
    public IEnumerable<CachedVariant> ListVariants()
    {
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*.epub"))
        {
            if (TryParse(f) is { } v) yield return v;
        }
    }
```

Change `TryParse`'s signature to `private static CachedVariant? TryParse(FileInfo file)`, derive `path` from `file.FullName` at the top of the method so the rest of the parsing body is untouched, and pass `file.LastWriteTimeUtc` as the new final constructor argument at every `return new CachedVariant(...)` site in that method.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EpubCacheTests"`
Expected: PASS, including the file's pre-existing tests — they must keep passing unchanged, since the parsing logic itself is not being altered.

Then: `dotnet test`
Expected: PASS, 244 tests (243 + 1).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: expose when a cached epub was converted"
```

---

### Task 2: Remove touch-on-serve; evict FIFO by conversion time

`EpubCache.Touch` re-stamps a served file's write time so `EnforceCap` acts as approximate LRU. It is removed. Read section **A2** of the spec for the reasoning — the short version is that this cache bridges one expensive conversion to one download, and touch-on-serve protects already-consumed volumes while evicting the ones not yet fetched. Removing it also makes `ConvertedAtUtc` (Task 1) genuinely mean conversion time, which the next task relies on.

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs` (delete `Touch`, reword `EnforceCap`'s comment)
- Modify: `src/Inkshelf/Convert/ConvertService.cs:39` (drop the `Touch` call)
- Modify: `docs/ARCHITECTURE.md:200`
- Test: `tests/Inkshelf.Tests/ConvertServiceTests.cs`, `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `EpubCache.Touch` no longer exists. `EnforceCap` still orders by `LastWriteTimeUtc` — unchanged code, new meaning (oldest conversion rather than least recently used).

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/ConvertServiceTests.cs`, reusing its existing `TempDir`, `DetailClient`, `DetailJson`, `TokenStoreWith` and `Service` helpers:

```csharp
    [Fact]
    public async Task A_cache_hit_does_not_restamp_the_file()
    {
        // The cached EPUB's write time IS its conversion time, and /converted sorts
        // on it. Serving a hit must not bump it — otherwise fetching an old comic
        // would reorder it to "newest conversion", and cap eviction would protect
        // volumes already on the reader while deleting ones not yet fetched.
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var target = new RenderTarget(100, 200, 1.0, false);
        var path = cache.PathFor("item1", 1, 2, target.MaxW, target.MaxH);
        File.WriteAllText(path, "epub");
        var converted = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, converted);

        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 1, 2)),
            cache, new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.KickAsync("item1", fresh: false, target, default);

        Assert.Equal(ConvertStatus.Done, r.Status);                    // it was a cache hit
        Assert.Equal(converted, File.GetLastWriteTimeUtc(path));       // and it stayed put
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~A_cache_hit_does_not_restamp"`
Expected: FAIL — the write time comes back as roughly now instead of 2026-01-02, because `KickAsync` still calls `Touch`. This is the real red: the behaviour being removed.

- [ ] **Step 3: Delete `Touch` and its call**

In `src/Inkshelf/Convert/ConvertService.cs` line 39, drop the `Touch` call so the cache-hit branch becomes:

```csharp
        if (System.IO.File.Exists(path)) { return new KickResult(ConvertStatus.Done, path, downloadName); }
```

In `src/Inkshelf/Convert/EpubCache.cs`, delete the whole `Touch` method **and** its preceding comment block, then reword `EnforceCap`'s comment so it no longer claims LRU:

```csharp
    // Evict oldest-by-conversion-time entries until total cache bytes are under the
    // cap. FIFO, not LRU, and deliberately so: this cache bridges one expensive
    // conversion to one download, after which the EPUB lives on the reader. Nothing
    // re-stamps a served file, so write time stays the conversion time — which is
    // also what /converted sorts on. No-op when maxBytes <= 0 or already under.
    // Best-effort (ignores IO races).
```

- [ ] **Step 4: Delete the now-meaningless test**

Remove `EpubCacheTests.Touch_bumps_last_write_time` entirely (around line 97) — it tests a method that no longer exists.

Leave `EpubCacheTests.EnforceCap_deletes_oldest_until_under_cap` untouched. It sets write times explicitly and asserts oldest-first eviction, which is exactly the FIFO behaviour that survives — it is the guard that this change didn't break eviction.

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: PASS, **243** tests — 244 from Task 1, plus the new one, minus the deleted `Touch` test, minus `ConvertServiceTests.KickAsync_touches_cached_file_on_serve` which asserted the very behaviour being removed.

- [ ] **Step 6: Fix the env-var docs**

`docs/ARCHITECTURE.md:200` is wrong on two counts — it says LRU, and its stated default is stale (1 GB, while `AbsOptions.MaxCacheBytes` is `5_368_709_120`). Replace the row:

```
| `MaxCacheBytes` | `1073741824` (1 GB) | LRU-evict the EPUB cache past this |
```

with:

```
| `MaxCacheBytes` | `5368709120` (5 GiB) | evict oldest conversions past this |
```

`README.md:140` already says "oldest entries are evicted past it" with the right default, so leave it alone.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: evict the epub cache FIFO, not touch-on-serve"
```

---

### Task 3: Sort the Converted page

**Files:**
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs`
- Test: `tests/Inkshelf.Tests/ConvertedRenderTests.cs`

**Interfaces:**
- Consumes: `CachedVariant.ConvertedAtUtc` from Task 1, which means true conversion time only because Task 2 removed touch-on-serve.
- Produces: `ConvertedModel.Sort` (`string?`), `ConvertedModel.DescParam` (`string?`), `ConvertedModel.Desc` (`bool`), and `ConvertedModel.SortHref(string field)` returning the URL for toggling that field. Task 4 renders using these.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ConvertedRenderTests.cs`. The existing fixtures are single-item, so these need a multi-item batch plus a helper to seed cache files with controlled write times. Add both near the existing `BatchJson()`:

```csharp
    // Three items whose alphabetical, series and author orders all differ from
    // each other, so a wrong sort key cannot accidentally produce the right order.
    private static string MultiBatchJson() => $$"""
        {"libraryItems":[
          {"id":"a1","libraryId":"{{LibId}}","media":{"metadata":{"title":"Zebra Tales","authors":[{"id":"x","name":"Adams"}],"series":[{"id":"s1","name":"Alpha","sequence":"2"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"a.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"b2","libraryId":"{{LibId}}","media":{"metadata":{"title":"Middle Road","authors":[{"id":"y","name":"Zimmer"}],"series":[{"id":"s1","name":"Alpha","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"b.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"c3","libraryId":"{{LibId}}","media":{"metadata":{"title":"Apple Days","authors":[{"id":"z","name":"Mills"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"c.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } }
        ]}
        """;

    private static StubHandler MultiStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(MultiBatchJson());
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    // Seed one cache file per item with an explicit conversion time.
    private static void SeedConverted(EpubCache cache, string itemId, DateTime convertedAtUtc)
    {
        var p = cache.PathFor(itemId, Size, Mtime, W, H);
        File.WriteAllText(p, "epub");
        File.SetLastWriteTimeUtc(p, convertedAtUtc);
    }

    // The order the three titles appear in the rendered HTML.
    private static List<string> TitleOrder(string html) =>
        new[] { "Zebra Tales", "Middle Road", "Apple Days" }
            .Where(t => html.Contains(t, StringComparison.Ordinal))
            .OrderBy(t => html.IndexOf(t, StringComparison.Ordinal))
            .ToList();

    private static async Task<string> GetConvertedAsync(string query, params (string Id, DateTime At)[] seed)
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MultiStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<EpubCache>();
        foreach (var (id, at) in seed) SeedConverted(cache, id, at);
        return await (await client.SendAsync(Request(factory, "/converted" + query))).Content.ReadAsStringAsync();
    }

    // b2 converted most recently, then c3, then a1 — deliberately not the
    // alphabetical, series or author order.
    private static (string, DateTime)[] Seed() =>
    [
        ("a1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        ("c3", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
        ("b2", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
    ];

    [Fact]
    public async Task Defaults_to_newest_conversion_first()
    {
        var html = await GetConvertedAsync("", Seed());
        Assert.Equal(new[] { "Middle Road", "Apple Days", "Zebra Tales" }, TitleOrder(html));
    }

    [Fact]
    public async Task Converted_desc_can_be_flipped_to_oldest_first()
    {
        var html = await GetConvertedAsync("?sort=converted", Seed());
        Assert.Equal(new[] { "Zebra Tales", "Apple Days", "Middle Road" }, TitleOrder(html));
    }

    [Fact]
    public async Task Sorts_by_title()
    {
        var html = await GetConvertedAsync("?sort=title", Seed());
        Assert.Equal(new[] { "Apple Days", "Middle Road", "Zebra Tales" }, TitleOrder(html));
    }

    [Fact]
    public async Task Sorts_by_title_descending()
    {
        var html = await GetConvertedAsync("?sort=title&desc=1", Seed());
        Assert.Equal(new[] { "Zebra Tales", "Middle Road", "Apple Days" }, TitleOrder(html));
    }

    [Fact]
    public async Task Sorts_by_series_sequence_with_unseried_last()
    {
        // Alpha #1 = Middle Road, Alpha #2 = Zebra Tales, Apple Days has no series.
        var html = await GetConvertedAsync("?sort=series", Seed());
        Assert.Equal(new[] { "Middle Road", "Zebra Tales", "Apple Days" }, TitleOrder(html));
    }

    [Fact]
    public async Task Sorts_by_author()
    {
        // Adams = Zebra Tales, Mills = Apple Days, Zimmer = Middle Road.
        var html = await GetConvertedAsync("?sort=author", Seed());
        Assert.Equal(new[] { "Zebra Tales", "Apple Days", "Middle Road" }, TitleOrder(html));
    }

    [Fact]
    public async Task An_unknown_sort_value_falls_back_to_the_default()
    {
        var html = await GetConvertedAsync("?sort=../etc/passwd", Seed());
        Assert.Equal(new[] { "Middle Road", "Apple Days", "Zebra Tales" }, TitleOrder(html));
    }

    [Fact]
    public async Task Conversion_order_ignores_the_source_mtime_in_the_filename()
    {
        // THE TRAP. CachedVariant.MtimeMs is the SOURCE ebook's mtime, not the
        // conversion time, and it sits right next to the field we want. Here every
        // file shares one source mtime while their write times differ, so anything
        // sorting on MtimeMs produces an arbitrary order and fails this test while
        // passing the others.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MultiStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<EpubCache>();
        foreach (var (id, at) in Seed()) SeedConverted(cache, id, at);

        // Prove the premise: all three filenames carry the same mtime component.
        Assert.Equal(3, cache.ListVariants().Count());
        Assert.Single(cache.ListVariants().Select(v => v.MtimeMs).Distinct());

        var html = await (await client.SendAsync(Request(factory, "/converted"))).Content.ReadAsStringAsync();
        Assert.Equal(new[] { "Middle Road", "Apple Days", "Zebra Tales" }, TitleOrder(html));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ConvertedRenderTests"`
Expected: FAIL. `Defaults_to_newest_conversion_first` reports the current series→sequence→title order (`Middle Road, Zebra Tales, Apple Days`) instead of the expected newest-first order.

- [ ] **Step 3: Bind the query parameters and sort**

In `src/Inkshelf/Pages/Converted.cshtml.cs`:

Add the bound properties and the href helper next to the existing public properties:

```csharp
    // desc binds as a STRING on purpose: ABS wants desc=1 and Razor's bool binder
    // rejects "1", so a bool here makes every descending direction unreachable.
    // Same rule as the library listing.
    [FromQuery(Name = "sort")] public string? Sort { get; set; }
    [FromQuery(Name = "desc")] public string? DescParam { get; set; }
    public bool Desc => DescParam == "1";

    // Two-state toggle, unlike the library listing's off/asc/desc cycle: this list
    // is sorted locally, so there is no "let the server decide" state to return to.
    // Clicking the active field flips direction; `converted` starts descending
    // because newest-first is the point of the page.
    public string SortHref(string field)
    {
        var nextDesc = ActiveSort == field ? !Desc : field == ConvertedKey;
        return $"/converted?sort={field}" + (nextDesc ? "&desc=1" : "");
    }

    public const string ConvertedKey = "converted";
    private static readonly string[] Keys = [ConvertedKey, "series", "title", "author"];

    // `sort` is client-supplied, so anything unrecognised — absent, misspelled or
    // hostile — means "the default view", which is newest conversion FIRST. Note
    // EffectiveDesc keys off recognition, not off `Sort is null`: with a garbage
    // value, Desc would be false and the page would render oldest-first, which is
    // not the default it claims to fall back to.
    private bool IsRecognised => Keys.Contains(Sort);
    public string ActiveSort => IsRecognised ? Sort! : ConvertedKey;
    private bool EffectiveDesc => IsRecognised ? Desc : true;
```

Collect the conversion time while enumerating the cache. Replace the existing id-collecting loop:

```csharp
        // Cache entries for THIS device. Only the SET of item ids matters for the
        // batch fetch — row state is recomputed below from the current ebook file —
        // but keep each item's newest conversion time for the default sort. An item
        // can have more than one matching variant if the source changed and the
        // older entry hasn't been evicted.
        var convertedAt = new Dictionary<string, DateTime>();
        foreach (var v in _cache.ListVariants())
        {
            if (v.MaxW != target.MaxW || v.MaxH != target.MaxH || v.Grayscale != target.Grayscale) continue;
            if (!convertedAt.TryGetValue(v.ItemId, out var seen) || v.ConvertedAtUtc > seen)
                convertedAt[v.ItemId] = v.ConvertedAtUtc;
        }
        if (convertedAt.Count == 0) return Page();
```

Update the batch call to use the dictionary's keys — replace `ids.ToList()` with `convertedAt.Keys.ToList()`.

Replace the sort at the end of `OnGetAsync`:

```csharp
        IEnumerable<(ItemRowModel Row, AbsBatchMetadata? Meta)> ordered = ActiveSort switch
        {
            "series" => built
                .OrderBy(b => HasSeries(b.Meta) ? 0 : 1)
                .ThenBy(b => SeriesKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => SeqKey(b.Meta))
                .ThenBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
            "title" => built.OrderBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
            "author" => built
                .OrderBy(b => AuthorKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
            // ConvertedAtUtc, not the source mtime in the filename.
            _ => built
                .OrderBy(b => convertedAt.TryGetValue(b.Row.Item.Id, out var at) ? at : DateTime.MinValue)
                .ThenBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
        };
        var rows = ordered.Select(b => b.Row).ToList();
        if (EffectiveDesc) rows.Reverse();
        Rows = rows;
        return Page();
```

Add the two key helpers beside the existing `SeriesKey` / `SeqKey`:

```csharp
    private static string TitleKey((ItemRowModel Row, AbsBatchMetadata? Meta) b) =>
        b.Row.Item.Media?.Metadata?.Title ?? "";

    private static string AuthorKey(AbsBatchMetadata? m) =>
        m?.Authors is { Count: > 0 } a ? a[0].Name : "";
```

Add `using Microsoft.AspNetCore.Mvc;` if it is not already present (it is — `IActionResult` is used).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConvertedRenderTests"`
Expected: PASS, including the five pre-existing tests in the file — the empty-cache, grayscale-mismatch, batch-failure and Index-link tests must all still pass untouched.

Then: `dotnet test`
Expected: PASS, 251 tests (243 + 8).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Pages/Converted.cshtml.cs tests/Inkshelf.Tests/ConvertedRenderTests.cs
git commit -m "feat: sort the converted view, newest conversion first"
```

---

### Task 4: The sortbar, plus docs

**Files:**
- Modify: `src/Inkshelf/Pages/Converted.cshtml`
- Modify: `docs/ROADMAP.md`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `tools/uicheck/Program.cs`
- Test: `tests/Inkshelf.Tests/ConvertedRenderTests.cs`

**Interfaces:**
- Consumes: `ConvertedModel.SortHref`, `.ActiveSort`, `.Desc` from Task 3.
- Produces: nothing consumed later.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/ConvertedRenderTests.cs`:

```csharp
    [Fact]
    public async Task Renders_a_sortbar_with_the_active_field_marked()
    {
        var html = await GetConvertedAsync("?sort=title", Seed());

        Assert.Contains("class=\"sortbar\"", html);
        Assert.Contains("/converted?sort=converted&amp;desc=1", html);   // default view link
        Assert.Contains("/converted?sort=series", html);
        Assert.Contains("/converted?sort=author", html);
        // Title is active and ascending, so its own link flips to descending
        // and it carries the ascending arrow.
        Assert.Contains("/converted?sort=title&amp;desc=1", html);
        Assert.Contains("↑", html);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Renders_a_sortbar"`
Expected: FAIL — `Assert.Contains() Failure` on `class="sortbar"`, which the view does not render yet.

- [ ] **Step 3: Add the sortbar**

In `src/Inkshelf/Pages/Converted.cshtml`, insert immediately after the
`<p>@L["Converted & cached on this device."]</p>` line and before the
`@if (Model.LoadError)` block:

```razor
@if (Model.Rows.Count > 0)
{
    <nav class="sortbar">
        @L["Sort:"]
        <a href="@Model.SortHref("converted")">@L["Converted"]@(Inkshelf.Pages.SortLinks.Arrow("converted", Model.ActiveSort, Model.Desc))</a> ·
        <a href="@Model.SortHref("series")">@L["Series"]@(Inkshelf.Pages.SortLinks.Arrow("series", Model.ActiveSort, Model.Desc))</a> ·
        <a href="@Model.SortHref("title")">@L["Title"]@(Inkshelf.Pages.SortLinks.Arrow("title", Model.ActiveSort, Model.Desc))</a> ·
        <a href="@Model.SortHref("author")">@L["Author"]@(Inkshelf.Pages.SortLinks.Arrow("author", Model.ActiveSort, Model.Desc))</a>
    </nav>
}
```

It is guarded on a non-empty list so the empty state and the load-error notice stay clean — there is nothing to sort in either case.

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~ConvertedRenderTests"`
Expected: PASS.

Then: `dotnet test`
Expected: PASS, 252 tests (251 + 1).

Then: `dotnet format Inkshelf.sln --verify-no-changes`
Expected: no output, exit 0. If it reports changes, run `dotnet format Inkshelf.sln` and re-run the suite.

- [ ] **Step 5: Headless browser pass**

**Do not add the sortbar assertion to the existing `converted-de` check.** That check runs *before* the Convert click in `Program.cs`, so nothing is converted yet, the page shows its empty state, and the Step 3 guard in this task correctly hides the bar. Asserting there would fail.

Instead add a **second** visit at the very end of the `UICHECK_AUTHED` block — after the failure-reason section, by which point the good `Neon Blade Vol. 1` conversion kicked off earlier has had ample time to finish. Waiting on the selector rather than a fixed sleep makes it deterministic:

```csharp
        // Converted view again, now that a conversion has actually landed — this is
        // where the sortbar exists (it's hidden on the empty state). Waiting on the
        // selector doubles as "the conversion finished".
        await page.GotoAsync(baseUrl + "/converted");
        await page.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 30000 });
        await Shot("converted-sorted-de");
        Expect("converted-sorted-de", await page.InnerTextAsync("body"),
            "Sortierung:", "Konvertiert", "Serien", "Titel", "Autor");
```

Run: `tools/uicheck/run.sh`
Expected: `PASS`. Then **look at** `tools/uicheck/shots/converted-sorted-de.png` and confirm the sortbar renders without overflowing the 758px-wide viewport — four links plus the label is the most crowded sortbar in the app, and the library listing's has only three. If it wraps or overflows, say so in your report rather than adjusting CSS: that is a design decision, not a fix to make silently.

- [ ] **Step 6: Update the docs**

In `docs/ROADMAP.md`, delete the **Sort the Converted view, newest first** bullet from `## Browsing & reading` and add to the top of `## Done`:

```markdown
- **Converted view sorting** — `/converted` defaults to newest conversion first
  (the one you came to fetch) with Converted / Series / Title / Author sort links.
  The timestamp is the cached EPUB's own write time, exposed as
  `CachedVariant.ConvertedAtUtc` — deliberately not the source-ebook `mtimeMs`
  that the cache filename carries as an invalidation key. Sorting is a two-state
  toggle rather than the library listing's off/asc/desc cycle, because a locally
  sorted list has no server-side default order to fall back to. Filtering and
  paging were considered and left out: the list is per-device and short.
```

In `docs/ARCHITECTURE.md`, replace this exact passage (around lines 98–101):

```
  a "converted" badge always agrees across pages. The `/converted` view is the EPUB
  cache read back: `EpubCache.ListVariants` reverse-parses filenames into item ids,
  filtered to the current device's target, then one cross-library
  `POST /api/items/batch/get` supplies metadata.
```

with:

```
  a "converted" badge always agrees across pages. The `/converted` view is the EPUB
  cache read back: `EpubCache.ListVariants` reverse-parses filenames into item ids,
  filtered to the current device's target, then one cross-library
  `POST /api/items/batch/get` supplies metadata. It sorts in-process — newest
  conversion first by default, with series/title/author as sort links — keyed on
  `CachedVariant.ConvertedAtUtc`, the cached file's own write time. That is
  deliberately not the `mtimeMs` in the cache filename, which is the *source*
  ebook's mtime and exists to invalidate the entry.
```

Keep the house style: present-tense design description, no changelog, no "shipped" status, no per-change enumeration. Do **not** touch `CHANGELOG.md`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "test: cover the converted sortbar; docs: record the sorting"
```

If the uicheck change and the docs feel like separate concerns, split into two commits (`test:` then `docs:`) — either is fine as long as each subject is accurate.

---

## Done criteria

- `dotnet test` reports 252 passing; `dotnet format Inkshelf.sln --verify-no-changes` is clean.
- `tools/uicheck/run.sh` passes and `shots/converted-de.png` shows the German sortbar without horizontal overflow.
- `/converted` with no query parameters lists the most recently converted comic first.
- Each of the four sort links works, the active one shows an arrow, and clicking it flips direction.
- A garbage `?sort=` value renders the default order rather than erroring.
- Nothing sorts by `CachedVariant.MtimeMs`.
- `EpubCache.Touch` no longer exists, and a cache hit leaves the file's write time alone.
