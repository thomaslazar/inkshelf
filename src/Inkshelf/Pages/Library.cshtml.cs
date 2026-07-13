using Inkshelf.Abs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class LibraryModel : PageModel
{
    public const int PageSize = 10;
    public const int SearchLimit = 25;
    private readonly AbsSession _session;
    private readonly AbsClient _client;
    public LibraryModel(AbsSession session, AbsClient client) { _session = session; _client = client; }

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

        var libraries = await _session.ExecuteAsync((tok, c) => _client.GetLibrariesAsync(tok, c), ct);
        LibraryName = libraries.FirstOrDefault(l => l.Id == Id)?.Name ?? "Library";

        if (IsSearch)
        {
            SearchResults = await _session.ExecuteAsync(
                (tok, c) => _client.SearchAsync(tok, Id, Q!.Trim(), SearchLimit, c), ct);
            return Page();
        }

        var filter = await ResolveFilterAsync(ct);
        var zeroPage = Math.Max(0, page - 1);
        var result = await _session.ExecuteAsync(
            (tok, c) => _client.GetItemsAsync(tok, Id, zeroPage, PageSize, filter, Sort, Desc, c), ct);
        Items = result.Results;
        Pager = new Pager(result.Page, result.Limit <= 0 ? PageSize : result.Limit, result.Total);
        return Page();
    }

    // Turn ?filter / ?author / ?series into an ABS filter string. Author/series
    // names are resolved to ids via one search call (the row links only carry
    // names). Returns null when nothing matches (→ empty listing).
    private async Task<string?> ResolveFilterAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(Filter)) { FilterLabel = "filter"; return Filter; }

        if (!string.IsNullOrWhiteSpace(Author))
        {
            var r = await _session.ExecuteAsync((tok, c) => _client.SearchAsync(tok, Id, Author!.Trim(), SearchLimit, c), ct);
            var a = r.Authors.FirstOrDefault(x => string.Equals(x.Name, Author!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (a is not null) { FilterLabel = a.Name; return AbsFilter.Encode("authors", a.Id); }
            FilterLabel = Author; return "authors.__none__";
        }
        if (!string.IsNullOrWhiteSpace(Series))
        {
            var name = Series!.Trim();
            var r = await _session.ExecuteAsync((tok, c) => _client.SearchAsync(tok, Id, name, SearchLimit, c), ct);
            var s = r.Series.FirstOrDefault(x => string.Equals(x.Series.Name, name, StringComparison.OrdinalIgnoreCase));
            if (s is not null) { FilterLabel = s.Series.Name; return AbsFilter.Encode("series", s.Series.Id); }
            FilterLabel = name; return "series.__none__";
        }
        return null;
    }

    public bool IsFiltered => FilterLabel is not null;

    // Build a listing URL for this library carrying the active facet, plus the
    // given sort/page overrides. page resets to 1 on a sort change.
    public string ListingHref(string? sort, bool desc, int page)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(Filter)) qs.Add("filter=" + Uri.EscapeDataString(Filter));
        if (!string.IsNullOrEmpty(Author)) qs.Add("author=" + Uri.EscapeDataString(Author));
        if (!string.IsNullOrEmpty(Series)) qs.Add("series=" + Uri.EscapeDataString(Series));
        if (!string.IsNullOrEmpty(sort)) { qs.Add("sort=" + Uri.EscapeDataString(sort)); if (desc) qs.Add("desc=1"); }
        if (page > 1) qs.Add("page=" + page);
        return $"/library/{Id}" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
    }

    public string SortHref(string field)
    {
        var (s, d) = SortLinks.Next(field, Sort, Desc);
        return ListingHref(s, d, 1);
    }

    // Row link helpers (names — resolved at click time).
    public string AuthorHref(string name) => $"/library/{Id}?author={Uri.EscapeDataString(name)}";
    public string SeriesHref(string name) => $"/library/{Id}?series={Uri.EscapeDataString(name)}";
    // Search-group link helpers (ids — direct filter).
    public string AuthorFilterHref(string authorId) => $"/library/{Id}?filter={Uri.EscapeDataString(AbsFilter.Encode("authors", authorId))}";
    public string SeriesFilterHref(string seriesId) => $"/library/{Id}?filter={Uri.EscapeDataString(AbsFilter.Encode("series", seriesId))}";
}
