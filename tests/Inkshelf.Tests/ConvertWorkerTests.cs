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

    // A VALID zip whose only page has an image extension but non-image bytes:
    // the archive opens (past the BadArchive stage) but decoding the page throws.
    private static byte[] CbzWithBadImage()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("p1.jpg").Open())
        {
            var junk = System.Text.Encoding.ASCII.GetBytes("not a real jpeg");
            s.Write(junk, 0, junk.Length);
        }
        return ms.ToArray();
    }

    private static byte[] CoverJpg(int w = 300, int h = 450)
    {
        using var ms = new MemoryStream();
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        img.Save(ms, new JpegEncoder()); return ms.ToArray();
    }

    private static byte[] Garbage() => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

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

    // A DI provider whose AbsDownloadClient returns `ebook` for /ebook and, for
    // /cover, either `cover` bytes (status 200) or the given error status.
    private static IServiceScopeFactory ScopeFactoryFor(
        byte[] ebook, byte[]? cover = null, string coverType = "image/jpeg", int coverStatus = 200)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/cover"))
                {
                    if (coverStatus != 200 || cover is null)
                        return new HttpResponseMessage((System.Net.HttpStatusCode)coverStatus);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(cover)
                        { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(coverType) } }
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(ebook) };
            }))
            { BaseAddress = new Uri("http://abs.local") }));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // A DI provider whose AbsDownloadClient returns `ebook` for /ebook and throws a
    // timeout-style TaskCanceledException for /cover — simulating an HttpClient
    // request timeout, NOT an app-shutdown cancellation (the job's ct stays live).
    private static IServiceScopeFactory ScopeFactoryCoverThrows(byte[] ebook)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/cover"))
                    throw new TaskCanceledException();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(ebook) };
            }))
            { BaseAddress = new Uri("http://abs.local") }));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ConvertWorker Worker(ConvertQueue queue, IServiceScopeFactory scopes, EpubCache cache,
        long maxArchiveBytes = long.MaxValue) =>
        new(queue, scopes, new EpubConverter(), new ConvertLock(), cache,
            new AbsOptions { MaxConcurrentConversions = 1, MaxArchiveBytes = maxArchiveBytes, MaxCacheBytes = long.MaxValue },
            NullLogger<ConvertWorker>.Instance);

    private static ConvertJob Job(string path) =>
        new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"), new RenderTarget(0, 0, 1.0, false));

    private static ConvertJob JobSized(string path, long bytes) =>
        new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"),
            new RenderTarget(0, 0, 1.0, false), null, bytes);

    // A download stub that always throws — proves the pre-download size check runs
    // BEFORE any download attempt.
    private static IServiceScopeFactory ScopeFactoryDownloadThrows()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("http://abs.local") }));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

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
    public async Task An_oversized_reported_archive_fails_TooLarge_before_downloading()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(JobSized(path, 500)); // reported 500 bytes

        // Ceiling 8 < 500 → pre-check trips. Download stub throws if ever called:
        // reaching it would yield DownloadFailed, so TooLarge proves the order.
        var worker = Worker(queue, ScopeFactoryDownloadThrows(), cache, maxArchiveBytes: 8);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertFailReason.TooLarge, queue.FailureFor(path)!.Value.Reason);
        Assert.Equal(500, queue.FailureFor(path)!.Value.ArchiveBytes);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task An_over_ceiling_download_fails_TooLarge_via_copy_guard()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path)); // ArchiveBytes 0 → pre-check skipped, copy guard trips

        var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache, maxArchiveBytes: 8);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertFailReason.TooLarge, queue.FailureFor(path)!.Value.Reason);
        Assert.Null(queue.FailureFor(path)!.Value.ArchiveBytes);
    }

    [Fact]
    public async Task A_download_failure_is_categorized_DownloadFailed()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryDownloadThrows(), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertFailReason.DownloadFailed, queue.FailureFor(path)!.Value.Reason);
    }

    [Fact]
    public async Task A_non_archive_download_is_categorized_BadArchive()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        // Downloads successfully, but the bytes aren't a valid archive → convert stage
        // throws from ArchiveFactory → BadArchive.
        var worker = Worker(queue, ScopeFactoryReturning(Garbage()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertFailReason.BadArchive, queue.FailureFor(path)!.Value.Reason);
    }

    [Fact]
    public async Task A_valid_archive_with_an_undecodable_page_is_categorized_ConvertError()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        // Archive opens fine (past BadArchive), but the page image won't decode →
        // the convert stage throws a non-archive exception → ConvertError.
        var worker = Worker(queue, ScopeFactoryReturning(CbzWithBadImage()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertFailReason.ConvertError, queue.FailureFor(path)!.Value.Reason);
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

    [Fact]
    public async Task Embeds_the_ABS_cover_when_available()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryFor(Cbz(), CoverJpg(), "image/jpeg"), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        using var epub = ZipFile.OpenRead(path);
        Assert.Contains("OEBPS/cover.jpg", epub.Entries.Select(e => e.FullName));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"cover-img\"/>", opf);
    }

    [Fact]
    public async Task A_missing_ABS_cover_still_produces_an_epub_with_first_page_cover()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryFor(Cbz(), coverStatus: 404), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        using var epub = ZipFile.OpenRead(path);
        Assert.DoesNotContain(epub.Entries.Select(e => e.FullName), n => n.StartsWith("OEBPS/cover"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"img1\"/>", opf);
    }

    [Fact]
    public async Task A_cover_fetch_timeout_still_produces_an_epub_with_first_page_cover()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        // The stub throws TaskCanceledException on /cover — an OperationCanceledException
        // subtype — but the worker's own token is never cancelled, so this must NOT
        // fail the job; it must fall back to the first page like a missing cover.
        var worker = Worker(queue, ScopeFactoryCoverThrows(Cbz()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        using var epub = ZipFile.OpenRead(path);
        Assert.DoesNotContain(epub.Entries.Select(e => e.FullName), n => n.StartsWith("OEBPS/cover"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"img1\"/>", opf);
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(cond(), "condition not met within timeout");
    }
}
