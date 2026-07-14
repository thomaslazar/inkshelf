using Inkshelf.Abs;

namespace Inkshelf.Pages;

// Single source of truth for library listing URLs — used by the page (sort bar,
// pager) and the item-row partial. Built from the active facet context so
// listing/sort links carry the current filter forward, while row links (by
// author/series name, or by id) need only the library id.
public sealed class LibraryLinks
{
    private readonly string _id;
    private readonly string? _filter;
    private readonly string? _author;
    private readonly string? _series;
    private readonly string? _sort;
    private readonly bool _desc;

    public LibraryLinks(string id, string? filter, string? author, string? series, string? sort, bool desc)
    {
        _id = id; _filter = filter; _author = author; _series = series; _sort = sort; _desc = desc;
    }

    // Row / search-group link: filter by an author or series id.
    public string FilterHref(string group, string id) =>
        $"/library/{_id}?filter={Uri.EscapeDataString(AbsFilter.Encode(group, id))}";

    // Row link: filter by author NAME (resolved to an id at click time).
    public string AuthorHref(string name) =>
        $"/library/{_id}?author={Uri.EscapeDataString(name)}";

    // Row link: series display can be "Name #seq"; link by the bare name.
    public string SeriesHref(string display) =>
        $"/library/{_id}?series={Uri.EscapeDataString(display.Split(" #")[0])}";

    // Listing URL carrying the active facet, plus sort/page overrides.
    // page resets to 1 on a sort change.
    public string ListingHref(string? sort, bool desc, int page)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(_filter)) qs.Add("filter=" + Uri.EscapeDataString(_filter));
        if (!string.IsNullOrEmpty(_author)) qs.Add("author=" + Uri.EscapeDataString(_author));
        if (!string.IsNullOrEmpty(_series)) qs.Add("series=" + Uri.EscapeDataString(_series));
        if (!string.IsNullOrEmpty(sort)) { qs.Add("sort=" + Uri.EscapeDataString(sort)); if (desc) qs.Add("desc=1"); }
        if (page > 1) qs.Add("page=" + page);
        return $"/library/{_id}" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
    }

    public string SortHref(string field)
    {
        var (s, d) = SortLinks.Next(field, _sort, _desc);
        return ListingHref(s, d, 1);
    }
}
