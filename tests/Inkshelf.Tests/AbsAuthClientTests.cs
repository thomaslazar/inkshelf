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
}
