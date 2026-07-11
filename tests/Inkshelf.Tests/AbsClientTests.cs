using System.Net;
using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsClientTests
{
    private static AbsClient Client(StubHandler h) =>
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

    [Fact]
    public async Task GetItemsAsync_builds_query_and_parses()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"results":[{"id":"i1","media":{"metadata":{"title":"Dune","authorName":"Herbert","seriesName":"Dune #1"}}}],"total":42,"limit":24,"page":1}"""));
        var page = await Client(h).GetItemsAsync("acc", "lib1", page: 1, limit: 24);

        Assert.Equal(42, page.Total);
        Assert.Equal("Dune", page.Results[0].Media!.Metadata!.Title);
        Assert.Equal("/api/libraries/lib1/items", h.Last!.RequestUri!.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("24", q["limit"]);
        Assert.Equal("1", q["page"]);
        Assert.Equal("1", q["minified"]);
        Assert.Equal("Bearer acc", h.Last!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetItemsAsync_throws_unauthorized_on_401()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<AbsUnauthorizedException>(
            () => Client(h).GetItemsAsync("acc", "lib1", 0, 24));
    }

    [Fact]
    public async Task GetLibrariesAsync_parses()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraries":[{"id":"l1","name":"Books","mediaType":"book"}]}"""));
        var libs = await Client(h).GetLibrariesAsync("acc");
        Assert.Equal("Books", libs.Single().Name);
    }
}
