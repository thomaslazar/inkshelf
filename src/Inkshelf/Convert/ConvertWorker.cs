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

                await using var archive = await download.DownloadEbookAsync(job.ItemId, job.AccessToken, ct);
                using var buffered = new MemoryStream();
                if (!await CopyWithLimitAsync(archive, buffered, _options.MaxArchiveBytes, ct))
                {
                    _logger.LogWarning("Archive for {Id} exceeds {Limit} bytes — refusing.", job.ItemId, _options.MaxArchiveBytes);
                    _queue.MarkFailed(job.CachePath);
                    return;
                }
                buffered.Position = 0;
                await _converter.ConvertAsync(buffered, job.Meta, job.CachePath, job.MaxW, job.MaxH, job.Dpr, ct);
                _cache.EnforceCap(_options.MaxCacheBytes);
                _logger.LogInformation("Converted {Id} in {Ms} ms", job.ItemId, sw.ElapsedMilliseconds);
            }
            _queue.MarkDone(job.CachePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App stopping mid-convert: leave the .tmp for the next startup sweep,
            // don't mark Failed (a restart re-queues on the next tap anyway).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversion failed for {Id}", job.ItemId);
            _queue.MarkFailed(job.CachePath);
        }
    }

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
