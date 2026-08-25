using Microsoft.AspNetCore.DataProtection;

namespace Inkshelf.Auth;

public class TokenStore
{
    private const string CookieName = "inkshelf_session";
    private readonly IDataProtector _protector;
    private readonly IHttpContextAccessor _accessor;
    private readonly AbsOptions _options;
    // Save writes to the RESPONSE; Request.Cookies is fixed for the life of the
    // request, so without this a mid-request refresh stays invisible to every later
    // Read() — and a bearer captured after it (a download ticket, a queued
    // conversion job) would be the one ABS just rejected. Scoped service: one
    // instance per request.
    private Tokens? _saved;

    public TokenStore(IDataProtectionProvider dp, IHttpContextAccessor accessor, AbsOptions options)
    {
        _protector = dp.CreateProtector("inkshelf.session.v1");
        _accessor = accessor;
        _options = options;
    }

    private HttpContext Ctx => _accessor.HttpContext
        ?? throw new InvalidOperationException("No HttpContext.");

    // For the request log only: whether a session cookie was PRESENT, never whether
    // it decrypts. Lets the log tell a browser's own request from a download
    // manager's cookie-less re-request without leaking the cookie name.
    public static bool HasSessionCookie(HttpRequest request) => request.Cookies.ContainsKey(CookieName);

    public void Save(Tokens tokens)
    {
        _saved = tokens;
        // access \n refresh — neither ABS token contains a newline (JWTs are base64url.compact)
        var payload = _protector.Protect($"{tokens.Access}\n{tokens.Refresh}");
        Ctx.Response.Cookies.Append(CookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = _options.ForceSecureCookies || Ctx.Request.IsHttps,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/"
        });
    }

    public Tokens? Read()
    {
        if (_saved is not null) return _saved;
        var raw = Ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = _protector.Unprotect(raw).Split('\n', 2);
            return parts.Length == 2 ? new Tokens(parts[0], parts[1]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // tampered / key rotated
        }
    }

    public void Clear()
    {
        _saved = null;
        Ctx.Response.Cookies.Delete(CookieName);
    }
}
