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
        Assert.Null(q["minified"]); // full item JSON so author/series carry ids
        Assert.Null(q["filter"]);
        Assert.Equal("Bearer acc", h.Last!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetItemsAsync_full_metadata_parses_authors_and_series()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"results":[{"id":"i1","media":{"metadata":{"title":"Dune","authors":[{"id":"a1","name":"Herbert"}],"series":[{"id":"s1","name":"Dune","sequence":"1"}]}}}],"total":1,"limit":10,"page":0}"""));
        var page = await Client(h).GetItemsAsync("acc", "lib1", 0, 10);
        var m = page.Results[0].Media!.Metadata!;
        Assert.Equal("a1", m.Authors![0].Id);
        Assert.Equal("s1", m.Series![0].Id);
        Assert.Equal("1", m.Series![0].Sequence);
    }

    [Fact]
    public async Task GetItemsAsync_appends_filter_when_set()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"results":[],"total":0,"limit":10,"page":0}"""));
        await Client(h).GetItemsAsync("acc", "lib1", 0, 10, filter: "series.czE=");
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("series.czE=", q["filter"]);
    }

    [Fact]
    public async Task SearchAsync_parses_groups()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"book":[{"libraryItem":{"id":"i1","media":{"metadata":{"title":"Dune"}}}}],"series":[{"series":{"id":"s1","name":"Dune"}}],"authors":[{"id":"a1","name":"Herbert","numBooks":6}]}"""));
        var r = await Client(h).SearchAsync("acc", "lib1", "dune", 25);
        Assert.Equal("Dune", r.Book[0].LibraryItem.Media!.Metadata!.Title);
        Assert.Equal("s1", r.Series[0].Series.Id);
        Assert.Equal("Herbert", r.Authors[0].Name);
        Assert.Equal("/api/libraries/lib1/search", h.Last!.RequestUri!.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("dune", q["q"]);
        Assert.Equal("25", q["limit"]);
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

    [Fact]
    public async Task GetLibrariesAsync_throws_on_500()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(h).GetLibrariesAsync("acc"));
    }
}
