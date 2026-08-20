using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class ItemModel : PageModel
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    private readonly DownloadMarks _marks;
    public ItemModel(AbsApiClient api, EpubCache cache, ConvertQueue queue, DownloadMarks marks)
    { _api = api; _cache = cache; _queue = queue; _marks = marks; }

    [FromRoute] public string Id { get; set; } = "";

    // One downloadable ebook file: its display name/format, the download href,
    // and (for cbz/cbr) the convert action to render via _ConvertAction.
    public record FileRow(string Name, string Format, string DownloadHref, ConvertActionModel? Convert,
        bool Downloaded = false);

    public string LibraryId { get; private set; } = "";
    public string LibraryName { get; private set; } = "";
    public AbsDetailMetadata? Meta { get; private set; }
    public List<string> Tags { get; private set; } = new();
    public bool HasCover { get; private set; }
    public bool Read { get; private set; }
    public List<FileRow> Files { get; private set; } = new();
    public LibraryLinks Links => new(LibraryId, null, null, null, null, false);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        AbsItemDetail detail;
        try { detail = await _api.GetItemDetailAsync(Id, ct); }
        catch (HttpRequestException) { return NotFound(); }
        if (detail.Media is null) return NotFound();

        LibraryId = detail.LibraryId ?? "";
        // Resolve the library's display name for the breadcrumb. A transient
        // failure just drops the middle crumb (an expired session throws
        // AbsAuthException, not HttpRequestException, so it still → /login).
        if (!string.IsNullOrEmpty(LibraryId))
        {
            try { LibraryName = (await _api.GetLibrariesAsync(ct)).FirstOrDefault(l => l.Id == LibraryId)?.Name ?? ""; }
            catch (HttpRequestException) { LibraryName = ""; }
        }
        Meta = detail.Media.Metadata;
        Tags = detail.Media.Tags ?? new();
        HasCover = !string.IsNullOrEmpty(detail.Media.CoverPath);

        var ds = Auth.DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], ds.Retina, ds.Grayscale, ds.Spread, ds.Scale, ds.ActiveOverride);
        var marks = ds.Did.Length == 0 ? new HashSet<string>() : _marks.Read(ds.Did);

        try { Read = (await _api.GetFinishedItemIdsAsync(ct)).Contains(Id); }
        catch (HttpRequestException) { Read = false; }

        var primaryIno = detail.Media.EbookFile?.Ino;
        foreach (var f in detail.LibraryFiles ?? new())
        {
            if (f.FileType != "ebook" || f.Metadata is null) continue;
            var isPrimary = f.Ino is not null && f.Ino == primaryIno;
            var keyIno = isPrimary ? null : f.Ino;
            var fmt = f.Metadata.Ext?.TrimStart('.').ToLowerInvariant() ?? "";
            var name = f.Metadata.Filename ?? f.Ino ?? "file";
            var dl = isPrimary ? $"/download/{Id}" : $"/download/{Id}?file={Uri.EscapeDataString(f.Ino!)}";
            var rawDownloaded = marks.Contains(DownloadMarks.RawKey(Id, keyIno));

            ConvertActionModel? convert = null;
            if (fmt is "cbz" or "cbr")
            {
                var state = ConvertRowStateResolver.ResolveFor(
                    Id, f.Metadata.Size, f.Metadata.MtimeMs, fmt, target, _cache, _queue);
                convert = new ConvertActionModel(Id, keyIno, state, $"/item/{Id}",
                    marks.Contains(DownloadMarks.EpubKey(Id, keyIno)), ShowRegen: true);
            }
            Files.Add(new FileRow(name, fmt.ToUpperInvariant(), dl, convert, rawDownloaded));
        }
        return Page();
    }
}
