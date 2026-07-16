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
        {"libraryItems":[{"id":"{{ItemId}}","media":{"metadata":{"authors":[{"id":"a1","name":"Author One"}],"series":[]},
         "ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } }]}
        """;

    private const string LibrariesJson = """{"libraries":[{"id":"lib1","name":"Test Library","mediaType":"book"}]}""";

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
        if (path == $"/api/libraries/{LibId}/items") return StubHandler.Json(ItemsJson());
        if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(BatchJson());
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static WebApplicationFactory<Program> CreateFactory(StubHandler stub, string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub)));
        });

    private static HttpRequestMessage LibraryRequest(WebApplicationFactory<Program> factory)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var sessionCookie = protector.Protect("access\nrefresh");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/library/{LibId}");
        req.Headers.Add("Cookie", $"inkshelf_session={Uri.EscapeDataString(sessionCookie)}; scr={W}x{H}x1");
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
        queue.Enqueue(new ConvertJob(ItemId, "tok", path, new EbookMeta("T", "A", null, null, ItemId), W, H, 1.0));

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
}
