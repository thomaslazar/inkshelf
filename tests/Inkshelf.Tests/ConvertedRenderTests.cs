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
        // from the default view must offer the OTHER direction (no &desc=1) —
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
        // Cached state, keyed on the title only that branch renders — a bare ">EPUB"
        // would also match a raw epub file's format label.
        Assert.Contains("title=\"Already converted", html);      // cached state (current ebook)
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
        // number, not the literal Razor expression — guards the v@Model email trap).
        Assert.Contains("<a href=\"/?all=1\" class=\"home-link\"", html);
        Assert.Matches(@"Inkshelf v\d+\.\d+", html);
        Assert.DoesNotContain("@Model", html);
    }
}
