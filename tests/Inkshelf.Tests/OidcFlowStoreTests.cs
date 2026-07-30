using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Inkshelf;
using Inkshelf.Auth;

namespace Inkshelf.Tests;

public class OidcFlowStoreTests
{
    private static OidcFlowStore Make(HttpContext ctx, AbsOptions? options = null)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new OidcFlowStore(dp, accessor, options ?? new AbsOptions());
    }

    private static readonly OidcFlow Flow =
        new("st8", "verifier-abc", "connect.sid=s%3Aabc; auth_method=openid-mobile");

    [Fact]
    public void Save_then_Read_roundtrips()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(Flow);

        // move the Set-Cookie value into the request cookies of a fresh context
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_oidc={value}";

        Assert.Equal(Flow, Make(ctx2).Read());
    }

    [Fact]
    public void Read_returns_null_when_absent() =>
        Assert.Null(Make(new DefaultHttpContext()).Read());

    [Fact]
    public void Read_returns_null_when_tampered()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = "inkshelf_oidc=not-a-valid-token";
        Assert.Null(Make(ctx).Read());
    }

    [Fact]
    public void Save_scopes_the_cookie_to_oidc_and_stays_lax()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(Flow);
        var setCookie = ctx.Response.Headers.SetCookie.ToString();

        Assert.Contains("path=/oidc", setCookie, StringComparison.OrdinalIgnoreCase);
        // Lax is load-bearing: the callback is a cross-site top-level navigation,
        // and Strict would strip the cookie.
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        Make(ctx, new AbsOptions { ForceSecureCookies = true }).Save(Flow);
        Assert.Contains("secure", ctx.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(Flow);
        Assert.DoesNotContain("secure", ctx.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
