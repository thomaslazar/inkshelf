using Inkshelf.Abs;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class IndexModel : PageModel
{
    private readonly AbsApiClient _api;
    public IndexModel(AbsApiClient api) { _api = api; }

    public List<AbsLibrary> Libraries { get; private set; } = new();

    // Deployed build version (same source as the ABS User-Agent), shown on the
    // libraries page so you can tell what's actually running.
    public string Version { get; } = typeof(IndexModel).Assembly.GetName().Version?.ToString(3) ?? "0";

    public async Task<IActionResult> OnGetAsync([FromQuery] string? all, CancellationToken ct)
    {
        var fav = Favorites.Read(Request);
        if (fav is not null && string.IsNullOrEmpty(all))
            return Redirect($"/library/{fav}");
        Libraries = await _api.GetLibrariesAsync(ct);
        return Page();
    }
}
