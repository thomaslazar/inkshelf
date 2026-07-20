using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertServiceTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "svc-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static AbsApiClient DetailClient(string detailJson) =>
        new(new HttpClient(new StubHandler(_ => StubHandler.Json(detailJson)))
        { BaseAddress = new Uri("http://abs.local") });

    private static string DetailJson(string format, string title, string author, long size, long mtime) =>
        $$"""
        {"media":{"metadata":{"title":"{{title}}","authorName":"{{author}}"},
         "ebookFile":{"ebookFormat":"{{format}}","metadata":{"filename":"x.{{format}}","size":{{size}},"mtimeMs":{{mtime}} } } } }
        """;

    // A TokenStore backed by an HttpContext carrying a valid session cookie.
    private static TokenStore TokenStoreWith(string access)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var opts = new AbsOptions();
        var store = new TokenStore(dp, accessor, opts);
        store.Save(new Tokens(access, "refresh"));
        // Save wrote the cookie to the RESPONSE; copy it onto the REQUEST so Read() sees it.
        var setCookie = accessor.HttpContext!.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';')[0].Split('=', 2)[1];
        accessor.HttpContext!.Request.Headers.Cookie = $"inkshelf_session={value}";
        return store;
    }

    private static ConvertService Service(AbsApiClient api, EpubCache cache, ConvertQueue queue, TokenStore store) =>
        new(api, cache, queue, store, NullLogger<ConvertService>.Instance);

    [Fact]
    public async Task KickAsync_returns_None_for_non_comic()
    {
        using var dir = new TempDir();
        var svc = Service(DetailClient(DetailJson("epub", "T", "A", 1, 2)),
            new EpubCache(dir.Path), new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.KickAsync("item1", fresh: false, new RenderTarget(100, 200, 1.0, false), default);
        Assert.Equal(ConvertStatus.None, r.Status);
    }

    [Fact]
    public async Task KickAsync_returns_Done_with_name_when_cached()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(cache.PathFor("item1", 123, 456, 100, 200), "epub");
        var svc = Service(DetailClient(DetailJson("cbz", "My: Comic", "Jane Doe", 123, 456)),
            cache, new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.KickAsync("item1", fresh: false, new RenderTarget(100, 200, 1.0, false), default);
        Assert.Equal(ConvertStatus.Done, r.Status);
        Assert.StartsWith("Jane Doe - My", r.DownloadName);
        Assert.EndsWith(".epub", r.DownloadName);
    }

    [Fact]
    public async Task KickAsync_touches_cached_file_on_serve()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var path = cache.PathFor("item1", 123, 456, 100, 200);
        File.WriteAllText(path, "epub");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var svc = Service(DetailClient(DetailJson("cbz", "My: Comic", "Jane Doe", 123, 456)),
            cache, new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.KickAsync("item1", fresh: false, new RenderTarget(100, 200, 1.0, false), default);
        Assert.Equal(ConvertStatus.Done, r.Status);
        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task KickAsync_enqueues_a_job_carrying_the_token_on_miss()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 123, 456)),
            cache, queue, TokenStoreWith("TOKEN123"));
        var r = await svc.KickAsync("item1", fresh: false, new RenderTarget(100, 200, 1.0, false), default);
        Assert.Equal(ConvertStatus.Queued, r.Status);
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal("TOKEN123", job!.AccessToken);
        Assert.Equal(cache.PathFor("item1", 123, 456, 100, 200), job.CachePath);
    }

    [Fact]
    public async Task StatusAsync_reports_none_before_any_kick()
    {
        using var dir = new TempDir();
        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 123, 456)),
            new EpubCache(dir.Path), new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.StatusAsync("item1", new RenderTarget(100, 200, 1.0, false), default);
        Assert.Equal(ConvertStatus.None, r.Status);
    }
}
