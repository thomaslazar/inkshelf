using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Auth;

// Per-device rendering preferences, stored in a server-written cookie. Modeled
// on Favorites: static Read/Set, same cookie-flag rules. Distinct from the
// JS-written "scr" device probe — this is user CHOICE, scr is device TRUTH; the
// two are read together where conversion happens.
public sealed record DeviceSettings(bool Retina, bool Grayscale, string Lang)
{
    public const string Cookie = "inkshelf_settings";
    // Retina defaults ON — most readers want crisp pages; opt out per device.
    // Lang "" = no explicit choice yet (resolved from Accept-Language at render).
    public static readonly DeviceSettings Default = new(true, false, "");

    // Two 0/1 flags "<retina><grayscale>" then an optional lowercase language
    // code, e.g. "10de" = retina on, grayscale off, lang "de" ("10" alone = no
    // language). No cookie-reserved characters, so it survives cookie encoding.
    public string Serialize() => $"{(Retina ? 1 : 0)}{(Grayscale ? 1 : 0)}{Lang}";

    public static DeviceSettings Read(HttpRequest req)
    {
        // Two 0/1 flags, then an optional language code. Legacy 2-char cookies
        // parse with Lang "" (English/resolve). Anything malformed → Default.
        if (req.Cookies.TryGetValue(Cookie, out var v) && v is { Length: >= 2 }
            && v[0] is '0' or '1' && v[1] is '0' or '1')
            return new DeviceSettings(v[0] == '1', v[1] == '1',
                SanitizeLang(v.Length > 2 ? v[2..] : ""));
        return Default;
    }

    // Accept a short lowercase code (letters + dash), else "" (→ resolve from header).
    private static string SanitizeLang(string s)
    {
        if (s.Length is 0 or > 8) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or '-')) return "";
        return s;
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
