using System.Net;
using System.Text.RegularExpressions;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

// Renders a real /library/{id} response end-to-end (WebApplicationFactory +
// stubbed ABS) and asserts the per-row convert markup. Locks down a real
// regression: the regen "↻" anchor must stay a PLAIN link (no data-warm) or
// the poll script overwrites its glyph, producing a duplicate "EPUB" row.
public class ListingRenderTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "listing-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string LibId = "lib1";
    private const string ItemId = "item1";
    private const long Size = 12345;
    private const long Mtime = 67890;
    private const int W = 375;
    private const int H = 812;

    private static string ItemsJson() => $$"""
        {"results":[{"id":"{{ItemId}}","media":{"metadata":{"title":"My Comic"},"ebookFormat":"cbz"} }],"total":1,"limit":10,"page":0}
        """;

    private static string BatchJson() => $$"""
        {"libraryItems":[{"id":"{{ItemId}}","media":{"metadata":{"authors":[{"id":"a1","name":"Author One"}],"series":[{"id":"s1","name":"The Sandman"} ]},
         "ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } }]}
        """;

    // A search hit for the same cbz item. Search uses ABS's EXPANDED item JSON,
    // which carries the full ebookFile (format is at media.ebookFile.ebookFormat)
    // but — unlike the minified listing — NO top-level media.ebookFormat.
    private static string SearchJson() => $$"""
        {"book":[{"libraryItem":{"id":"{{ItemId}}","media":{"metadata":{"title":"My Comic"},"ebookFile":{"ebookFormat":"cbz"} } } }],"series":[],"authors":[]}
        """;

    private const string LibrariesJson = """{"libraries":[{"id":"lib1","name":"Test Library","mediaType":"book"}]}""";

    private static StubHandler MakeStub(string? meJson = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
        if (path == $"/api/libraries/{LibId}/items") return StubHandler.Json(ItemsJson());
        if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(BatchJson());
        if (path == $"/api/libraries/{LibId}/search") return StubHandler.Json(SearchJson());
        if (path == "/api/me") return StubHandler.Json(meJson ?? """{"mediaProgress":[]}""");
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
                // Drop the background ConvertWorker: these tests assert the RENDER
                // of a given queue state (a Queued row shows "Converting…"). The
                // real worker would drain the enqueued job and — with no stubbed
                // AbsDownloadClient — fail the download and mark it Failed, racing
                // the request and flaking the assertion.
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    // settings: raw "inkshelf_settings" cookie value (DeviceSettings.Serialize's
    // "<retina><grayscale>" digit pair, e.g. "01"), omitted when null.
    private static HttpRequestMessage LibraryRequest(WebApplicationFactory<Program> factory, string? settings = null)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var sessionCookie = protector.Protect("access\nrefresh");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/library/{LibId}");
        var cookie = $"inkshelf_session={Uri.EscapeDataString(sessionCookie)}; scr={W}x{H}x1";
        if (settings is not null) cookie += $"; inkshelf_settings={settings}";
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    // Isolate the `class="regen"` anchor so a whole-page DoesNotContain check
    // doesn't get tripped up by a converting row's primary anchor legitimately
    // carrying data-warm.
    private static string RegenAnchor(string html)
    {
        var match = Regex.Match(html, "<a class=\"regen\"[^>]*>");
        Assert.True(match.Success, "Expected a regen anchor in the rendered listing.");
        return match.Value;
    }

    // Isolate the row's PRIMARY convert anchor (the one before the regen ↻
    // link) — a whole-page DoesNotContain("data-warm") would false-fail on the
    // layout's own poll script, which references the attribute by name.
    private static string PrimaryConvertAnchor(string html)
    {
        var match = Regex.Match(html, $"<a href=\"/convert/{ItemId}\\?return=[^\"]*\"[^>]*>[^<]*</a>");
        Assert.True(match.Success, "Expected a primary convert anchor in the rendered listing.");
        return match.Value;
    }

    [Fact]
    public async Task Converting_row_has_data_warm_and_poll_and_noscript_refresh_but_regen_stays_plain()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var queue = factory.Services.GetRequiredService<ConvertQueue>();
        var cache = factory.Services.GetRequiredService<EpubCache>();
        var path = cache.PathFor(ItemId, Size, Mtime, W, H);
        queue.Enqueue(new ConvertJob(ItemId, "tok", path, new EbookMeta("T", "A", null, null, ItemId), new RenderTarget(W, H, 1.0, false)));

        var response = await client.SendAsync(LibraryRequest(factory));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true, "Expected Cache-Control: no-store.");

        Assert.Contains($"/convert/{ItemId}?return=", html);
        Assert.Contains("data-warm data-poll", html);
        Assert.Contains("Converting&#8230;", html);
        Assert.Contains("<noscript><meta http-equiv=\"refresh\" content=\"30\" /></noscript>", html);

        Assert.DoesNotContain("data-warm", RegenAnchor(html));
    }

    [Fact]
    public async Task Cached_row_renders_plain_epub_link_and_omits_refresh_and_regen_stays_plain()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        var path = cache.PathFor(ItemId, Size, Mtime, W, H);
        File.WriteAllText(path, "epub");

        var response = await client.SendAsync(LibraryRequest(factory));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true, "Expected Cache-Control: no-store.");

        Assert.Contains("EPUB &#10003;", html);
        Assert.DoesNotContain("data-warm", PrimaryConvertAnchor(html));
        Assert.DoesNotContain("<meta http-equiv=\"refresh\"", html);

        Assert.DoesNotContain("data-warm", RegenAnchor(html));
    }

    // Task 6: row-state must be keyed on the SAME RenderTarget (scr probe + the
    // inkshelf_settings cookie's grayscale flag) the real conversion uses — a
    // grayscale-variant cache file only counts as "converted" when the request
    // carries grayscale=on; the same file is not this request's cache path
    // otherwise, so the row must still offer plain "Convert".
    [Fact]
    public async Task Grayscale_cache_hit_is_converted_only_with_settings_cookie_on()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        var path = cache.PathFor(ItemId, Size, Mtime, W, H, grayscale: true);
        File.WriteAllText(path, "epub");

        // retina=0, grayscale=1 → target matches the pre-seeded "-g" file.
        var grayResponse = await client.SendAsync(LibraryRequest(factory, settings: "01"));
        var grayHtml = await grayResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, grayResponse.StatusCode);
        Assert.Contains("EPUB &#10003;", grayHtml);
        Assert.DoesNotContain("data-warm", PrimaryConvertAnchor(grayHtml));

        // No settings cookie → default (colour) target; the "-g" file isn't its
        // cache path, so the row is still plain "Convert".
        var colourResponse = await client.SendAsync(LibraryRequest(factory));
        var colourHtml = await colourResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, colourResponse.StatusCode);
        Assert.DoesNotContain("EPUB &#10003;", colourHtml);
        Assert.Contains("data-warm>Convert</a>", PrimaryConvertAnchor(colourHtml));
    }

    // Regression: search-result rows for a cbz/cbr must offer Convert, not just
    // Download. The search branch previously built ItemRowModel inline (State
    // defaulting to NotConvertible), suppressing the convert action.
    [Fact]
    public async Task Search_result_row_offers_convert_for_cbz()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory);
        req.RequestUri = new Uri($"/library/{LibId}?q=comic", UriKind.Relative);
        var response = await client.SendAsync(req);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Results for", html); // confirm we rendered the search branch
        Assert.Contains($"/convert/{ItemId}?return=", html);
        Assert.Contains("data-warm>Convert</a>", PrimaryConvertAnchor(html));
    }

    // Search rows fetch batch metadata too (hits are capped), so an already-
    // converted item shows the cached "EPUB ✓" state, not a plain "Convert".
    [Fact]
    public async Task Search_result_row_shows_cached_when_already_converted()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H), "epub");

        var req = LibraryRequest(factory);
        req.RequestUri = new Uri($"/library/{LibId}?q=comic", UriKind.Relative);
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.Contains("Results for", html);
        Assert.Contains("EPUB &#10003;", html);
        Assert.DoesNotContain("data-warm", PrimaryConvertAnchor(html));
    }

    [Fact]
    public async Task Unread_row_shows_mark_read_toggle()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        Assert.Contains($"action=\"/read/{ItemId}\"", html);
        Assert.Contains(">Mark read</button>", html);
        Assert.Contains("name=\"read\" value=\"1\"", html);
    }

    [Fact]
    public async Task Read_row_shows_checked_toggle_that_unmarks()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var me = $$"""{"mediaProgress":[{"libraryItemId":"{{ItemId}}","isFinished":true} ]}""";
        using var factory = CreateFactory(MakeStub(me), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        Assert.Contains("&#10003; Read</button>", html);        // "✓ Read" (entity-encoded in markup)
        Assert.Contains("name=\"read\" value=\"0\"", html);     // tapping unmarks
    }

    [Fact]
    public async Task Search_row_shows_read_toggle_too()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var me = $$"""{"mediaProgress":[{"libraryItemId":"{{ItemId}}","isFinished":true} ]}""";
        using var factory = CreateFactory(MakeStub(me), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory);
        req.RequestUri = new Uri($"/library/{LibId}?q=comic", UriKind.Relative);
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.Contains("Results for", html);
        Assert.Contains($"action=\"/read/{ItemId}\"", html);
        Assert.Contains("&#10003; Read</button>", html);
    }

    // A facet filter (?filter=series.<b64-id>, as the structured row links build)
    // must show the facet TYPE and the resolved NAME, not the literal "filter".
    [Fact]
    public async Task Filter_by_series_shows_type_and_resolved_name()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory);
        req.RequestUri = new Uri($"/library/{LibId}?filter=series.czE=", UriKind.Relative); // base64("s1")
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.Contains("Filtered by <strong>Series: The Sandman</strong>", html);
    }

    [Fact]
    public async Task Filter_by_author_shows_type_and_resolved_name()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory);
        req.RequestUri = new Uri($"/library/{LibId}?filter=authors.YTE=", UriKind.Relative); // base64("a1")
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.Contains("Filtered by <strong>Author: Author One</strong>", html);
    }

    // Nothing in the page matches the filtered id → fall back to just the type,
    // never the bare "filter".
    [Fact]
    public async Task Filter_with_unresolved_id_falls_back_to_type()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory);
        req.RequestUri = new Uri($"/library/{LibId}?filter=series.bm9wZQ==", UriKind.Relative); // base64("nope")
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.Contains("Filtered by <strong>Series</strong>", html);
        Assert.DoesNotContain(">filter</strong>", html);
    }

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
}
