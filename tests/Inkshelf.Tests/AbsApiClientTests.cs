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

    [Fact]
    public async Task GetItemsBatchAsync_posts_ids_and_parses_expanded_fields()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryItems":[{"id":"i1","libraryId":"lib1","media":{"metadata":{"title":"My Comic","authors":[{"id":"a1","name":"Author One"}],"series":[{"id":"s1","name":"The Sandman","sequence":"2"}]},"coverPath":"/covers/i1.jpg","ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"x.cbz","size":123,"mtimeMs":456}}}}]}"""));
        var items = await Client(h).GetItemsBatchAsync(new[] { "i1" });

        Assert.Equal("/api/items/batch/get", h.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, h.Last!.Method);
        var it = Assert.Single(items);
        Assert.Equal("i1", it.Id);
        Assert.Equal("lib1", it.LibraryId);
        Assert.Equal("My Comic", it.Media!.Metadata!.Title);
        Assert.Equal("/covers/i1.jpg", it.Media!.CoverPath);
        Assert.Equal("The Sandman", it.Media!.Metadata!.Series![0].Name);
        Assert.Equal("cbz", it.Media!.EbookFile!.EbookFormat);
        Assert.Equal(123, it.Media!.EbookFile!.Metadata!.Size);
    }

    [Fact]
    public async Task GetItemsBatchAsync_empty_ids_makes_no_call()
    {
        var h = new StubHandler(_ => StubHandler.Json("""{"libraryItems":[]}"""));
        var items = await Client(h).GetItemsBatchAsync(System.Array.Empty<string>());
        Assert.Empty(items);
        Assert.Null(h.Last); // no request issued
    }

    [Fact]
    public async Task GetItemsMetadataBatchAsync_still_returns_media_dict()
    {
        var h = new StubHandler(_ => StubHandler.Json(
            """{"libraryItems":[{"id":"i1","media":{"metadata":{"series":[{"id":"s1","name":"S"}]}}}]}"""));
        var map = await Client(h).GetItemsMetadataBatchAsync(new[] { "i1" });
        Assert.True(map.ContainsKey("i1"));
        Assert.Equal("S", map["i1"].Metadata!.Series![0].Name);
    }

    [Fact]
    public async Task GetItemDetailAsync_requests_expanded_and_parses_files_and_metadata()
    {
        var json = """{"libraryId":"lib1","libraryFiles":[{"ino":"11","fileType":"ebook","metadata":{"filename":"a.cbz","ext":".cbz","size":10,"mtimeMs":20}},{"ino":"12","fileType":"ebook","metadata":{"filename":"a.pdf","ext":".pdf","size":30,"mtimeMs":40}},{"ino":"13","fileType":"image","metadata":{"filename":"cover.jpg","ext":".jpg","size":5,"mtimeMs":6}}],"media":{"coverPath":"/c.jpg","tags":["owned","fav"],"ebookFile":{"ino":"11","ebookFormat":"cbz","metadata":{"filename":"a.cbz","size":10,"mtimeMs":20}},"metadata":{"title":"My Comic","subtitle":"Sub","authors":[{"id":"a1","name":"Auth One"},{"id":"a2","name":"Auth Two"}],"series":[{"id":"s1","name":"S One","sequence":"3"},{"id":"s2","name":"S Two","sequence":"1"}],"narrators":["Nar A","Nar B"],"genres":["Fantasy","Horror"],"publisher":"Pub","publishedYear":"2021","language":"en","descriptionPlain":"Plain desc"}}}""";
        var h = new StubHandler(_ => StubHandler.Json(json));
        var d = await Client(h).GetItemDetailAsync("i1");

        Assert.Equal("/api/items/i1", h.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("1", System.Web.HttpUtility.ParseQueryString(h.Last!.RequestUri!.Query)["expanded"]);
        Assert.Equal("lib1", d.LibraryId);
        Assert.Equal(3, d.LibraryFiles!.Count);
        Assert.Equal("11", d.Media!.EbookFile!.Ino);
        Assert.Equal(2, d.Media!.Metadata!.Authors!.Count);
        Assert.Equal(2, d.Media!.Metadata!.Series!.Count);
        Assert.Equal(new[] { "Nar A", "Nar B" }, d.Media!.Metadata!.Narrators!.ToArray());
        Assert.Equal(new[] { "Fantasy", "Horror" }, d.Media!.Metadata!.Genres!.ToArray());
        Assert.Equal(new[] { "owned", "fav" }, d.Media!.Tags!.ToArray());
        Assert.Equal("Plain desc", d.Media!.Metadata!.DescriptionPlain);
        Assert.Equal("2021", d.Media!.Metadata!.PublishedYear);
    }

    [Fact]
    public async Task GetEbookFileStreamAsync_hits_ebook_ino_path()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 1, 2 }) });
        var (stream, _) = await Client(h).GetEbookFileStreamAsync("i1", "12");
        await using var _s = stream;
        Assert.Equal("/api/items/i1/ebook/12", h.Last!.RequestUri!.AbsolutePath);
    }
}
