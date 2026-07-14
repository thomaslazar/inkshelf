using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class LibraryModel : PageModel
{
    public const int PageSize = 10;
    public const int SearchLimit = 25;
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    public LibraryModel(AbsApiClient api, EpubCache cache) { _api = api; _cache = cache; }

    [FromRoute] public string Id { get; set; } = "";
    [FromQuery] public string? Q { get; set; }
    // Filter by a facet. Either a ready-made ABS filter (from search groups,
    // which carry ids), or an author/series NAME the row links carry — the
    // list is always minified so rows only have names; we resolve name→id here.
    [FromQuery] public string? Filter { get; set; }
    [FromQuery] public string? Author { get; set; }
    [FromQuery] public string? Series { get; set; }
    [FromQuery] public string? Sort { get; set; }
    // ABS wants desc=1 (not "true"), and Razor's bool binder rejects "1", so
    // carry the raw token and derive the flag.
    [FromQuery(Name = "desc")] public string? DescParam { get; set; }
    public bool Desc => DescParam == "1";

    public bool IsFavorite { get; private set; }
    public bool IsSearch => !string.IsNullOrWhiteSpace(Q);
    public string? FilterLabel { get; private set; }
    public string LibraryName { get; private set; } = "Library";

    public List<AbsItem> Items { get; private set; } = new();
    public Pager Pager { get; private set; } = new(0, PageSize, 0);
    public AbsSearchResults? SearchResults { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        IsFavorite = Favorites.Read(Request) == Id;

        var libraries = await _api.GetLibrariesAsync(ct);
        LibraryName = libraries.FirstOrDefault(l => l.Id == Id)?.Name ?? "Library";

        if (IsSearch)
        {
            SearchResults = await _api.SearchAsync(Id, Q!.Trim(), SearchLimit, ct);
            return Page();
        }

        var filter = await ResolveFilterAsync(ct);
        var zeroPage = Math.Max(0, page - 1);
        var result = await _api.GetItemsAsync(Id, zeroPage, PageSize, filter, Sort, Desc, ct);
        Items = result.Results;
        _structured = await FetchStructuredAsync(Items, ct);
        Pager = new Pager(result.Page, result.Limit <= 0 ? PageSize : result.Limit, result.Total);
        return Page();
    }

    // Expanded media (structured authors/series + ebookFile) for the current
    // page, keyed by item id, from one batch call. A batch failure leaves it
    // empty and rows fall back to the comma-joined name strings.
    private Dictionary<string, AbsBatchMedia> _structured = new();

    private async Task<Dictionary<string, AbsBatchMedia>> FetchStructuredAsync(List<AbsItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        if (ids.Count == 0) return new();
        try { return await _api.GetItemsMetadataBatchAsync(ids, ct); }
        catch (HttpRequestException) { return new(); }
    }

    // Row view-model for a listing item: structured author/series links plus
    // whether a converted EPUB is already cached for this device (so the row can
    // show it downloads instantly).
    public ItemRowModel RowFor(AbsItem item)
    {
        _structured.TryGetValue(item.Id, out var media);
        return new ItemRowModel(item, Links, media?.Metadata?.Authors, media?.Metadata?.Series, IsCached(item, media));
    }

    // A convert is cached only for the exact device size (the cache key includes
    // it), so we need the screen cookie; on the first load (before the layout
    // script has set it) this reports false, which self-corrects on next render.
    private bool IsCached(AbsItem item, AbsBatchMedia? media)
    {
        var fmt = item.Media?.EbookFormat;
        if (fmt != "cbz" && fmt != "cbr") return false;
        var efm = media?.EbookFile?.Metadata;
        if (efm is null) return false;
        var (w, h, _) = ScreenTarget.FromCookie(Request.Cookies["scr"]);
        return _cache.TryGet(item.Id, efm.Size, efm.MtimeMs, w, h, out _);
    }

    // Turn ?filter / ?author / ?series into an ABS filter string. Author/series
    // names are resolved to ids via one search call (the row links only carry
    // names). Returns null when nothing matches (→ empty listing).
    private async Task<string?> ResolveFilterAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Filter)) { FilterLabel = "filter"; return Filter; }

        if (!string.IsNullOrWhiteSpace(Author))
        {
            var r = await _api.SearchAsync(Id, Author!.Trim(), SearchLimit, ct);
            var a = r.Authors.FirstOrDefault(x => string.Equals(x.Name, Author!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (a is not null) { FilterLabel = a.Name; return AbsFilter.Encode("authors", a.Id); }
            FilterLabel = Author; return "authors.__none__";
        }
        if (!string.IsNullOrWhiteSpace(Series))
        {
            var name = Series!.Trim();
            var r = await _api.SearchAsync(Id, name, SearchLimit, ct);
            var s = r.Series.FirstOrDefault(x => string.Equals(x.Series.Name, name, StringComparison.OrdinalIgnoreCase));
            if (s is not null) { FilterLabel = s.Series.Name; return AbsFilter.Encode("series", s.Series.Id); }
            FilterLabel = name; return "series.__none__";
        }
        return null;
    }

    public bool IsFiltered => FilterLabel is not null;

    // One shared builder for every library URL (page + row partial).
    public LibraryLinks Links => new(Id, Filter, Author, Series, Sort, Desc);
}
