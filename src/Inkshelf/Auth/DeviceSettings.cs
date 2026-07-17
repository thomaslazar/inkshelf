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

    // Compact "r=<0|1>&g=<0|1>". '&' and '=' are valid in a cookie value.
    public string Serialize() => $"r={(Retina ? 1 : 0)}&g={(Grayscale ? 1 : 0)}";

    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v)) return Default;
        bool retina = false, grayscale = false;
        foreach (var part in v.Split('&'))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;
            var on = kv[1] == "1";
            if (kv[0] == "r") retina = on;
            else if (kv[0] == "g") grayscale = on;
        }
        return new DeviceSettings(retina, grayscale);
    }

    public static void Set(HttpResponse res, DeviceSettings settings)
    {
        var forceSecure = res.HttpContext.RequestServices?.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
        var isSecure = forceSecure || res.HttpContext.Request.IsHttps;
        var maxAge = (long)TimeSpan.FromDays(365).TotalSeconds;

        var setCookieValue = $"{Cookie}={settings.Serialize()}; Path=/; HttpOnly; SameSite=Lax; max-age={maxAge}";
        if (isSecure)
            setCookieValue += "; secure";

        res.Headers.Append("Set-Cookie", setCookieValue);
    }
}
