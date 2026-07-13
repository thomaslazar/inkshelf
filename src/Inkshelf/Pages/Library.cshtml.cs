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
    [FromQuery] public string? Filter { get; set; }

    public bool IsFavorite { get; private set; }
    public bool IsSearch => !string.IsNullOrWhiteSpace(Q);

    public List<AbsItem> Items { get; private set; } = new();
    public Pager Pager { get; private set; } = new(0, PageSize, 0);
    public AbsSearchResults? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        IsFavorite = Favorites.Read(Request) == Id;

        if (IsSearch)
        {
            Search = await _session.ExecuteAsync(
                (tok, c) => _client.SearchAsync(tok, Id, Q!.Trim(), SearchLimit, c), ct);
        }
        else
        {
            var zeroPage = Math.Max(0, page - 1);
            var result = await _session.ExecuteAsync(
                (tok, c) => _client.GetItemsAsync(tok, Id, zeroPage, PageSize, Filter, c), ct);
            Items = result.Results;
            Pager = new Pager(result.Page, result.Limit <= 0 ? PageSize : result.Limit, result.Total);
        }
        return Page();
    }

    public string AuthorHref(string authorId) =>
        $"/library/{Id}?filter={Uri.EscapeDataString(AbsFilter.Encode("authors", authorId))}";
    public string SeriesHref(string seriesId) =>
        $"/library/{Id}?filter={Uri.EscapeDataString(AbsFilter.Encode("series", seriesId))}";
}
