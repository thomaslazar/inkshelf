using Inkshelf.Abs;
using Inkshelf.Auth;
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
    private readonly ConvertQueue _queue;
    public LibraryModel(AbsApiClient api, EpubCache cache, ConvertQueue queue)
    { _api = api; _cache = cache; _queue = queue; }

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
    // The active facet's humanized type ("Series"/"Author"/…) and, when known,
    // its display name; FilterDisplay joins them ("Series: The Sandman").
    public string? FilterType { get; private set; }
    public string? FilterName { get; private set; }
    public string FilterDisplay => FilterName is null ? (FilterType ?? "") : $"{FilterType}: {FilterName}";
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
            // Search hits are capped (SearchLimit), so one batch-metadata call is
            // cheap and lets search rows carry the same convert state as the
            // listing (structured authors/series + cached/converting), not just a
            // plain "Convert".
            var books = SearchResults.Book.Select(b => b.LibraryItem).ToList();
            _structured = await FetchStructuredAsync(books, ct);
            ComputeConvertStates(books);
            _finished = await FetchFinishedAsync(ct);
            return Page();
        }

        var filter = await ResolveFilterAsync(ct);
        var zeroPage = Math.Max(0, page - 1);
        var result = await _api.GetItemsAsync(Id, zeroPage, PageSize, filter, Sort, Desc, ct);
        Items = result.Results;
        _structured = await FetchStructuredAsync(Items, ct);
        RefineFilterLabel();
        ComputeConvertStates(Items);
        _finished = await FetchFinishedAsync(ct);
        Pager = new Pager(result.Page, result.Limit <= 0 ? PageSize : result.Limit, result.Total);
        return Page();
    }

    // Expanded media (structured authors/series + ebookFile) for the current
    // page, keyed by item id, from one batch call. A batch failure leaves it
    // empty and rows fall back to the comma-joined name strings.
    private Dictionary<string, AbsBatchMedia> _structured = new();
    private HashSet<string> _finished = new();
    // Decoded active facet filter (group + value id), for resolving its label.
    private string? _filterGroup;
    private string? _filterValue;

    private async Task<Dictionary<string, AbsBatchMedia>> FetchStructuredAsync(List<AbsItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        if (ids.Count == 0) return new();
        try { return await _api.GetItemsMetadataBatchAsync(ids, ct); }
        catch (HttpRequestException) { return new(); }
    }

    // Read-state is a single GET /api/me. A transient failure degrades to "all
    // unread" rather than blanking the page; an expired session still propagates
    // AbsAuthException → /login (only HttpRequestException is swallowed).
    private async Task<HashSet<string>> FetchFinishedAsync(CancellationToken ct)
    {
        try { return await _api.GetFinishedItemIdsAsync(ct); }
        catch (HttpRequestException) { return new(); }
    }

    // Row view-model for a listing item: structured author/series links plus
    // the precomputed convert state (so the row can show progress/cached/retry).
    public ItemRowModel RowFor(AbsItem item)
    {
        _structured.TryGetValue(item.Id, out var media);
        var state = _states.TryGetValue(item.Id, out var s) ? s : ConvertRowState.NotConvertible;
        if (state == ConvertRowState.NotConvertible)
        {
            // Search rows: _states is empty (ComputeConvertStates runs only for
            // the listing branch), so fall back to a plain Convert for cbz/cbr.
            var f = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat;
            if (f is "cbz" or "cbr") state = ConvertRowState.Convert;
        }
        var ret = Request.Path + Request.QueryString; // exact current listing URL
        return new ItemRowModel(item, Links, media?.Metadata?.Authors, media?.Metadata?.Series, state, ret, _finished.Contains(item.Id));
    }

    // Per-row convert state, precomputed so the head (which renders before the
    // rows) can decide whether to emit the no-JS <noscript> meta-refresh.
    public bool AnyConverting { get; private set; }
    private readonly Dictionary<string, ConvertRowState> _states = new();

    private void ComputeConvertStates(IEnumerable<AbsItem> items)
    {
        var s = DeviceSettings.Read(Request);
        var t = ScreenTarget.FromCookie(Request.Cookies["scr"], s.Retina, s.Grayscale);
        foreach (var item in items)
        {
            _structured.TryGetValue(item.Id, out var media);
            var state = RowState(item, media, t);
            _states[item.Id] = state;
            if (state == ConvertRowState.Converting) AnyConverting = true;
        }
    }

    private ConvertRowState RowState(AbsItem item, AbsBatchMedia? media, RenderTarget target)
        => ConvertRowStateResolver.Resolve(item, media, target, _cache, _queue);

    // Turn ?filter / ?author / ?series into an ABS filter string. Author/series
    // names are resolved to ids via one search call (the row links only carry
    // names). Returns null when nothing matches (→ empty listing).
    private async Task<string?> ResolveFilterAsync(CancellationToken ct)
    {
        // A ready-made facet filter (?filter=series.<b64-id>, as the structured row
        // links build). Decode the group for the label; the name is resolved from
        // the fetched items in RefineFilterLabel (no extra API call).
        if (!string.IsNullOrEmpty(Filter))
        {
            if (AbsFilter.Decode(Filter) is { } d)
            {
                _filterGroup = d.Group; _filterValue = d.Value;
                FilterType = Humanize(d.Group);
                // genres/tags/narrators filter by NAME — the decoded value IS the label.
                if (d.Group is "genres" or "tags" or "narrators") FilterName = d.Value;
            }
            else { FilterType = "Filter"; }
            return Filter;
        }

        if (!string.IsNullOrWhiteSpace(Author))
        {
            var r = await _api.SearchAsync(Id, Author!.Trim(), SearchLimit, ct);
            var a = r.Authors.FirstOrDefault(x => string.Equals(x.Name, Author!.Trim(), StringComparison.OrdinalIgnoreCase));
            FilterType = "Author";
            if (a is not null) { FilterName = a.Name; return AbsFilter.Encode("authors", a.Id); }
            FilterName = Author; return "authors.__none__";
        }
        if (!string.IsNullOrWhiteSpace(Series))
        {
            var name = Series!.Trim();
            var r = await _api.SearchAsync(Id, name, SearchLimit, ct);
            var s = r.Series.FirstOrDefault(x => string.Equals(x.Series.Name, name, StringComparison.OrdinalIgnoreCase));
            FilterType = "Series";
            if (s is not null) { FilterName = s.Series.Name; return AbsFilter.Encode("series", s.Series.Id); }
            FilterName = name; return "series.__none__";
        }
        return null;
    }

    // Resolve a facet filter's display name from the fetched page's batch metadata
    // — the filtered items carry the matching series/author ref (id + name), so no
    // extra call is needed. Leaves FilterName null (→ just the type) when nothing
    // matches, e.g. an empty result set.
    private void RefineFilterLabel()
    {
        if (_filterValue is null) return;
        foreach (var media in _structured.Values)
        {
            if (_filterGroup == "series")
            {
                var s = media.Metadata?.Series?.FirstOrDefault(x => x.Id == _filterValue);
                if (s is not null) { FilterName = s.Name; return; }
            }
            else if (_filterGroup == "authors")
            {
                var a = media.Metadata?.Authors?.FirstOrDefault(x => x.Id == _filterValue);
                if (a is not null) { FilterName = a.Name; return; }
            }
        }
    }

    private static string Humanize(string group) => group switch
    {
        "authors" => "Author",
        "series" => "Series",
        "genres" => "Genre",
        "tags" => "Tag",
        "narrators" => "Narrator",
        _ => group.Length > 0 ? char.ToUpperInvariant(group[0]) + group[1..] : group
    };

    public bool IsFiltered => FilterType is not null;

    // One shared builder for every library URL (page + row partial).
    public LibraryLinks Links => new(Id, Filter, Author, Series, Sort, Desc);
}
