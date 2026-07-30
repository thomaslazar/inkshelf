using Microsoft.AspNetCore.DataProtection;

namespace Inkshelf.Auth;

// One in-flight OIDC login: the state we echo back, the PKCE verifier, and the
// ABS cookies from leg 1 that the token exchange refuses to work without.
public record OidcFlow(string State, string Verifier, string Cookies);

// Same shape as TokenStore — the flow lives in an encrypted cookie so the app
// stays stateless, and expires on its own if the login is abandoned.
public class OidcFlowStore
{
    private const string CookieName = "inkshelf_oidc";
    private readonly IDataProtector _protector;
    private readonly IHttpContextAccessor _accessor;
    private readonly AbsOptions _options;

    public OidcFlowStore(IDataProtectionProvider dp, IHttpContextAccessor accessor, AbsOptions options)
    {
        _protector = dp.CreateProtector("inkshelf.oidc.v1");
        _accessor = accessor;
        _options = options;
    }

    private HttpContext Ctx => _accessor.HttpContext
        ?? throw new InvalidOperationException("No HttpContext.");

    public void Save(OidcFlow flow)
    {
        // state \n verifier \n cookies — none of the three can contain a newline
        // (base64url, base64url, and a Cookie header value).
        var payload = _protector.Protect($"{flow.State}\n{flow.Verifier}\n{flow.Cookies}");
        Ctx.Response.Cookies.Append(CookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            // Lax, not Strict: the callback arrives as a cross-site top-level
            // navigation from the provider, which Strict would strip.
            SameSite = SameSiteMode.Lax,
            Secure = _options.ForceSecureCookies || Ctx.Request.IsHttps,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/oidc"
        });
    }

    public OidcFlow? Read()
    {
        var raw = Ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = _protector.Unprotect(raw).Split('\n', 3);
            return parts.Length == 3 ? new OidcFlow(parts[0], parts[1], parts[2]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // tampered / key rotated
        }
    }

    public void Clear() => Ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/oidc" });
}
