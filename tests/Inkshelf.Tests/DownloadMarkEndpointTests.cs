using System.Net;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

// The download endpoints must record a per-device mark. Needs a stubbed ABS (the
// endpoint 404s without one, so nothing would be marked) and a temp CachePath (so
// marks don't land in the repo).
public class DownloadMarkEndpointTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dlmark-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string ItemId = "item1";
    private const string Did = "abc123def4560000";

    private const string Ino = "9999";

    // Expanded item detail: one primary epub file, plus a second ebook in
    // libraryFiles so the `?file={ino}` branch has something to resolve.
    private const string DetailJson = $$"""
        {"media":{"metadata":{"title":"A Book","authorName":"An Author"},
         "ebookFile":{"ebookFormat":"epub","metadata":{"filename":"a.epub","size":10,"mtimeMs":20} } },
         "libraryFiles":[{"ino":"{{Ino}}","fileType":"ebook","metadata":{"filename":"b.epub","size":11,"mtimeMs":21} }]}
        """;

    // A separate cbz item for the /convert/{id} tests — the /download endpoint's
    // fixture above is an epub (not convertible).
    private const string ComicId = "comic1";
    private const long CSize = 555, CMtime = 666;
    private static string ComicDetailJson() => $$"""
        {"media":{"metadata":{"title":"A Comic","authorName":"An Author"},
         "ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"a.cbz","size":{{CSize}},"mtimeMs":{{CMtime}} } } } }
        """;

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/items/{ItemId}") return StubHandler.Json(DetailJson);
        if (path == $"/api/items/{ComicId}") return StubHandler.Json(ComicDetailJson());
        if (path == $"/api/items/{ItemId}/ebook" || path == $"/api/items/{ItemId}/ebook/{Ino}")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("epub-bytes"u8.ToArray()) };
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static WebApplicationFactory<Program> CreateFactory(string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = MakeStub()));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    private static HttpRequestMessage Download(WebApplicationFactory<Program> factory, string? did)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/download/{ItemId}");
        var cookie = $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}";
        if (did is not null) cookie += $"; inkshelf_settings=retina=1&gray=0&lang=&fav=&did={did}";
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    [Fact]
    public async Task A_download_records_a_raw_mark_and_not_an_epub_mark()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.SendAsync(Download(factory, Did));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var marks = factory.Services.GetRequiredService<DownloadMarks>().Read(Did);
        Assert.Contains(DownloadMarks.RawKey(ItemId, null), marks);
        Assert.DoesNotContain(DownloadMarks.EpubKey(ItemId, null), marks);
    }

    [Fact]
    public async Task A_download_from_a_device_with_no_id_mints_one_and_marks_it()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.SendAsync(Download(factory, did: null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var setCookie = string.Join(" ", res.Headers.TryGetValues("Set-Cookie", out var v) ? v : Array.Empty<string>());
        var minted = System.Text.RegularExpressions.Regex.Match(setCookie, "did%3D([0-9a-f]{16})").Groups[1].Value;
        Assert.NotEqual("", minted);

        Assert.Contains(DownloadMarks.RawKey(ItemId, null),
            factory.Services.GetRequiredService<DownloadMarks>().Read(minted));
    }

    [Fact]
    public async Task A_failed_download_records_nothing()
    {
        // Unknown item → the endpoint 404s before serving, so there is no file to
        // have downloaded and nothing should be marked.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, "/download/nope");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang=&fav=&did={Did}");
        await client.SendAsync(req);

        Assert.Empty(factory.Services.GetRequiredService<DownloadMarks>().Read(Did));
    }

    [Fact]
    public async Task A_per_file_download_marks_that_file_and_not_the_primary()
    {
        // The ino is what separates "I pulled this item's primary ebook" from
        // "I pulled the second file in it". Swapping them would mark the wrong row
        // action, and only an end-to-end request exercises that wiring.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/download/{ItemId}?file={Ino}");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang=&fav=&did={Did}");

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var marks = factory.Services.GetRequiredService<DownloadMarks>().Read(Did);
        Assert.Contains(DownloadMarks.RawKey(ItemId, Ino), marks);
        Assert.DoesNotContain(DownloadMarks.RawKey(ItemId, null), marks);
    }

    [Fact]
    public async Task Converted_epub_download_records_an_epub_mark_and_not_a_raw_one()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Pre-seed the cached EPUB for this device's render target. No `scr`
        // cookie is sent (mirrors Download()'s request), so ScreenTarget.FromCookie
        // resolves to (0,0,1,false) — the same target the convert endpoint computes
        // — and KickAsync sees ConvertStatus.Done, serving + marking immediately
        // instead of only enqueuing a background job.
        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ComicId, CSize, CMtime, 0, 0, spread: DeviceSettings.Default.Spread), "epub");

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/convert/{ComicId}?return=/");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang=&fav=&did={Did}");

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var marks = factory.Services.GetRequiredService<DownloadMarks>().Read(Did);
        Assert.Contains(DownloadMarks.EpubKey(ComicId, null), marks);
        Assert.DoesNotContain(DownloadMarks.RawKey(ComicId, null), marks);
    }
}
