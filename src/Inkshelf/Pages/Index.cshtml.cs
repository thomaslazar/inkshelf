using Inkshelf.Abs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class IndexModel : PageModel
{
    private readonly AbsSession _session;
    private readonly AbsClient _client;
    public IndexModel(AbsSession session, AbsClient client) { _session = session; _client = client; }

    public List<AbsLibrary> Libraries { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync([FromQuery] string? all, CancellationToken ct)
    {
        var fav = Favorites.Read(Request);
        if (fav is not null && string.IsNullOrEmpty(all))
            return Redirect($"/library/{fav}");
        Libraries = await _session.ExecuteAsync((tok, c) => _client.GetLibrariesAsync(tok, c), ct);
        return Page();
    }
}
