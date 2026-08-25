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

        // A FRESH store on a fresh context, on purpose: this pins the COOKIE
        // round-trip, which the same-instance path would mask. Move the Set-Cookie
        // value into the request cookies of that context.
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_session={value}";

        var read = Make(ctx2).Read();
        Assert.Equal(new Tokens("acc", "ref"), read);
    }

    [Fact]
    public void Read_sees_a_Save_on_the_same_instance()
    {
        // AbsAuthHandler refreshes mid-request and Saves; Save writes a RESPONSE
        // cookie, so a Read falling back to the (immutable) request cookies would
        // hand out the access token ABS just rejected — to a download ticket or a
        // queued conversion job.
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = "inkshelf_session=stale-and-unreadable";
        var store = Make(ctx);
        store.Save(new Tokens("fresh-acc", "fresh-ref"));

        Assert.Equal(new Tokens("fresh-acc", "fresh-ref"), store.Read());
    }

    [Fact]
    public void Read_returns_null_after_Clear_on_the_same_instance()
    {
        var ctx = new DefaultHttpContext();
        var store = Make(ctx);
        store.Save(new Tokens("acc", "ref"));
        store.Clear();

        Assert.Null(store.Read());
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

    [Fact]
    public void Save_then_Read_roundtrips_the_username()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(new Tokens("acc", "ref", "alice"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_session={value}";

        Assert.Equal(new Tokens("acc", "ref", "alice"), Make(ctx2).Read());
    }

    [Fact]
    public void Read_accepts_a_cookie_written_before_the_username_was_stored()
    {
        // THE upgrade test. A two-field cookie predates this feature; rejecting it
        // would sign every logged-in device out the moment the new build deploys.
        var payload = DataProtectionProvider.Create("inkshelf-tests")
            .CreateProtector("inkshelf.session.v1").Protect("acc\nref");
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"inkshelf_session={payload}";

        var read = Make(ctx).Read();

        Assert.NotNull(read);
        Assert.Equal("acc", read!.Access);
        Assert.Equal("ref", read.Refresh);
        Assert.Equal("", read.Username);   // not known yet, and that is fine
    }

    [Fact]
    public void A_username_containing_a_newline_cannot_corrupt_the_tokens()
    {
        // Why the name is stored last: a newline in it must not shift a token field.
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(new Tokens("acc", "ref", "ev\nil"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_session={value}";

        var read = Make(ctx2).Read();

        Assert.NotNull(read);
        Assert.Equal("acc", read!.Access);
        Assert.Equal("ref", read.Refresh);
    }
}
