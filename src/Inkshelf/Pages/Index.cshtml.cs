using Inkshelf.Abs;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class IndexModel : PageModel
{
    private readonly AbsApiClient _api;
    private readonly TokenStore _tokens;
    public IndexModel(AbsApiClient api, TokenStore tokens) { _api = api; _tokens = tokens; }

    public List<AbsLibrary> Libraries { get; private set; } = new();

    public string Version => AppVersion.Current;

    // From the session cookie, not ABS: the libraries page already decrypts it, so
    // this costs no request and still shows when ABS is unreachable.
    public string Username => _tokens.Read()?.Username ?? "";

    public async Task<IActionResult> OnGetAsync([FromQuery] string? all, CancellationToken ct)
    {
        Libraries = await _api.GetLibrariesAsync(ct);
        var settings = DeviceSettings.Read(Request);
        var fav = settings.Fav;
        if (!string.IsNullOrEmpty(fav) && string.IsNullOrEmpty(all))
        {
            // Only honor the favorite if it still exists on the ABS we're pointed
            // at now - a cookie saved against a different ABS would otherwise
            // redirect into a library this one doesn't have. Drop the stale
            // favorite and fall through to the list rather than looping on a dead
            // link.
            if (Libraries.Any(l => l.Id == fav)) return Redirect($"/library/{fav}");
            DeviceSettings.Set(Response, settings with { Fav = "" });
        }
        return Page();
    }
}
