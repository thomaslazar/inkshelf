namespace Inkshelf;

public static class Favorites
{
    public const string Cookie = "inkshelf_fav_library";

    public static string? Read(HttpRequest req) =>
        req.Cookies.TryGetValue(Cookie, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static void Set(HttpResponse res, string id) =>
        res.Cookies.Append(Cookie, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = res.HttpContext.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });

    public static void Clear(HttpResponse res) =>
        res.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
}
