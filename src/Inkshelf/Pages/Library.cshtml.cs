using Inkshelf.Abs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class LibraryModel : PageModel
{
    public const int PageSize = 24;
    private readonly AbsSession _session;
    private readonly AbsClient _client;
    public LibraryModel(AbsSession session, AbsClient client) { _session = session; _client = client; }

    [FromRoute] public string Id { get; set; } = "";
    public List<AbsItem> Items { get; private set; } = new();
    public Pager Pager { get; private set; } = new(0, PageSize, 0);

    public async Task<IActionResult> OnGetAsync([FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();

        var zeroPage = Math.Max(0, page - 1);
        var result = await _session.ExecuteAsync(
            (tok, c) => _client.GetItemsAsync(tok, Id, zeroPage, PageSize, c), ct);
        Items = result.Results;
        Pager = new Pager(result.Page, result.Limit <= 0 ? PageSize : result.Limit, result.Total);
        return Page();
    }
}
