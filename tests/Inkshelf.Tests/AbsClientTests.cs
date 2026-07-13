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
    public async Task GetItemsAsync_appends_sort_and_desc()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"results":[],"total":0,"limit":10,"page":0}"""));
        await Client(h).GetItemsAsync("acc", "lib1", 0, 10, filter: null, sort: "addedAt", desc: true);
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("addedAt", q["sort"]);
        Assert.Equal("1", q["desc"]);
    }

    [Fact]
    public async Task GetItemDetailAsync_parses_ebook_and_metadata()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"media":{"metadata":{"title":"Vol 1","authorName":"A Artist","authors":[{"id":"a1","name":"A Artist"}],"series":[{"id":"s1","name":"Saga","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"Vol1.cbz","size":123,"mtimeMs":999}}}}"""));
        var d = await Client(h).GetItemDetailAsync("acc", "i1");
        Assert.Equal("Vol 1", d.Media!.Metadata!.Title);
        Assert.Equal("cbz", d.Media!.EbookFile!.EbookFormat);
        Assert.Equal(123, d.Media!.EbookFile!.Metadata!.Size);
        Assert.Equal(999, d.Media!.EbookFile!.Metadata!.MtimeMs);
        Assert.Equal("Vol1.cbz", d.Media!.EbookFile!.Metadata!.Filename);
        Assert.Equal("/api/items/i1", h.Last!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetItemsMetadataBatchAsync_posts_ids_and_maps_structured_metadata()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryItems":[{"id":"i1","media":{"metadata":{"title":"Vol 1","authorName":"A, B","seriesName":"Saga, Part 2 #1","authors":[{"id":"a1","name":"A"},{"id":"a2","name":"B"}],"series":[{"id":"s1","name":"Saga, Part 2","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"v1.cbz","size":42,"mtimeMs":7}}}}]}"""));
        var map = await Client(h).GetItemsMetadataBatchAsync("acc", new[] { "i1" });
        Assert.Equal(HttpMethod.Post, h.Last!.Method);
        Assert.Equal("/api/items/batch/get", h.Last!.RequestUri!.AbsolutePath);
        var md = map["i1"].Metadata!;
        Assert.Equal(2, md.Authors!.Count);
        Assert.Equal("a2", md.Authors[1].Id);
        // A single series name containing a comma stays one entry.
        Assert.Single(md.Series!);
        Assert.Equal("Saga, Part 2", md.Series![0].Name);
        Assert.Equal("1", md.Series![0].Sequence);
        // ebookFile carries the cache-key inputs (size + mtime).
        Assert.Equal(42, map["i1"].EbookFile!.Metadata!.Size);
        Assert.Equal(7, map["i1"].EbookFile!.Metadata!.MtimeMs);
    }

    [Fact]
    public async Task GetItemsMetadataBatchAsync_empty_skips_call()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"libraryItems":[]}"""));
        var map = await Client(h).GetItemsMetadataBatchAsync("acc", System.Array.Empty<string>());
        Assert.Empty(map);
        Assert.Null(h.Last); // no request made
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
