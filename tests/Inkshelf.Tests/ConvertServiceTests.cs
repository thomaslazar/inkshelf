using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertServiceTests
{
    // A stub AbsClient whose item-detail GET returns the given JSON.
    private static AbsClient DetailClient(string detailJson) =>
        new(new HttpClient(new StubHandler(_ => StubHandler.Json(detailJson)))
        { BaseAddress = new Uri("http://abs.local") });

    private static AbsSession SessionWithToken(AbsClient client)
    {
        // Write a session cookie on one context, replay it into a request context.
        var w = new DefaultHttpContext();
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        new TokenStore(dp, new HttpContextAccessor { HttpContext = w }).Save(new Tokens("acc", "ref"));
        var value = w.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"inkshelf_session={value}";
        var store = new TokenStore(dp, new HttpContextAccessor { HttpContext = ctx });
        return new AbsSession(store, client);
    }

    private static ConvertService Service(AbsClient client, EpubCache cache) =>
        new(SessionWithToken(client), client, cache, new EpubConverter(),
            NullLogger<ConvertService>.Instance);

    private static string DetailJson(string format, string title, string author, long size, long mtime)
    {
        var json = $@"{{
            ""media"": {{
                ""metadata"": {{
                    ""title"": ""{title}"",
                    ""authorName"": ""{author}""
                }},
                ""ebookFile"": {{
                    ""ebookFormat"": ""{format}"",
                    ""metadata"": {{
                        ""filename"": ""x.{format}"",
                        ""size"": {size},
                        ""mtimeMs"": {mtime}
                    }}
                }}
            }}
        }}";
        return json;
    }

    [Fact]
    public async Task ConvertAsync_returns_NotFound_for_non_comic_format()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var svc = Service(DetailClient(DetailJson("epub", "T", "A", 1, 2)), cache);

        var outcome = await svc.ConvertAsync("item1", fresh: false, warm: false, 100, 200, 1.0, default);

        Assert.Equal(ConvertResultKind.NotFound, outcome.Kind);
    }

    [Fact]
    public async Task ConvertAsync_returns_File_with_sanitized_name_when_cached()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        // Pre-create the cache file so no real conversion runs.
        File.WriteAllText(cache.PathFor("item1", 123, 456, 100, 200), "epub-bytes");
        var svc = Service(DetailClient(DetailJson("cbz", "My: Comic", "Jane Doe", 123, 456)), cache);

        var outcome = await svc.ConvertAsync("item1", fresh: false, warm: false, 100, 200, 1.0, default);

        Assert.Equal(ConvertResultKind.File, outcome.Kind);
        // ':' is an invalid file-name char on some platforms; on Linux it is legal,
        // so assert the stable part and the extension.
        Assert.StartsWith("Jane Doe - My", outcome.DownloadName);
        Assert.EndsWith(".epub", outcome.DownloadName);
    }

    [Fact]
    public async Task ConvertAsync_returns_Warmed_when_cached_and_warm_requested()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(cache.PathFor("item1", 123, 456, 100, 200), "epub-bytes");
        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 123, 456)), cache);

        var outcome = await svc.ConvertAsync("item1", fresh: false, warm: true, 100, 200, 1.0, default);

        Assert.Equal(ConvertResultKind.Warmed, outcome.Kind);
    }

    // Self-cleaning temp directory.
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "inkshelf-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
