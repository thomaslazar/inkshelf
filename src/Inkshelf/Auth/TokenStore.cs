using Microsoft.AspNetCore.DataProtection;

namespace Inkshelf.Auth;

public class TokenStore
{
    private const string CookieName = "inkshelf_session";
    private readonly IDataProtector _protector;
    private readonly IHttpContextAccessor _accessor;
    private readonly AbsOptions _options;

    public TokenStore(IDataProtectionProvider dp, IHttpContextAccessor accessor, AbsOptions options)
    {
        _protector = dp.CreateProtector("inkshelf.session.v1");
        _accessor = accessor;
        _options = options;
    }

    private HttpContext Ctx => _accessor.HttpContext
        ?? throw new InvalidOperationException("No HttpContext.");

    public void Save(Tokens tokens)
    {
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

    public void Clear() => Ctx.Response.Cookies.Delete(CookieName);
}
