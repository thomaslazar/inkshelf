using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Inkshelf.Auth;

// Per-device rendering preferences plus the favorite library, stored in one
// server-written cookie via static Read/Set. Distinct from the JS-written "scr"
// device probe — this is user CHOICE, scr is device TRUTH; the two are read
// together where conversion happens.
public sealed record DeviceSettings(bool Retina, bool Grayscale, string Lang)
{
    public const string Cookie = "inkshelf_settings";
    // Retina defaults ON — most readers want crisp pages; opt out per device.
    // Lang "" = no explicit choice yet (resolved from Accept-Language at render).
    public static readonly DeviceSettings Default = new(true, false, "");

    public const string LegacyFavCookie = "inkshelf_fav_library";

    // An init property rather than a fourth positional parameter, so the ten
    // existing `new DeviceSettings(a, b, c)` sites in the tests keep compiling —
    // those tests are the regression net for this refactor. Record equality still
    // covers it and `with { Fav = ... }` still works.
    public string Fav { get; init; } = "";

    // Keyed, NOT positional: "retina=1&gray=0&lang=de&fav=". Looks like a query
    // string because it is parsed by QueryHelpers, but it is a cookie value —
    // Response.Cookies.Append escapes the & and = to %26/%3D and the request side
    // unescapes them. Every key is always written, including empty ones: Read
    // distinguishes "key present but empty" from "key absent" and they mean
    // different things for fav (see Read).
    public string Serialize() =>
        $"retina={(Retina ? 1 : 0)}&gray={(Grayscale ? 1 : 0)}"
        + $"&lang={SanitizeLang(Lang)}&fav={SanitizeId(Fav)}";

    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v))
            return Default with { Fav = LegacyFav(req) };

        // No '=' means the legacy positional shape ("10", "10de"). Written before
        // the keyed format; parsed here so existing devices keep their settings.
        if (!v.Contains('=')) return ReadLegacy(v) with { Fav = LegacyFav(req) };

        var q = QueryHelpers.ParseQuery(v);
        return new DeviceSettings(
            Flag(q, "retina", Default.Retina),
            Flag(q, "gray", Default.Grayscale),
            q.TryGetValue("lang", out var lang) ? SanitizeLang(lang.ToString()) : Default.Lang)
        {
            // PRESENCE, not emptiness. `fav=` present-but-empty means deliberately
            // un-favorited; falling back to the legacy cookie on empty would
            // resurrect a favorite the user just cleared.
            Fav = q.TryGetValue("fav", out var fav) ? SanitizeId(fav.ToString()) : LegacyFav(req),
        };
    }

    // An absent key means "not specified", which must land on the DOCUMENTED
    // default — retina defaults ON, so a plain `== "1"` would silently flip it off.
    // ParseQuery hands back a plain Dictionary whose indexer THROWS on a missing
    // key, so every lookup goes through TryGetValue.
    private static bool Flag(Dictionary<string, StringValues> q, string key, bool fallback) =>
        q.TryGetValue(key, out var v) && v.Count > 0 ? v[0] == "1" : fallback;

    // Two 0/1 flags then an optional language code, e.g. "10de". Anything
    // malformed → Default.
    private static DeviceSettings ReadLegacy(string v) =>
        v is { Length: >= 2 } && v[0] is '0' or '1' && v[1] is '0' or '1'
            ? new DeviceSettings(v[0] == '1', v[1] == '1', SanitizeLang(v.Length > 2 ? v[2..] : ""))
            : Default;

    // Accept a short lowercase code (letters + dash), else "" (→ resolve from header).
    private static string SanitizeLang(string s)
    {
        if (s.Length is 0 or > 8) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or '-')) return "";
        return s;
    }

    private static string LegacyFav(HttpRequest req) =>
        req.Cookies.TryGetValue(LegacyFavCookie, out var v) ? SanitizeId(v) : "";

    // An opaque ABS library id, so allow only what those ids use. This is a trust
    // boundary, not tidiness: `libraryId` arrives from a form POST, and a value
    // containing '&' would inject extra keys into the cookie we write. Rejecting
    // '%' also rules out double-decoding, since ParseQuery URL-decodes a value the
    // cookie layer already unescaped once.
    private static string SanitizeId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return "";
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
        // The favorite now lives in the settings cookie. Drop the old one so it
        // can't linger and shadow a later un-favorite.
        res.Cookies.Delete(LegacyFavCookie, new CookieOptions { Path = "/" });
    }
}
