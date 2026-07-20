using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Inkshelf.Abs;

namespace Inkshelf.Convert;

// Drains ConvertQueue on the APP LIFETIME (never a request token), so a client
// disconnect can't cancel an in-flight conversion. Runs MaxConcurrentConversions
// consumer loops (default 1). Per job it downloads the archive with the captured
// token, buffers it under the archive ceiling, converts, and records terminal
// state. ConvertLock + a double-checked File.Exists dedup identical targets.
public sealed class ConvertWorker : BackgroundService
{
    private readonly ConvertQueue _queue;
    private readonly IServiceScopeFactory _scopes; // resolve AbsDownloadClient per job
    private readonly EpubConverter _converter;
    private readonly ConvertLock _lock;
    private readonly EpubCache _cache;
    private readonly AbsOptions _options;
    private readonly ILogger<ConvertWorker> _logger;

    public ConvertWorker(ConvertQueue queue, IServiceScopeFactory scopes, EpubConverter converter,
        ConvertLock convertLock, EpubCache cache, AbsOptions options, ILogger<ConvertWorker> logger)
    {
        _queue = queue; _scopes = scopes; _converter = converter;
        _lock = convertLock; _cache = cache; _options = options; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cache.SweepTemp(); // clear orphan .tmp from a prior crash/shutdown

        var loops = Math.Max(1, _options.MaxConcurrentConversions);
        var tasks = new Task[loops];
        for (var i = 0; i < loops; i++) tasks[i] = ConsumeAsync(stoppingToken);
        await Task.WhenAll(tasks);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
                await ProcessAsync(job, ct);
        }
        catch (OperationCanceledException) { /* app shutting down */ }
    }

    private async Task ProcessAsync(ConvertJob job, CancellationToken ct)
    {
        var dlTmp = Path.Combine(Path.GetDirectoryName(job.CachePath)!,
            Guid.NewGuid().ToString("N") + ".dl.tmp");
        try
        {
            if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }
            _queue.MarkRunning(job.CachePath);

            using (await _lock.AcquireAsync(job.CachePath, ct))
            {
                if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }

                var sw = Stopwatch.StartNew();
                using var scope = _scopes.CreateScope();
                var download = scope.ServiceProvider.GetRequiredService<AbsDownloadClient>();

                // Spool the download to a temp FILE (not a MemoryStream) so the ~220 MiB
                // archive never sits in the managed heap. Ceiling enforced during the copy.
                await using (var archive = await download.DownloadEbookAsync(job.ItemId, job.AccessToken, ct))
                await using (var spool = new FileStream(dlTmp, FileMode.Create, FileAccess.Write))
                {
                    if (!await CopyWithLimitAsync(archive, spool, _options.MaxArchiveBytes, ct))
                    {
                        _logger.LogWarning("Archive for {Id} exceeds {Limit} bytes — refusing.", job.ItemId, _options.MaxArchiveBytes);
                        _queue.MarkFailed(job.CachePath);
                        return;
                    }
                }

                // Best-effort cover (small; never fails the job). Uses the same
                // captured token as the ebook download.
                var cover = await TryFetchCoverAsync(download, job, ct);

                await using (var read = new FileStream(dlTmp, FileMode.Open, FileAccess.Read))
                    await _converter.ConvertAsync(read, job.Meta, job.CachePath, job.Target, ct, cover);

                _cache.EnforceCap(_options.MaxCacheBytes);
                _logger.LogInformation("Converted {Id} in {Ms} ms", job.ItemId, sw.ElapsedMilliseconds);
            }
            _queue.MarkDone(job.CachePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // app stopping — leave .tmp for the next startup sweep, don't mark Failed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversion failed for {Id}", job.ItemId);
            _queue.MarkFailed(job.CachePath);
        }
        finally
        {
            // Best-effort cleanup: never let deleting our own temp file throw out of
            // finally (it would mask a real conversion exception and skip the release).
            try { if (File.Exists(dlTmp)) File.Delete(dlTmp); } catch { }
            // Return ImageSharp's retained UNMANAGED pool to the OS between jobs
            // (GC config can't reclaim it). Safe across jobs — trims free buffers,
            // not ones a concurrent convert is renting.
            SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
        }
    }

    // Thumbnail-appropriate cover width requested from ABS. Not the device page cap:
    // the cover is only ever a thumbnail, so page-resolution art would just bloat the
    // file. The device cap still bounds it on the way through PageImageProcessor.
    private const int CoverWidth = 600;

    // Best-effort ABS cover fetch. Any failure (no cover / 404 / transient, including
    // an HttpClient request timeout, which surfaces as TaskCanceledException) yields
    // null and the converter falls back to the first page — never fails the job.
    // Only a genuine app-shutdown cancellation (ct.IsCancellationRequested) propagates.
    private static async Task<(byte[] Bytes, string Ext)?> TryFetchCoverAsync(
        AbsDownloadClient download, ConvertJob job, CancellationToken ct)
    {
        try
        {
            var (stream, contentType) = await download.DownloadCoverAsync(job.ItemId, job.AccessToken, CoverWidth, ct);
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                return (ms.ToArray(), CoverExt(contentType));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // e.g. HttpClient request timeout — not app shutdown; fall back to first page
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string CoverExt(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };

    // Copy src→dst, returning false as soon as more than `limit` bytes are read
    // (decompression-bomb / OOM guard). limit <= 0 disables the cap.
    private static async Task<bool> CopyWithLimitAsync(Stream src, Stream dst, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (limit > 0 && total > limit) return false;
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return true;
    }
}
