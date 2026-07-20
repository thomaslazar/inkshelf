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

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        var settings = DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], settings.Retina, settings.Grayscale);

        // Cache entries for THIS device. Only the SET of item ids matters here —
        // state is recomputed below from the current ebook file's size/mtime, so
        // which cached variant existed is irrelevant.
        var ids = new HashSet<string>();
        foreach (var v in _cache.ListVariants())
        {
            if (v.MaxW != target.MaxW || v.MaxH != target.MaxH || v.Grayscale != target.Grayscale) continue;
            ids.Add(v.ItemId);
        }
        if (ids.Count == 0) return Page();

        List<AbsBatchItem> items;
        try { items = await _api.GetItemsBatchAsync(ids.ToList(), ct); }
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

        // series → sequence → title; items with no series sort last.
        Rows = built
            .OrderBy(b => HasSeries(b.Meta) ? 0 : 1)
            .ThenBy(b => SeriesKey(b.Meta), StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => SeqKey(b.Meta))
            .ThenBy(b => b.Row.Item.Media?.Metadata?.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(b => b.Row)
            .ToList();
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
}
