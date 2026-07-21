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
        {"libraryId":"{{LibId}}","libraryFiles":[{"ino":"1","fileType":"ebook","metadata":{"filename":"My Comic.cbz","ext":".cbz","size":{{PSize}},"mtimeMs":{{PMtime}} } },{"ino":"2","fileType":"ebook","metadata":{"filename":"My Comic.pdf","ext":".pdf","size":50,"mtimeMs":60 } }],"media":{"coverPath":"/c.jpg","tags":["owned"],"ebookFile":{"ino":"1","ebookFormat":"cbz","metadata":{"filename":"My Comic.cbz","size":{{PSize}},"mtimeMs":{{PMtime}} } },"metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"},{"id":"a2","name":"Author Two"}],"series":[{"id":"s1","name":"The Sandman","sequence":"3"}],"narrators":["Nar A"],"genres":["Fantasy"],"descriptionPlain":"A plain description."} } }
        """;

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/items/{ItemId}") return StubHandler.Json(DetailJson());
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        if (path == "/api/libraries") return StubHandler.Json("""{"libraries":[{"id":"lib1","name":"Test Library","mediaType":"book"}]}""");
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
    public async Task Breadcrumb_shows_the_actual_library_between_libraries_and_title()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(Request(factory, $"/item/{ItemId}"))).Content.ReadAsStringAsync();

        // Libraries › <actual library, links to the listing> › <book title>
        Assert.Contains($"href=\"/library/{LibId}\">Test Library</a>", html);
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
