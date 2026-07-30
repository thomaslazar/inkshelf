using System.Net;
using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsAuthClientTests
{
    private static AbsAuthClient Client(StubHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://abs.local") });

    [Fact]
    public async Task LoginAsync_parses_tokens_and_sets_header()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"user":{"accessToken":"acc","refreshToken":"ref"}}"""));
        var tokens = await Client(h).LoginAsync("u", "p");

        Assert.Equal(new Inkshelf.Auth.Tokens("acc", "ref"), tokens);
        Assert.Equal("/login", h.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("true", h.Last!.Headers.GetValues("x-return-tokens").Single());
    }

    [Fact]
    public async Task LoginAsync_throws_on_401()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<AbsLoginFailedException>(() => Client(h).LoginAsync("u", "bad"));
    }

    [Fact]
    public async Task RefreshAsync_sends_refresh_header_and_parses()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"user":{"accessToken":"acc2","refreshToken":"ref2"}}"""));
        var tokens = await Client(h).RefreshAsync("ref");

        Assert.Equal(new Inkshelf.Auth.Tokens("acc2", "ref2"), tokens);
        Assert.Equal("/auth/refresh", h.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("ref", h.Last!.Headers.GetValues("x-refresh-token").Single());
    }

    private static HttpResponseMessage Redirect(string location, params string[] cookies)
    {
        var res = new HttpResponseMessage(HttpStatusCode.Found);
        res.Headers.Location = new Uri(location);
        foreach (var c in cookies) res.Headers.Add("Set-Cookie", c);
        return res;
    }

    [Fact]
    public async Task StartOidcAsync_sends_pkce_params_and_returns_location_and_cookies()
    {
        var h = new StubHandler(_ => Redirect("https://idp.example/authorize?x=1",
            "connect.sid=s%3Aabc; Path=/; HttpOnly",
            "auth_method=openid-mobile; Path=/; HttpOnly"));

        var (url, cookies) = await Client(h)
            .StartOidcAsync("https://ink.example/oidc/callback", "chal", "st8");

        Assert.Equal("https://idp.example/authorize?x=1", url);
        // name=value only — we are building a request Cookie header, not storing cookies
        Assert.Equal("connect.sid=s%3Aabc; auth_method=openid-mobile", cookies);

        Assert.Equal("/auth/openid", h.Last!.RequestUri!.AbsolutePath);
        var q = h.Last!.RequestUri!.Query;
        Assert.Contains("response_type=code", q);
        Assert.Contains("code_challenge=chal", q);
        Assert.Contains("code_challenge_method=S256", q);
        Assert.Contains("state=st8", q);
        Assert.Contains(Uri.EscapeDataString("https://ink.example/oidc/callback"), q);
    }

    [Fact]
    public async Task StartOidcAsync_throws_with_body_on_400()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Invalid redirect_uri")
        });

        var ex = await Assert.ThrowsAsync<AbsOidcException>(() =>
            Client(h).StartOidcAsync("https://ink.example/oidc/callback", "chal", "st8"));
        Assert.Equal(400, ex.Status);
        Assert.Contains("Invalid redirect_uri", ex.Body);
    }

    [Fact]
    public async Task StartOidcAsync_throws_when_the_redirect_has_no_location()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));
        await Assert.ThrowsAsync<AbsOidcException>(() =>
            Client(h).StartOidcAsync("https://ink.example/oidc/callback", "chal", "st8"));
    }

    [Fact]
    public async Task CompleteOidcAsync_replays_cookies_and_verifier_and_parses_tokens()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"user":{"accessToken":"acc3","refreshToken":"ref3"}}"""));

        var tokens = await Client(h).CompleteOidcAsync(
            "the-code", "st8", "ver1", "connect.sid=s%3Aabc; auth_method=openid-mobile");

        Assert.Equal(new Inkshelf.Auth.Tokens("acc3", "ref3"), tokens);
        Assert.Equal("/auth/openid/callback", h.Last!.RequestUri!.AbsolutePath);
        var q = h.Last!.RequestUri!.Query;
        Assert.Contains("code=the-code", q);
        Assert.Contains("state=st8", q);
        Assert.Contains("code_verifier=ver1", q);
        // Both cookies matter: no connect.sid means "No session", and without
        // auth_method=openid-mobile ABS answers with a redirect instead of tokens.
        Assert.Equal("connect.sid=s%3Aabc; auth_method=openid-mobile",
            h.Last!.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task CompleteOidcAsync_throws_on_401()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized")
        });
        var ex = await Assert.ThrowsAsync<AbsOidcException>(() =>
            Client(h).CompleteOidcAsync("c", "s", "v", "connect.sid=x"));
        Assert.Equal(401, ex.Status);
    }
}
