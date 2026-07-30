using System.Reflection;
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

    // Deployed build version, shown on the libraries page so you can tell what's
    // actually running. InformationalVersion, not AssemblyVersion, because the
    // build appends "+<branch-or-pr>.<sha>" to it via SourceRevisionId — a release
    // image shows a bare "0.4.1", anything else says which build it came from.
    public string Version { get; } =
        typeof(IndexModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(IndexModel).Assembly.GetName().Version?.ToString(3) ?? "0";

    public async Task<IActionResult> OnGetAsync([FromQuery] string? all, CancellationToken ct)
    {
        Libraries = await _api.GetLibrariesAsync(ct);
        var settings = DeviceSettings.Read(Request);
        var fav = settings.Fav;
        if (!string.IsNullOrEmpty(fav) && string.IsNullOrEmpty(all))
        {
            // Only honor the favorite if it still exists on the ABS we're pointed
            // at now — a cookie saved against a different ABS would otherwise
            // redirect into a library this one doesn't have. Drop the stale
            // favorite and fall through to the list rather than looping on a dead
            // link.
            if (Libraries.Any(l => l.Id == fav)) return Redirect($"/library/{fav}");
            DeviceSettings.Set(Response, settings with { Fav = "" });
        }
        return Page();
    }
}
