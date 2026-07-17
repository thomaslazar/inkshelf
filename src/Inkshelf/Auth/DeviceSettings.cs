using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Auth;

// Per-device rendering preferences, stored in a server-written cookie. Modeled
// on Favorites: static Read/Set, same cookie-flag rules. Distinct from the
// JS-written "scr" device probe — this is user CHOICE, scr is device TRUTH; the
// two are read together where conversion happens.
public sealed record DeviceSettings(bool Retina, bool Grayscale)
{
    public const string Cookie = "inkshelf_settings";
    public static readonly DeviceSettings Default = new(false, false);

    // Compact positional flags "<retina><grayscale>", each 0 or 1 (e.g. "10").
    // No cookie-reserved characters, so the value survives cookie encoding intact.
    public string Serialize() => $"{(Retina ? 1 : 0)}{(Grayscale ? 1 : 0)}";

    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || v is null || v.Length < 2) return Default;
        return new DeviceSettings(v[0] == '1', v[1] == '1');
    }

    public static void Set(HttpResponse res, DeviceSettings settings)
    {
        var forceSecure = res.HttpContext.RequestServices?.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
        res.Cookies.Append(Cookie, settings.Serialize(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = forceSecure || res.HttpContext.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });
    }
}
