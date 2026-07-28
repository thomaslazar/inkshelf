using System.Net;
using Inkshelf;
using Inkshelf.Abs;
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

    // Expanded item detail with one primary epub file.
    private const string DetailJson = """
        {"media":{"metadata":{"title":"A Book","authorName":"An Author"},
         "ebookFile":{"ebookFormat":"epub","metadata":{"filename":"a.epub","size":10,"mtimeMs":20} } } }
        """;

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/items/{ItemId}") return StubHandler.Json(DetailJson);
        if (path == $"/api/items/{ItemId}/ebook")
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
}
