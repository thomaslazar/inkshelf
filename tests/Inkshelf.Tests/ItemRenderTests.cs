using System.Net;
using System.Text.RegularExpressions;
using Inkshelf;
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
    private const long SSize = 222, SMtime = 333;     // secondary (non-primary) cbz, ino "3"
    private const int W = 375, H = 812;

    private static string DetailJson() => $$"""
        {"libraryId":"{{LibId}}","libraryFiles":[{"ino":"1","fileType":"ebook","metadata":{"filename":"My Comic.cbz","ext":".cbz","size":{{PSize}},"mtimeMs":{{PMtime}} } },{"ino":"2","fileType":"ebook","metadata":{"filename":"My Comic.pdf","ext":".pdf","size":50,"mtimeMs":60 } },{"ino":"3","fileType":"ebook","metadata":{"filename":"My Comic 2.cbz","ext":".cbz","size":{{SSize}},"mtimeMs":{{SMtime}} } }],"media":{"coverPath":"/c.jpg","tags":["owned"],"ebookFile":{"ino":"1","ebookFormat":"cbz","metadata":{"filename":"My Comic.cbz","size":{{PSize}},"mtimeMs":{{PMtime}} } },"metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"},{"id":"a2","name":"Author Two"}],"series":[{"id":"s1","name":"The Sandman","sequence":"3"}],"narrators":["Nar A"],"genres":["Fantasy"],"descriptionPlain":"A plain description."} } }
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
        // The cached state, discriminated by the title only that branch renders —
        // NOT by a bare ">EPUB", which the file-format span also emits for a raw epub.
        Assert.Contains("title=\"Already converted", html);          // primary cbz cached (shared key)
        Assert.Contains($"action=\"/read/{ItemId}\"", html);         // read toggle
    }

    [Fact]
    public async Task An_epub_mark_on_the_primary_file_does_not_light_up_a_non_primary_row()
    {
        // The primary ebook's mark key uses a null ino; a non-primary file's mark
        // uses its own ino (Item.cshtml.cs's `keyIno = isPrimary ? null : f.Ino`).
        // Both files are cached (Cached state) so each row's convert action renders
        // "EPUB"; only the per-file mark should decide which one gets the arrow.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, PSize, PMtime, W, H), "epub"); // primary (ino "1")
        File.WriteAllText(cache.PathFor(ItemId, SSize, SMtime, W, H), "epub"); // non-primary (ino "3")

        const string did = "abc123def4560000";
        factory.Services.GetRequiredService<DownloadMarks>()
            .Add(did, DownloadMarks.EpubKey(ItemId, null)); // marks the PRIMARY's EPUB key only
        // Also mark the primary's RAW key, so the same request exercises the raw
        // Download arrow's per-file mapping (Item.cshtml.cs's `RawKey(Id, keyIno)`)
        // alongside the EPUB one — a single shared mistake in `keyIno` would break both.
        factory.Services.GetRequiredService<DownloadMarks>()
            .Add(did, DownloadMarks.RawKey(ItemId, null));

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/item/{ItemId}");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; "
            + $"scr={W}x{H}x1; inkshelf_settings=retina=0&gray=0&lang=&fav=&did={did}");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        var primary = Regex.Match(html, $"<a [^>]*href=\"/convert/{ItemId}\\?return=[^\"]*\"[^>]*>([^<]*)</a>");
        Assert.True(primary.Success, "Expected the primary file's convert anchor.");
        Assert.Contains("&#8595;", primary.Groups[1].Value);

        var secondary = Regex.Match(html, $"<a [^>]*href=\"/convert/{ItemId}\\?file=3&amp;return=[^\"]*\"[^>]*>([^<]*)</a>");
        Assert.True(secondary.Success, "Expected the non-primary file's convert anchor.");
        Assert.DoesNotContain("&#8595;", secondary.Groups[1].Value);

        // Raw Download arrow: primary (no file=) shows it, the pdf's (file=2) does not.
        var primaryDownload = Regex.Match(html, $"<a [^>]*href=\"/download/{ItemId}\">([^<]*)</a>");
        Assert.True(primaryDownload.Success, "Expected the primary file's download anchor.");
        Assert.Contains("&#8595;", primaryDownload.Groups[1].Value);

        var secondaryDownload = Regex.Match(html, $"<a [^>]*href=\"/download/{ItemId}\\?file=2\">([^<]*)</a>");
        Assert.True(secondaryDownload.Success, "Expected the non-primary file's download anchor.");
        Assert.DoesNotContain("&#8595;", secondaryDownload.Groups[1].Value);
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

    // The item page is the ONLY place regen is offered (listing rows dropped it —
    // too small a target next to Convert). The old regression still applies here:
    // the ↻ anchor must stay a PLAIN link. Give it data-warm and the poll script
    // overwrites its glyph with status text, producing a duplicate "EPUB".
    [Fact]
    public async Task Item_page_offers_regen_and_it_stays_a_plain_link()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(Request(factory, $"/item/{ItemId}"))).Content.ReadAsStringAsync();

        var regen = Regex.Match(html, "<a class=\"btn regen\"[^>]*>");
        Assert.True(regen.Success, "Expected a regen anchor on the item page.");
        Assert.Contains("fresh=1", regen.Value);
        Assert.DoesNotContain("data-warm", regen.Value);
    }
}
