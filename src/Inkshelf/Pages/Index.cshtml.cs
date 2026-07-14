using Inkshelf.Abs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class IndexModel : PageModel
{
    private readonly AbsApiClient _api;
    public IndexModel(AbsApiClient api) { _api = api; }

    public List<AbsLibrary> Libraries { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync([FromQuery] string? all, CancellationToken ct)
    {
        var fav = Favorites.Read(Request);
        if (fav is not null && string.IsNullOrEmpty(all))
            return Redirect($"/library/{fav}");
        Libraries = await _api.GetLibrariesAsync(ct);
        return Page();
    }
}
