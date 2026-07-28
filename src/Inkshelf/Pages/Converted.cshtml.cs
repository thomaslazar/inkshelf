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
public class ConvertedModel : PageModel
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    public ConvertedModel(AbsApiClient api, EpubCache cache, ConvertQueue queue)
    { _api = api; _cache = cache; _queue = queue; }

    public List<ItemRowModel> Rows { get; private set; } = new();
    public bool LoadError { get; private set; }
    public bool AnyConverting { get; private set; }

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

    // `sort` is client-supplied, so anything unrecognised — absent, misspelled or
    // hostile — means "the default view", which is newest conversion FIRST. `Desc`
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

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        var settings = DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], settings.Retina, settings.Grayscale);

        // Cache entries for THIS device. Only the SET of item ids matters for the
        // batch fetch — row state is recomputed below from the current ebook file —
        // but keep each item's newest conversion time for the default sort. An item
        // can have more than one matching variant if the source changed and the
        // older entry hasn't been evicted.
        var convertedAt = new Dictionary<string, DateTime>();
        foreach (var v in _cache.ListVariants())
        {
            if (v.MaxW != target.MaxW || v.MaxH != target.MaxH || v.Grayscale != target.Grayscale) continue;
            if (!convertedAt.TryGetValue(v.ItemId, out var seen) || v.ConvertedAtUtc > seen)
                convertedAt[v.ItemId] = v.ConvertedAtUtc;
        }
        if (convertedAt.Count == 0) return Page();

        List<AbsBatchItem> items;
        try { items = await _api.GetItemsBatchAsync(convertedAt.Keys.ToList(), ct); }
        catch (HttpRequestException) { LoadError = true; return Page(); }

        var finished = await FetchFinishedAsync(ct);

        var built = new List<(ItemRowModel Row, AbsBatchMetadata? Meta)>();
        foreach (var it in items)
        {
            if (it.Media is null) continue;
            var m = it.Media;
            // Map the batch shape into the AbsItem the shared row/resolver expect.
            var item = new AbsItem(it.Id, new AbsMedia(
                new AbsMetadata(m.Metadata?.Title, null, null), m.CoverPath, null, m.EbookFile));
            var links = new LibraryLinks(it.LibraryId ?? "", null, null, null, null, false);
            var state = ConvertRowStateResolver.Resolve(item, m, target, _cache, _queue);
            if (state == ConvertRowState.Converting) AnyConverting = true;
            built.Add((new ItemRowModel(item, links, m.Metadata?.Authors, m.Metadata?.Series,
                state, "/converted", finished.Contains(it.Id)), m.Metadata));
        }

        IEnumerable<(ItemRowModel Row, AbsBatchMetadata? Meta)> ordered = ActiveSort switch
        {
            "series" => built
                .OrderBy(b => HasSeries(b.Meta) ? 0 : 1)
                .ThenBy(b => SeriesKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => SeqKey(b.Meta))
                .ThenBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
            "title" => built.OrderBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
            "author" => built
                .OrderBy(b => AuthorKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
            // ConvertedAtUtc, not the source mtime in the filename.
            _ => built
                .OrderBy(b => convertedAt.TryGetValue(b.Row.Item.Id, out var at) ? at : DateTime.MinValue)
                .ThenBy(b => TitleKey(b), StringComparer.OrdinalIgnoreCase),
        };
        var rows = ordered.Select(b => b.Row).ToList();
        if (AppliedDesc) rows.Reverse();
        Rows = rows;
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

    private static string TitleKey((ItemRowModel Row, AbsBatchMetadata? Meta) b) =>
        b.Row.Item.Media?.Metadata?.Title ?? "";

    private static string AuthorKey(AbsBatchMetadata? m) =>
        m?.Authors is { Count: > 0 } a ? a[0].Name : "";
}
