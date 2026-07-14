using Microsoft.Extensions.Logging.Abstractions;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertServiceTests
{
    private static AbsApiClient DetailClient(string detailJson) =>
        new(new HttpClient(new StubHandler(_ => StubHandler.Json(detailJson)))
        { BaseAddress = new Uri("http://abs.local") });

    private static ConvertService Service(AbsApiClient api, EpubCache cache) =>
        new(api, cache, new EpubConverter(), new AbsOptions { MaxCacheBytes = long.MaxValue },
            NullLogger<ConvertService>.Instance);

    private static string DetailJson(string format, string title, string author, long size, long mtime) =>
        $$"""
        {"media":{"metadata":{"title":"{{title}}","authorName":"{{author}}"},
         "ebookFile":{"ebookFormat":"{{format}}","metadata":{"filename":"x.{{format}}","size":{{size}},"mtimeMs":{{mtime}} } } } }
        """;

    [Fact]
    public async Task ConvertAsync_returns_NotFound_for_non_comic_format()
    {
        using var dir = new TempDir();
        var svc = Service(DetailClient(DetailJson("epub", "T", "A", 1, 2)), new EpubCache(dir.Path));
        var outcome = await svc.ConvertAsync("item1", fresh: false, warm: false, 100, 200, 1.0, default);
        Assert.Equal(ConvertResultKind.NotFound, outcome.Kind);
    }

    [Fact]
    public async Task ConvertAsync_returns_File_with_sanitized_name_when_cached()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(cache.PathFor("item1", 123, 456, 100, 200), "epub-bytes");
        var svc = Service(DetailClient(DetailJson("cbz", "My: Comic", "Jane Doe", 123, 456)), cache);
        var outcome = await svc.ConvertAsync("item1", fresh: false, warm: false, 100, 200, 1.0, default);
        Assert.Equal(ConvertResultKind.File, outcome.Kind);
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "inkshelf-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
