using System.Net;
using Inkshelf.Abs;
using Inkshelf.Auth;
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
        {"libraryItems":[{"id":"{{ItemId}}","libraryId":"{{LibId}}","media":{"metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"}],"series":[{"id":"s1","name":"The Sandman","sequence":"1"}]},"coverPath":"/c.jpg","ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } } ]}
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

    // Three items whose alphabetical and author orders differ from each other.
    // The series sequence is set to CONTRADICT the title order (Zebra Tales is
    // sequence 1, Middle Road is sequence 2): title order alone would sort
    // Middle Road before Zebra Tales, so a series sort that silently drops the
    // sequence key and falls back to the title tiebreak produces the wrong order
    // and gets caught, instead of accidentally matching it.
    private static string MultiBatchJson() => $$"""
        {"libraryItems":[
          {"id":"a1","libraryId":"{{LibId}}","media":{"metadata":{"title":"Zebra Tales","authors":[{"id":"x","name":"Adams"}],"series":[{"id":"s1","name":"Alpha","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"a.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"b2","libraryId":"{{LibId}}","media":{"metadata":{"title":"Middle Road","authors":[{"id":"y","name":"Zimmer"}],"series":[{"id":"s1","name":"Alpha","sequence":"2"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"b.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
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

    // Seven items, so the minimum page size of 5 splits them 5 + 2. Titles are
    // zero-padded so an ordinal title sort and a numeric reading agree, which
    // keeps the expected page contents obvious.
    // Not `const`: an interpolated raw string with substitutions can't be a
    // compile-time constant even when every substitution is itself const.
    private static readonly string PagedBatchJson = $$"""
        {"libraryItems":[
          {"id":"p1","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 01","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p1.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p2","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 02","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p2.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p3","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 03","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p3.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p4","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 04","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p4.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p5","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 05","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p5.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p6","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 06","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p6.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p7","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 07","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p7.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } }
        ]}
        """;

    private static StubHandler PagedStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(PagedBatchJson);
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    // Which "Paged NN" titles appear, in the order they appear.
    private static List<string> PagedOrder(string html) =>
        Enumerable.Range(1, 7).Select(i => $"Paged {i:00}")
            .Where(t => html.Contains(t, StringComparison.Ordinal))
            .OrderBy(t => html.IndexOf(t, StringComparison.Ordinal))
            .ToList();

    // Seeds all seven with distinct conversion times, oldest first, so the
    // default newest-first sort is a strict reversal and page boundaries are
    // unambiguous.
    private static async Task<string> GetPagedAsync(string query, int perPage,
        Action<WebApplicationFactory<Program>, EpubCache>? extra = null)
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(PagedStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<EpubCache>();
        for (var i = 1; i <= 7; i++)
            SeedConverted(cache, $"p{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
        extra?.Invoke(factory, cache);
        var settings = (DeviceSettings.Default with { PerPage = perPage }).Serialize();
        return await (await client.SendAsync(Request(factory, "/converted" + query, settings))).Content.ReadAsStringAsync();
    }

    // Seed one cache file per item with an explicit conversion time.
    private static void SeedConverted(EpubCache cache, string itemId, DateTime convertedAtUtc)
    {
        var p = cache.PathFor(itemId, Size, Mtime, W, H, spread: DeviceSettings.Default.Spread, scale: DeviceSettings.Default.Scale);
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

    // b2 converted most recently, then c3, then a1 - deliberately not the
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
        // Alpha #1 = Zebra Tales, Alpha #2 = Middle Road, Apple Days has no series.
        var html = await GetConvertedAsync("?sort=series", Seed());
        Assert.Equal(new[] { "Zebra Tales", "Middle Road", "Apple Days" }, TitleOrder(html));
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
    public async Task Descending_series_reverses_the_unseried_grouping_too()
    {
        // ACCEPTED BEHAVIOUR, pinned deliberately. `desc` reverses the whole list,
        // so "unseried last" inverts and Apple Days (no series) leads. Keeping it
        // last in both directions would need per-key ordering instead of one
        // Reverse(); the owner judged that not worth the branching. This test
        // exists so a later "fix" is a conscious change, not a silent one.
        var html = await GetConvertedAsync("?sort=series&desc=1", Seed());
        Assert.Equal(new[] { "Apple Days", "Middle Road", "Zebra Tales" }, TitleOrder(html));
    }

    [Fact]
    public async Task Renders_a_sortbar_with_the_active_field_marked()
    {
        var html = await GetConvertedAsync("?sort=title", Seed());

        Assert.Contains("class=\"sortbar\"", html);
        Assert.Contains("/converted?sort=converted&amp;desc=1", html);   // default view link
        Assert.Contains("/converted?sort=series", html);
        Assert.Contains("/converted?sort=author", html);
        // Title is active and ascending, so its own link flips to descending
        // and it carries the ascending arrow. Razor HTML-encodes the ↑ (U+2191)
        // in text content, same as elsewhere in this suite (e.g. the "✓ Read" button).
        Assert.Contains("/converted?sort=title&amp;desc=1", html);
        Assert.Contains("&#x2191;", html);
    }

    [Fact]
    public async Task Default_view_shows_the_applied_descending_arrow_not_the_query_direction()
    {
        // No `sort` param: query-direction Desc is false, but the page actually
        // applies descending (newest-first). The arrow must reflect what's on
        // screen, not the unset query value.
        var html = await GetConvertedAsync("", Seed());
        Assert.Contains("&#x2193;", html);
    }

    [Fact]
    public async Task Default_views_converted_link_toggles_to_ascending_not_desc_again()
    {
        // The applied direction is already descending, so clicking "Converted"
        // from the default view must offer the OTHER direction (no &desc=1) -
        // not re-request the descending order already shown.
        var html = await GetConvertedAsync("", Seed());
        Assert.Contains("/converted?sort=converted\"", html);
        Assert.DoesNotContain("/converted?sort=converted&amp;desc=1", html);
    }

    [Fact]
    public async Task A_garbage_sort_value_renders_the_same_arrow_as_the_default_view()
    {
        var html = await GetConvertedAsync("?sort=../etc/passwd", Seed());
        Assert.Contains("&#x2193;", html);
    }

    [Fact]
    public async Task Conversion_order_ignores_the_source_mtime_in_the_filename()
    {
        // THE TRAP. CachedVariant.MtimeMs is the SOURCE ebook's mtime, not the
        // conversion time, and it sits right next to the field we want. The two
        // assertions below prove the premise that makes the ordering assertion
        // meaningful: all three fixture files really do carry one identical,
        // shared mtimeMs even though their write times (the seeded conversion
        // times) differ, so a mix-up that sorts on MtimeMs instead has no write-time
        // signal left to fall back on.
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

    [Fact]
    public async Task Slices_to_the_configured_page_size()
    {
        // Sorted by title so the expected contents of each page are obvious and
        // independent of conversion times.
        var page1 = await GetPagedAsync("?sort=title", perPage: 5);

        Assert.Equal(
            new[] { "Paged 01", "Paged 02", "Paged 03", "Paged 04", "Paged 05" },
            PagedOrder(page1));
        Assert.Contains("Page 1 of 2", page1);
    }

    [Fact]
    public async Task The_second_page_shows_the_remainder()
    {
        var page2 = await GetPagedAsync("?sort=title&page=2", perPage: 5);

        Assert.Equal(new[] { "Paged 06", "Paged 07" }, PagedOrder(page2));
        Assert.Contains("Page 2 of 2", page2);
    }

    [Fact]
    public async Task A_page_beyond_the_end_clamps_to_the_last_page()
    {
        // Clamps rather than rendering an empty list: this list is sliced
        // locally, so an out-of-range page is ours to correct.
        var far = await GetPagedAsync("?sort=title&page=99", perPage: 5);

        Assert.Equal(new[] { "Paged 06", "Paged 07" }, PagedOrder(far));
        Assert.Contains("Page 2 of 2", far);
    }

    [Fact]
    public async Task A_page_below_one_reads_as_the_first_page()
    {
        var low = await GetPagedAsync("?sort=title&page=0", perPage: 5);

        Assert.Equal(
            new[] { "Paged 01", "Paged 02", "Paged 03", "Paged 04", "Paged 05" },
            PagedOrder(low));
        Assert.Contains("Page 1 of 2", low);
    }

    [Fact]
    public async Task A_larger_page_size_fits_everything_on_one_page()
    {
        var all = await GetPagedAsync("?sort=title", perPage: 10);

        Assert.Equal(7, PagedOrder(all).Count);
        Assert.Contains("Page 1 of 1", all);
    }

    [Fact]
    public async Task The_pager_hrefs_keep_the_active_sort_and_direction()
    {
        var html = await GetPagedAsync("?sort=title&desc=1", perPage: 5);

        // The pager is present and its next-page link carries the view's own
        // (explicit) sort and direction, so paging does not silently reset the
        // list to the default order. Razor HTML-encodes `&` in attribute values.
        Assert.Contains("class=\"pager\"", html);
        Assert.Contains("href=\"/converted?sort=title&amp;desc=1&amp;page=2\"", html);
    }

    [Fact]
    public async Task The_pager_hrefs_carry_the_applied_sort_on_the_default_view()
    {
        // No `sort` query param at all: the raw Sort is null and the raw Desc is
        // false, but the page applies converted/descending. The pager must carry
        // the APPLIED values, not the raw ones - this is the case where the two
        // diverge, so it's the only one that would catch a regression to raw.
        var html = await GetPagedAsync("", perPage: 5);

        Assert.Contains("href=\"/converted?sort=converted&amp;desc=1&amp;page=2\"", html);
    }

    [Fact]
    public async Task A_row_return_url_carries_the_current_page_and_sort()
    {
        // Regression for the read button's #item- anchor going stale under
        // paging: the row's return URL must be the exact request URL (page and
        // sort included), not a bare "/converted", or a no-JS read toggle on
        // page 2 lands back on page 1 with a fragment naming a row that is not
        // on the page it redirected to.
        var html = await GetPagedAsync("?sort=title&page=2", perPage: 5);

        Assert.Contains(
            "name=\"return\" value=\"/converted?sort=title&amp;page=2#item-p6\"",
            html);
    }

    // Pins the ORDERING of the work, which is the whole point of this task. If
    // rows are built before the slice, all seven rows mint their tickets and the
    // two counts come out equal.
    [Fact]
    public async Task Mints_tickets_only_for_the_rows_it_renders()
    {
        static async Task<int> LiveAfterAsync(int perPage)
        {
            using var cacheDir = new TempDir();
            using var keysDir = new TempDir();
            using var factory = CreateFactory(PagedStub(), cacheDir.Path, keysDir.Path);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var cache = factory.Services.GetRequiredService<EpubCache>();
            for (var i = 1; i <= 7; i++)
                SeedConverted(cache, $"p{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
            var settings = (DeviceSettings.Default with { PerPage = perPage }).Serialize();
            await client.SendAsync(Request(factory, "/converted?sort=title", settings));
            return factory.Services.GetRequiredService<DownloadTickets>().LiveCount;
        }

        var five = await LiveAfterAsync(5);
        var ten = await LiveAfterAsync(10);

        // Two tickets per rendered row (EPUB and raw): 5 rows -> 10, 7 rows -> 14.
        Assert.Equal(10, five);
        Assert.Equal(14, ten);
    }

    // Pins the deliberate exception: convert state is resolved for EVERY item,
    // not just the page's, so a conversion running on page 2 still refreshes
    // page 1. An item on this page resolves to Converting only when its source
    // changed since the conversion, because ConvertQueue.Status answers Done
    // whenever the cache file exists. Deleting the seeded p7 file (the brief's
    // first idea) does not work here: EpubCache.ListVariants() re-reads the
    // cache directory on every call, so a deleted p7 drops out of convertedAt
    // entirely and is never fetched at all - it would not even land on page 2,
    // so the test would prove nothing. Instead, p7's cache file is seeded at the
    // size the batch stub reports for every OTHER item (Size), but the stub
    // reports p7's OWN ebookFile at a different size, simulating its source
    // having changed since that conversion. The resolver keys off the batch
    // size, so it looks up a cache path that does not exist, and the queue
    // entry is enqueued against exactly that path.
    [Fact]
    public async Task A_conversion_on_a_later_page_still_refreshes_this_page()
    {
        const long ChangedSize = Size + 1;
        var batchJson = PagedBatchJson.Replace(
            $"\"filename\":\"p7.cbz\",\"size\":{Size}",
            $"\"filename\":\"p7.cbz\",\"size\":{ChangedSize}");
        Assert.NotEqual(PagedBatchJson, batchJson); // the replace actually matched

        var stub = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(batchJson);
            if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
            if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(stub, cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<EpubCache>();
        for (var i = 1; i <= 7; i++)
            SeedConverted(cache, $"p{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));

        // p7 is last by title, so it is on page 2 while we render page 1. The
        // queue entry targets the path the resolver computes from the batch's
        // (changed) size, which was never seeded, so Status answers not-Done.
        var target = DeviceSettings.Default.ToRenderTarget($"{W}x{H}x1");
        var pending = cache.PathFor("p7", ChangedSize, Mtime, target.MaxW, target.MaxH,
            target.Grayscale, target.Spread, target.Scale, target.Dpr, target.Upscale);
        factory.Services.GetRequiredService<ConvertQueue>().Enqueue(new ConvertJob(
            "p7", "tok", pending, new EbookMeta("T", "A", null, null, "p7"), target));

        var settings = (DeviceSettings.Default with { PerPage = 5 }).Serialize();
        var page1 = await (await client.SendAsync(Request(factory, "/converted?sort=title", settings))).Content.ReadAsStringAsync();

        // p7 is not on this page...
        Assert.DoesNotContain("Paged 07", page1);
        // ...but its conversion still arms the refresh.
        Assert.Contains("http-equiv=\"refresh\"", page1);
    }

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

    // Same as Request, but carries a device id so download marks resolve.
    private static HttpRequestMessage RequestAs(WebApplicationFactory<Program> factory, string url, string did)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; scr={W}x{H}x1; "
            + $"inkshelf_settings=retina=0&gray=0&lang=&fav=&did={did}");
        return req;
    }

    private static HttpRequestMessage Request(WebApplicationFactory<Program> factory, string url, string? settings = null)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var cookie = $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; scr={W}x{H}x1";
        if (settings is not null) cookie += $"; inkshelf_settings={settings}";
        req.Headers.Add("Cookie", cookie);
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
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H, spread: DeviceSettings.Default.Spread, scale: DeviceSettings.Default.Scale), "epub"); // matches the request's device target

        var html = await (await client.SendAsync(Request(factory, "/converted"))).Content.ReadAsStringAsync();

        Assert.Contains("My Comic", html);
        // Cached state, keyed on the title only that branch renders - a bare ">EPUB"
        // would also match a raw epub file's format label.
        Assert.Contains("title=\"Already converted", html);      // cached state (current ebook)
        Assert.Contains($"/library/{LibId}?filter=", html);     // series/author link into the item's library

        // Both hrefs are re-requested by a cookie-less download manager (issue #40),
        // so each must carry a ticket - not just the listing's rows.
        Assert.Matches($"href=\"/download/{ItemId}\\?t=[A-Za-z0-9_-]{{22}}\"", html);
        Assert.Matches($"href=\"/convert/{ItemId}\\?return=[^\"]*&amp;t=[A-Za-z0-9_-]{{22}}\"", html);
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
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H, grayscale: true, spread: DeviceSettings.Default.Spread), "epub");

        // Request carries no settings cookie → colour target → the "-g" variant
        // doesn't match, so the page is empty.
        var html = await (await client.SendAsync(Request(factory, "/converted"))).Content.ReadAsStringAsync();
        Assert.Contains("Nothing converted for this device yet.", html);
    }

    [Fact]
    public async Task A_variant_cached_at_a_different_dpr_is_not_listed_for_this_device()
    {
        // The device's target has Dpr 1 (the request's "scr" cookie carries no
        // retina/override, so FromCookie returns Dpr 1). A cache file that differs
        // from the target ONLY in Dpr must not be treated as a match - otherwise a
        // device would be served a variant sized for a different pixel ratio.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(
            cache.PathFor(ItemId, Size, Mtime, W, H, spread: DeviceSettings.Default.Spread, scale: DeviceSettings.Default.Scale, dpr: 2),
            "epub");

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
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H, spread: DeviceSettings.Default.Spread, scale: DeviceSettings.Default.Scale), "epub"); // non-empty → batch is attempted

        var response = await client.SendAsync(Request(factory, "/converted"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Couldn&#x27;t load details", html);
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
        // The title icon is a home link, and the deployed version renders (a real
        // number, not the literal Razor expression - guards the v@Model email trap).
        Assert.Contains("<a href=\"/?all=1\" class=\"home-link\"", html);
        Assert.Matches(@"Inkshelf v\d+\.\d+", html);
        Assert.DoesNotContain("@Model", html);
    }

    [Fact]
    public async Task An_epub_mark_puts_the_arrow_on_the_EPUB_action_not_on_Download()
    {
        // /converted has its own raw/epub flag wiring, so swapping the two there
        // would go unnoticed by the listing's and item page's tests.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        const string did = "abc123def4560000";
        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H, spread: DeviceSettings.Default.Spread, scale: DeviceSettings.Default.Scale), "epub");
        factory.Services.GetRequiredService<DownloadMarks>()
            .Add(did, DownloadMarks.EpubKey(ItemId, null));

        var html = await (await client.SendAsync(RequestAs(factory, "/converted", did))).Content.ReadAsStringAsync();

        Assert.Contains("EPUB &#8595;", html);          // the converted file is marked
        Assert.DoesNotContain("Download &#8595;", html); // the raw ebook is not
    }
}
