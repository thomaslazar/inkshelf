using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Inkshelf.Abs;
using Inkshelf.Auth;

namespace Inkshelf.Tests;

public class AbsSessionTests
{
    private static (AbsSession session, TokenStore store) Make(HttpContext ctx, AbsClient client)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var store = new TokenStore(dp, new HttpContextAccessor { HttpContext = ctx });
        return (new AbsSession(store, client), store);
    }

    private static HttpContext WithTokens(Tokens t)
    {
        // write cookie on one context, replay into a request context
        var w = new DefaultHttpContext();
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        new TokenStore(dp, new HttpContextAccessor { HttpContext = w }).Save(t);
        var value = w.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"inkshelf_session={value}";
        return ctx;
    }

    [Fact]
    public async Task ExecuteAsync_throws_AbsAuth_when_no_tokens()
    {
        var client = new AbsClient(new HttpClient(new StubHandler(_ => StubHandler.Json("{}"))));
        var (session, _) = Make(new DefaultHttpContext(), client);
        await Assert.ThrowsAsync<AbsAuthException>(
            () => session.ExecuteAsync((tok, ct) => Task.FromResult(tok)));
    }

    [Fact]
    public async Task ExecuteAsync_refreshes_then_retries_on_401()
    {
        // AbsClient stub: /auth/refresh returns new tokens
        var refreshClient = new AbsClient(new HttpClient(new StubHandler(_ =>
            StubHandler.Json("""{"user":{"accessToken":"new","refreshToken":"newref"}}""")))
            { BaseAddress = new Uri("http://abs.local") });
        var (session, _) = Make(WithTokens(new Tokens("old", "ref")), refreshClient);

        var calls = 0;
        var result = await session.ExecuteAsync((token, ct) =>
        {
            calls++;
            if (token == "old") throw new AbsUnauthorizedException();
            return Task.FromResult(token);
        });

        Assert.Equal(2, calls);
        Assert.Equal("new", result);
    }

    [Fact]
    public async Task ExecuteAsync_throws_AbsAuth_when_refresh_fails()
    {
        var badRefresh = new AbsClient(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("http://abs.local") });
        var (session, _) = Make(WithTokens(new Tokens("old", "ref")), badRefresh);

        await Assert.ThrowsAsync<AbsAuthException>(() =>
            session.ExecuteAsync<string>((token, ct) => throw new AbsUnauthorizedException()));
    }
}
