using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Inkshelf;
using Inkshelf.Auth;

namespace Inkshelf.Tests;

public class TokenStoreTests
{
    private static TokenStore Make(HttpContext ctx, AbsOptions? options = null)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new TokenStore(dp, accessor, options ?? new AbsOptions());
    }

    [Fact]
    public void Save_then_Read_roundtrips()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(new Tokens("acc", "ref"));

        // move the Set-Cookie value into the request cookies of a fresh context
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_session={value}";

        var read = Make(ctx2).Read();
        Assert.Equal(new Tokens("acc", "ref"), read);
    }

    [Fact]
    public void Read_returns_null_when_absent() =>
        Assert.Null(Make(new DefaultHttpContext()).Read());

    [Fact]
    public void Read_returns_null_when_tampered()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = "inkshelf_session=not-a-valid-token";
        Assert.Null(Make(ctx).Read());
    }

    [Fact]
    public void Save_emits_root_path_cookie()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(new Tokens("acc", "ref"));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        Make(ctx, new AbsOptions { ForceSecureCookies = true }).Save(new Tokens("acc", "ref"));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false, ForceSecureCookies false
        Make(ctx).Save(new Tokens("acc", "ref"));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}
