using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertWorkerTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "worker-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static byte[] Cbz()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("p1.jpg").Open())
        {
            using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(80, 120);
            img.Save(s, new JpegEncoder());
        }
        return ms.ToArray();
    }

    // A DI provider exposing an AbsDownloadClient whose HttpClient returns `bytes`.
    private static IServiceScopeFactory ScopeFactoryReturning(byte[] bytes)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) }))
            { BaseAddress = new Uri("http://abs.local") }));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ConvertWorker Worker(ConvertQueue queue, IServiceScopeFactory scopes, EpubCache cache,
        long maxArchiveBytes = long.MaxValue) =>
        new(queue, scopes, new EpubConverter(), new ConvertLock(), cache,
            new AbsOptions { MaxConcurrentConversions = 1, MaxArchiveBytes = maxArchiveBytes, MaxCacheBytes = long.MaxValue },
            NullLogger<ConvertWorker>.Instance);

    private static ConvertJob Job(string path) =>
        new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"), 0, 0, 1.0);

    [Fact]
    public async Task Processes_a_job_to_a_cached_epub()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.True(File.Exists(path));
        Assert.Equal(ConvertStatus.Done, queue.Status(path));
    }

    [Fact]
    public async Task A_download_failure_marks_Failed_not_Running()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        // Download client that always 401s → DownloadEbookAsync throws.
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("http://abs.local") }));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var worker = Worker(queue, scopes, cache);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertStatus.Failed, queue.Status(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task An_over_ceiling_archive_marks_Failed_and_writes_no_file()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        // Ceiling far below the CBZ the download returns → CopyWithLimitAsync
        // trips, the worker marks Failed and never converts.
        var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache, maxArchiveBytes: 8);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertStatus.Failed, queue.Status(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Deletes_the_archive_temp_file_after_a_successful_conversion()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.True(File.Exists(path));                                  // epub produced
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dl.tmp"));          // archive temp cleaned up
    }

    [Fact]
    public void SweepTemp_also_removes_orphaned_dl_tmp()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "abc.dl.tmp"), "partial download");
        File.WriteAllText(Path.Combine(dir.Path, "keep.epub"), "real");
        cache.SweepTemp();
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dl.tmp"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "keep.epub")));
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(cond(), "condition not met within timeout");
    }
}
