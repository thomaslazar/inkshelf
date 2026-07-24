using System.Linq;
using Inkshelf.Abs;
using Inkshelf.Auth;

namespace Inkshelf.Convert;

public readonly record struct KickResult(ConvertStatus Status, string? FilePath = null, string? DownloadName = null);

public readonly record struct FailureView(string Title, ConvertFailReason Reason, long? ArchiveBytes);

// The convert "kick": HTTP-free orchestration that runs IN THE REQUEST SCOPE.
// It fetches item detail (needs the ABS token), validates the format, computes
// the per-device cache path, and — on a miss — captures the access token and
// enqueues a background job. It never downloads or converts; ConvertWorker does
// that on the app lifetime. Kept HTTP-free so it unit-tests without a request.
public class ConvertService
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    private readonly TokenStore _tokens;
    private readonly ILogger<ConvertService> _logger;

    public ConvertService(AbsApiClient api, EpubCache cache, ConvertQueue queue,
        TokenStore tokens, ILogger<ConvertService> logger)
    {
        _api = api; _cache = cache; _queue = queue; _tokens = tokens; _logger = logger;
    }

    // Kick a conversion (or serve the cached result). fresh=true regenerates.
    public async Task<KickResult> KickAsync(string id, bool fresh, RenderTarget target,
        CancellationToken ct, string? fileIno = null)
    {
        var r = await ResolveAsync(id, target, fileIno, ct);
        if (r is null) return new KickResult(ConvertStatus.None);
        var (path, meta, downloadName, size) = r.Value;

        if (fresh) _cache.RemoveForItem(id);
        if (System.IO.File.Exists(path)) { _cache.Touch(path); return new KickResult(ConvertStatus.Done, path, downloadName); }

        var tokens = _tokens.Read();
        if (tokens is null) return new KickResult(ConvertStatus.None);
        var status = _queue.Enqueue(new ConvertJob(id, tokens.Access, path, meta, target, fileIno, size));
        return new KickResult(status);
    }

    // Poll status WITHOUT enqueuing. Done carries the file path + name to stream.
    public async Task<KickResult> StatusAsync(string id, RenderTarget target,
        CancellationToken ct, string? fileIno = null)
    {
        var r = await ResolveAsync(id, target, fileIno, ct);
        if (r is null) return new KickResult(ConvertStatus.None);
        var (path, _, downloadName, _) = r.Value;
        var status = _queue.Status(path);
        return status == ConvertStatus.Done
            ? new KickResult(ConvertStatus.Done, path, downloadName)
            : new KickResult(status);
    }

    // The current failure for this item's per-device conversion, enriched with the
    // item title for display. null when the item can't be resolved or isn't Failed.
    public async Task<FailureView?> FailureAsync(string id, RenderTarget target,
        CancellationToken ct, string? fileIno = null)
    {
        var r = await ResolveAsync(id, target, fileIno, ct);
        if (r is null) return null;
        var (path, meta, _, _) = r.Value;
        var f = _queue.FailureFor(path);
        return f is null ? null : new FailureView(meta.Title, f.Value.Reason, f.Value.ArchiveBytes);
    }

    // Fetch detail, validate cbz/cbr, and derive (cache path, EPUB metadata,
    // download filename, archive size). null = not found / not a comic.
    private async Task<(string Path, EbookMeta Meta, string DownloadName, long Size)?> ResolveAsync(
        string id, RenderTarget target, string? fileIno, CancellationToken ct)
    {
        AbsItemDetail detail;
        try { detail = await _api.GetItemDetailAsync(id, ct); }
        catch (HttpRequestException) { return null; }

        // Pick the file to convert: a specific libraryFile by ino, else the primary.
        string? fmt; long size; long mtime;
        if (!string.IsNullOrEmpty(fileIno))
        {
            var lf = detail.LibraryFiles?.FirstOrDefault(f => f.Ino == fileIno && f.FileType == "ebook");
            if (lf?.Metadata is null) return null;
            fmt = lf.Metadata.Ext?.TrimStart('.').ToLowerInvariant();
            size = lf.Metadata.Size; mtime = lf.Metadata.MtimeMs;
        }
        else
        {
            var ef = detail.Media?.EbookFile;
            if (ef?.Metadata is null) return null;
            fmt = ef.EbookFormat;
            size = ef.Metadata.Size; mtime = ef.Metadata.MtimeMs;
        }
        if (fmt != "cbz" && fmt != "cbr") return null;

        var md = detail.Media?.Metadata;
        var title = md?.Title ?? "Untitled";
        var author = md?.AuthorName is { Length: > 0 } an ? an
            : (md?.Authors is { Count: > 0 } ? md.Authors[0].Name : "Unknown");
        var seq = md?.Series is { Count: > 0 } ? md.Series[0].Sequence : null;
        var seriesName = md?.Series is { Count: > 0 } ? md.Series[0].Name : md?.SeriesName;

        var path = _cache.PathFor(id, size, mtime, target.MaxW, target.MaxH, target.Grayscale);
        var meta = new EbookMeta(title, author, seriesName, seq, id);
        var downloadName = Sanitize($"{author} - {title}") + ".epub";
        return (path, meta, downloadName, size);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
