using System.Net;
using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsApiClientTests
{
    private static AbsApiClient Client(StubHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://abs.local") });

    [Fact]
    public async Task GetItemsAsync_builds_query_and_parses()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"results":[{"id":"i1","media":{"metadata":{"title":"Dune","authorName":"Herbert","seriesName":"Dune #1"}}}],"total":42,"limit":24,"page":1}"""));
        var page = await Client(h).GetItemsAsync("lib1", page: 1, limit: 24);

        Assert.Equal(42, page.Total);
        Assert.Equal("Dune", page.Results[0].Media!.Metadata!.Title);
        Assert.Equal("/api/libraries/lib1/items", h.Last!.RequestUri!.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("24", q["limit"]);
        Assert.Equal("1", q["page"]);
        Assert.Null(q["minified"]);
        Assert.Null(q["filter"]);
    }

    [Fact]
    public async Task GetItemsAsync_appends_filter_when_set()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"results":[],"total":0,"limit":10,"page":0}"""));
        await Client(h).GetItemsAsync("lib1", 0, 10, filter: "series.czE=");
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("series.czE=", q["filter"]);
    }

    [Fact]
    public async Task GetItemsAsync_appends_sort_and_desc()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"results":[],"total":0,"limit":10,"page":0}"""));
        await Client(h).GetItemsAsync("lib1", 0, 10, filter: null, sort: "addedAt", desc: true);
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("addedAt", q["sort"]);
        Assert.Equal("1", q["desc"]);
    }

    [Fact]
    public async Task SearchAsync_parses_groups()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"book":[{"libraryItem":{"id":"i1","media":{"metadata":{"title":"Dune"}}}}],"series":[{"series":{"id":"s1","name":"Dune"}}],"authors":[{"id":"a1","name":"Herbert","numBooks":6}]}"""));
        var r = await Client(h).SearchAsync("lib1", "dune", 25);
        Assert.Equal("Dune", r.Book[0].LibraryItem.Media!.Metadata!.Title);
        Assert.Equal("s1", r.Series[0].Series.Id);
        Assert.Equal("Herbert", r.Authors[0].Name);
        Assert.Equal("/api/libraries/lib1/search", h.Last!.RequestUri!.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query);
        Assert.Equal("dune", q["q"]);
        Assert.Equal("25", q["limit"]);
    }

    [Fact]
    public async Task SearchAsync_parses_ebookFile_format()
    {
        // Search results use the expanded shape: format lives in ebookFile, not
        // at media.ebookFormat — the row falls back to it to show ebook links.
        var h = new StubHandler(_ => StubHandler.Json(
            """{"book":[{"libraryItem":{"id":"i1","media":{"metadata":{"title":"Tanya"},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"t.cbz","size":1,"mtimeMs":2}}}}}],"series":[],"authors":[]}"""));
        var r = await Client(h).SearchAsync("lib1", "tanya", 25);
        var media = r.Book[0].LibraryItem.Media!;
        Assert.Null(media.EbookFormat);
        Assert.Equal("cbz", media.EbookFile!.EbookFormat);
    }

    [Fact]
    public async Task GetItemDetailAsync_parses_ebook_and_metadata()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"media":{"metadata":{"title":"Vol 1","authorName":"A Artist","authors":[{"id":"a1","name":"A Artist"}],"series":[{"id":"s1","name":"Saga","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"Vol1.cbz","size":123,"mtimeMs":999}}}}"""));
        var d = await Client(h).GetItemDetailAsync("i1");
        Assert.Equal("Vol 1", d.Media!.Metadata!.Title);
        Assert.Equal("cbz", d.Media!.EbookFile!.EbookFormat);
        Assert.Equal(123, d.Media!.EbookFile!.Metadata!.Size);
        Assert.Equal("/api/items/i1", h.Last!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetItemsMetadataBatchAsync_posts_ids_and_maps_structured_metadata()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryItems":[{"id":"i1","media":{"metadata":{"title":"Vol 1","authorName":"A, B","seriesName":"Saga, Part 2 #1","authors":[{"id":"a1","name":"A"},{"id":"a2","name":"B"}],"series":[{"id":"s1","name":"Saga, Part 2","sequence":"1"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"v1.cbz","size":42,"mtimeMs":7}}}}]}"""));
        var map = await Client(h).GetItemsMetadataBatchAsync(new[] { "i1" });
        Assert.Equal(HttpMethod.Post, h.Last!.Method);
        Assert.Equal("/api/items/batch/get", h.Last!.RequestUri!.AbsolutePath);
        var md = map["i1"].Metadata!;
        Assert.Equal(2, md.Authors!.Count);
        Assert.Single(md.Series!);
        Assert.Equal("Saga, Part 2", md.Series![0].Name);
        Assert.Equal(42, map["i1"].EbookFile!.Metadata!.Size);
    }

    [Fact]
    public async Task GetItemsMetadataBatchAsync_empty_skips_call()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"libraryItems":[]}"""));
        var map = await Client(h).GetItemsMetadataBatchAsync(System.Array.Empty<string>());
        Assert.Empty(map);
        Assert.Null(h.Last);
    }

    [Fact]
    public async Task GetLibrariesAsync_parses()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraries":[{"id":"l1","name":"Books","mediaType":"book"}]}"""));
        var libs = await Client(h).GetLibrariesAsync();
        Assert.Equal("Books", libs.Single().Name);
    }

    [Fact]
    public async Task GetLibrariesAsync_throws_on_500()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(h).GetLibrariesAsync());
    }

    [Fact]
    public async Task GetFinishedItemIdsAsync_returns_only_finished_with_ids()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"mediaProgress":[{"libraryItemId":"i1","isFinished":true},{"libraryItemId":"i2","isFinished":false},{"libraryItemId":null,"isFinished":true}]}"""));
        var set = await Client(h).GetFinishedItemIdsAsync();
        Assert.Equal("/api/me", h.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("i1", set);
        Assert.DoesNotContain("i2", set);   // not finished
        Assert.Single(set);                 // null libraryItemId ignored
    }

    [Fact]
    public async Task GetFinishedItemIdsAsync_tolerates_empty_progress()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"mediaProgress":[]}"""));
        Assert.Empty(await Client(h).GetFinishedItemIdsAsync());
    }

    [Fact]
    public async Task SetReadAsync_patches_progress_with_isFinished_true()
    {
        var h = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
        await Client(h).SetReadAsync("item1", finished: true);
        Assert.Equal(HttpMethod.Patch, h.Last!.Method);
        Assert.Equal("/api/me/progress/item1", h.Last!.RequestUri!.AbsolutePath);
        var body = await h.Last!.Content!.ReadAsStringAsync();
        Assert.Contains("\"isFinished\":true", body);
    }

    [Fact]
    public async Task SetReadAsync_patches_isFinished_false_to_unmark()
    {
        var h = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
        await Client(h).SetReadAsync("item1", finished: false);
        var body = await h.Last!.Content!.ReadAsStringAsync();
        Assert.Contains("\"isFinished\":false", body);
    }
}
