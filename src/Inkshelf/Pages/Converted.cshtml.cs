using System.Globalization;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

// Combined "already converted, on this device" view. The EPUB cache is the only
// record of what's converted; we enumerate it, keep the variants matching this
// device's RenderTarget, dedupe by item id, then fetch metadata for those ids in
// one cross-library batch call and render the standard listing row.
public class ConvertedModel : PageModel, IPagedListing
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    private readonly DownloadMarks _marks;
    private readonly TokenStore _tokens;
    private readonly DownloadTickets _tickets;
    public ConvertedModel(AbsApiClient api, EpubCache cache, ConvertQueue queue, DownloadMarks marks,
        TokenStore tokens, DownloadTickets tickets)
    { _api = api; _cache = cache; _queue = queue; _marks = marks; _tokens = tokens; _tickets = tickets; }

    public List<ItemRowModel> Rows { get; private set; } = new();
    public bool LoadError { get; private set; }
    public bool AnyConverting { get; private set; }

    public Pager Pager { get; private set; } = new(0, DeviceSettings.Default.PerPage, 0);

    // The APPLIED sort and direction, not the raw query values: the pager must
    // describe what is on screen, the same rule SortHref's comment gives.
    public string PageHref(int page) =>
        $"/converted?sort={ActiveSort}" + (AppliedDesc ? "&desc=1" : "") + (page > 1 ? $"&page={page}" : "");

    // desc binds as a STRING on purpose: ABS wants desc=1 and Razor's bool binder
    // rejects "1", so a bool here makes every descending direction unreachable.
    // Same rule as the library listing.
    [FromQuery(Name = "sort")] public string? Sort { get; set; }
    [FromQuery(Name = "desc")] public string? DescParam { get; set; }
    public bool Desc => DescParam == "1";

    // Two-state toggle, unlike the library listing's off/asc/desc cycle: this list
    // is sorted locally, so there is no "let the server decide" state to return to.
    // Clicking the active field flips direction; `converted` starts descending
    // because newest-first is the point of the page.
    public string SortHref(string field)
    {
        var nextDesc = ActiveSort == field ? !AppliedDesc : field == ConvertedKey;
        return $"/converted?sort={field}" + (nextDesc ? "&desc=1" : "");
    }

    public const string ConvertedKey = "converted";
    private static readonly string[] Keys = [ConvertedKey, "series", "title", "author"];

    // `sort` is client-supplied, so anything unrecognised - absent, misspelled or
    // hostile - means "the default view", which is newest conversion FIRST. `Desc`
    // is what the query asked for; `AppliedDesc` is what the page actually did, and
    // it keys off recognition, not off `Sort is null`: with a garbage value, `Desc`
    // would be false and the page would render oldest-first, which is not the
    // default it claims to fall back to. The two diverge on the default view.
    private bool IsRecognised => Keys.Contains(Sort);
    public string ActiveSort => IsRecognised ? Sort! : ConvertedKey;

    // `Desc` is what the query asked for; `AppliedDesc` is what the page actually
    // did. They differ on the default view (no recognised `sort`), where the list
    // still renders newest-first even though `Desc` (from a missing/garbage
    // `desc` param) is false. The arrow and hrefs must reflect the applied
    // direction, not the raw query value, or they lie about what's on screen.
    public bool AppliedDesc => IsRecognised ? Desc : true;

    public async Task<IActionResult> OnGetAsync([FromQuery] int page = 1, CancellationToken ct = default)
    {
        var settings = DeviceSettings.EnsureDid(HttpContext);
        var target = settings.ToRenderTarget(Request.Cookies["scr"]);
        var markSet = _marks.Read(settings.Did);

        // Cache entries for THIS device. Only the SET of item ids matters for the
        // batch fetch - row state is recomputed below from the current ebook file -
        // but keep each item's newest conversion time for the default sort. An item
        // can have more than one matching variant if the source changed and the
        // older entry hasn't been evicted.
        var convertedAt = new Dictionary<string, DateTime>();
        foreach (var v in _cache.ListVariants())
        {
            if (v.MaxW != target.MaxW || v.MaxH != target.MaxH || v.Grayscale != target.Grayscale
                || v.Spread != target.Spread || v.Scale != target.Scale || v.Dpr != target.Dpr
                || v.Upscale != target.Upscale) continue;
            if (!convertedAt.TryGetValue(v.ItemId, out var seen) || v.ConvertedAtUtc > seen)
                convertedAt[v.ItemId] = v.ConvertedAtUtc;
        }
        if (convertedAt.Count == 0) return Page();

        List<AbsBatchItem> items;
        try { items = await _api.GetItemsBatchAsync(convertedAt.Keys.ToList(), ct); }
        catch (HttpRequestException) { LoadError = true; return Page(); }

        var finished = await FetchFinishedAsync(ct);

        // State for EVERY item, not just the page's: AnyConverting drives a 30s
        // MetaRefresh, and scoping it to the visible page would stop the page
        // refreshing while something converts on another page. Resolve is local
        // file and queue checks with no network call.
        var candidates = new List<(AbsBatchItem It, AbsBatchMedia M, ConvertRowState State, string? CachePath, AbsBatchMetadata? Meta)>();
        foreach (var it in items)
        {
            if (it.Media is null) continue;
            var m = it.Media;
            var probe = new AbsItem(it.Id, new AbsMedia(
                new AbsMetadata(m.Metadata?.Title, null, null), m.CoverPath, null, m.EbookFile));
            var (state, cachePath) = ConvertRowStateResolver.Resolve(probe, m, target, _cache, _queue);
            if (state == ConvertRowState.Converting) AnyConverting = true;
            candidates.Add((it, m, state, cachePath, m.Metadata));
        }

        IEnumerable<(AbsBatchItem It, AbsBatchMedia M, ConvertRowState State, string? CachePath, AbsBatchMetadata? Meta)> ordered = ActiveSort switch
        {
            "series" => candidates
                .OrderBy(b => HasSeries(b.Meta) ? 0 : 1)
                .ThenBy(b => SeriesKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => SeqKey(b.Meta))
                .ThenBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
            "title" => candidates.OrderBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
            "author" => candidates
                .OrderBy(b => AuthorKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
            // ConvertedAtUtc, not the source mtime in the filename.
            _ => candidates
                .OrderBy(b => convertedAt.TryGetValue(b.It.Id, out var at) ? at : DateTime.MinValue)
                .ThenBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
        };
        var sorted = ordered.ToList();
        if (AppliedDesc) sorted.Reverse();

        // Clamp rather than render an empty list: this list is sliced locally, so
        // a page past the end is ours to correct, unlike the library listing
        // where ABS answers an empty result.
        var perPage = settings.PerPage;
        var totalPages = Math.Max(1, (sorted.Count + perPage - 1) / perPage);
        var zeroPage = Math.Clamp(page - 1, 0, totalPages - 1);
        Pager = new Pager(zeroPage, perPage, sorted.Count);

        // Rows, and therefore TICKETS, only for what is rendered.
        var access = _tokens.Read()?.Access;
        foreach (var b in sorted.Skip(zeroPage * perPage).Take(perPage))
        {
            var item = new AbsItem(b.It.Id, new AbsMedia(
                new AbsMetadata(b.M.Metadata?.Title, null, null), b.M.CoverPath, null, b.M.EbookFile));
            var links = new LibraryLinks(b.It.LibraryId ?? "", null, null, null, null, false);
            var rawDownloaded = markSet.Contains(DownloadMarks.RawKey(b.It.Id, null));
            var epubDownloaded = markSet.Contains(DownloadMarks.EpubKey(b.It.Id, null));
            // Both hrefs are re-requested by a cookie-less download manager, so each
            // gets a ticket standing for exactly the file that row offers.
            var epubTicket = b.CachePath is { } path
                ? _tickets.MintEpub(b.It.Id, null, settings.Did,
                    EpubName.For(b.M.Metadata?.Authors?.FirstOrDefault()?.Name, b.M.Metadata?.Title), path)
                : null;
            var filename = b.M.EbookFile?.Metadata?.Filename;
            var rawTicket = filename is not null && access is { } acc
                ? _tickets.MintRaw(b.It.Id, null, settings.Did, filename, acc)
                : null;
            Rows.Add(new ItemRowModel(item, links, b.M.Metadata?.Authors, b.M.Metadata?.Series,
                b.State, "/converted", finished.Contains(b.It.Id), rawDownloaded, epubDownloaded,
                rawTicket, epubTicket));
        }
        return Page();
    }

    private async Task<HashSet<string>> FetchFinishedAsync(CancellationToken ct)
    { try { return await _api.GetFinishedItemIdsAsync(ct); } catch (HttpRequestException) { return new(); } }

    private static bool HasSeries(AbsBatchMetadata? m) => m?.Series is { Count: > 0 };

    private static string SeriesKey(AbsBatchMetadata? m) =>
        m?.Series is { Count: > 0 } s ? s[0].Name : "";

    private static double SeqKey(AbsBatchMetadata? m)
    {
        var seq = m?.Series is { Count: > 0 } s ? s[0].Sequence : null;
        return double.TryParse(seq, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;
    }

    private static string TitleKey(AbsBatchMetadata? m) => m?.Title ?? "";

    private static string AuthorKey(AbsBatchMetadata? m) =>
        m?.Authors is { Count: > 0 } a ? a[0].Name : "";
}
