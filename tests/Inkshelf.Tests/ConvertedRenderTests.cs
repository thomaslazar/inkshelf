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
