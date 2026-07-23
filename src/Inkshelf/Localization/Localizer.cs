using Inkshelf.Auth;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Localization;

// View-facing localiser. Injected once via _ViewImports (`@inject Localizer L`)
// and used as `L["English string"]`. Resolves the request language itself (from
// the DeviceSettings cookie, then Accept-Language) so strings in the layout and
// shared partials — which have no PageModel — need no plumbing.
public sealed class Localizer
{
    private readonly LocalizationCatalog _catalog;
    private readonly IHttpContextAccessor _http;

    public Localizer(LocalizationCatalog catalog, IHttpContextAccessor http)
    {
        _catalog = catalog;
        _http = http;
    }

    public string this[string key] => _catalog.Get(CurrentLang(), key);

    public string this[string key, params object?[] args]
        => string.Format(_catalog.Get(CurrentLang(), key), args);

    // Explicit cookie choice (incl. "en") → best Accept-Language match among
    // loaded catalogs → null (English). Never writes anything.
    public string? CurrentLang()
    {
        var req = _http.HttpContext?.Request;
        if (req is null) return null;

        var chosen = DeviceSettings.Read(req).Lang;
        if (!string.IsNullOrEmpty(chosen)) return chosen;

        foreach (var h in req.GetTypedHeaders().AcceptLanguage.OrderByDescending(x => x.Quality ?? 1.0))
        {
            var code = h.Value.ToString();
            if (string.IsNullOrEmpty(code)) continue;
            if (_catalog.Has(code)) return code;
            var dash = code.IndexOf('-');
            if (dash > 0 && _catalog.Has(code[..dash])) return code[..dash];
        }
        return null;
    }
}
