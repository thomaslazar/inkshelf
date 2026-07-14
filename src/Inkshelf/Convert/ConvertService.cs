using Inkshelf.Abs;

namespace Inkshelf.Convert;

public enum ConvertResultKind { NotFound, Warmed, File }

public readonly record struct ConvertOutcome(
    ConvertResultKind Kind, string? FilePath = null, string? DownloadName = null)
{
    public static readonly ConvertOutcome NotFound = new(ConvertResultKind.NotFound);
    public static readonly ConvertOutcome Warmed = new(ConvertResultKind.Warmed);
    public static ConvertOutcome File(string path, string downloadName) =>
        new(ConvertResultKind.File, path, downloadName);
}

// Orchestrates on-demand CBZ/CBR → fixed-layout EPUB conversion for a single
// item: fetch detail, validate format, probe the per-device cache, convert on
// miss, and describe what the caller should return. HTTP-free so it unit-tests
// without a request context (the endpoint parses the screen cookie and maps the
// outcome to an IResult).
public class ConvertService
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly EpubConverter _converter;
    private readonly AbsOptions _options;
    private readonly ILogger<ConvertService> _logger;

    public ConvertService(AbsApiClient api, EpubCache cache,
        EpubConverter converter, AbsOptions options, ILogger<ConvertService> logger)
    {
        _api = api; _cache = cache; _converter = converter; _options = options; _logger = logger;
    }

    public async Task<ConvertOutcome> ConvertAsync(string id, bool fresh, bool warm,
        int maxW, int maxH, double dpr, CancellationToken ct)
    {
        AbsItemDetail detail;
        try { detail = await _api.GetItemDetailAsync(id, ct); }
        catch (HttpRequestException) { return ConvertOutcome.NotFound; }

        var ef = detail.Media?.EbookFile;
        var fmt = ef?.EbookFormat;
        if (ef?.Metadata is null || (fmt != "cbz" && fmt != "cbr")) return ConvertOutcome.NotFound;

        var size = ef.Metadata.Size; var mtime = ef.Metadata.MtimeMs;
        if (fresh) _cache.RemoveForItem(id);

        // authorName isn't always populated on uploaded ebooks; fall back to the
        // authors[] list. Used for both the embedded metadata and the file name.
        var md = detail.Media!.Metadata!;
        var title = md.Title ?? "Untitled";
        var author = md.AuthorName is { Length: > 0 } an ? an
            : (md.Authors is { Count: > 0 } ? md.Authors[0].Name : "Unknown");
        var seq = md.Series is { Count: > 0 } ? md.Series[0].Sequence : null;
        var seriesName = md.Series is { Count: > 0 } ? md.Series[0].Name : md.SeriesName;

        var path = _cache.PathFor(id, size, mtime, maxW, maxH);
        if (!System.IO.File.Exists(path))
        {
            _logger.LogInformation("Converting {Id} ({Fmt}, {Bytes} bytes, cap {W}x{H} @dpr {Dpr}) to EPUB…", id, fmt, size, maxW, maxH, dpr);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (archive, _) = await _api.GetEbookStreamAsync(id, ct);
            using var buffered = new MemoryStream();
            await using (archive) await archive.CopyToAsync(buffered, ct);   // SharpCompress needs a seekable stream
            buffered.Position = 0;
            await _converter.ConvertAsync(buffered, new EbookMeta(title, author, seriesName, seq, id), path, maxW, maxH, dpr, ct);
            _logger.LogInformation("Converted {Id} in {Ms} ms → {OutBytes} bytes", id, sw.ElapsedMilliseconds, new FileInfo(path).Length);
            _cache.EnforceCap(_options.MaxCacheBytes);
        }
        else
        {
            _cache.Touch(path);
            _logger.LogInformation("Serving cached EPUB for {Id} ({OutBytes} bytes)", id, new FileInfo(path).Length);
        }

        // warm just ensures the EPUB is built + cached, so the user's next tap
        // downloads it instantly; it returns OK, not the file.
        if (warm) return ConvertOutcome.Warmed;

        var fileName = Sanitize($"{author} - {title}") + ".epub";
        return ConvertOutcome.File(path, fileName);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
