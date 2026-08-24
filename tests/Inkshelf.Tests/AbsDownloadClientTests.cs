using System.Net;
using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsDownloadClientTests
{
    private static AbsDownloadClient Client(StubHandler stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://abs.local") });

    [Fact]
    public async Task Sends_bearer_and_returns_stream()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });
        var client = Client(stub);

        var dl = await client.DownloadEbookAsync("item9", "TOKEN123", default);
        await using var s = dl.Content;

        Assert.Equal("/api/items/item9/ebook", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", stub.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("TOKEN123", stub.Last!.Headers.Authorization!.Parameter);
        Assert.Equal(3, s.Length);
    }

    [Fact]
    public async Task Throws_on_401()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DownloadEbookAsync("item9", "stale", default));
    }

    [Fact]
    public async Task DownloadCover_sends_bearer_and_width_and_returns_stream_and_type()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 9, 8, 7 })
            { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") } }
        });
        var client = Client(stub);

        var (stream, type) = await client.DownloadCoverAsync("item9", "TOKEN123", 600, default);
        await using var _ = stream;

        Assert.Equal("/api/items/item9/cover", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("width=600", stub.Last!.RequestUri!.Query.TrimStart('?'));
        Assert.Equal("Bearer", stub.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("TOKEN123", stub.Last!.Headers.Authorization!.Parameter);
        Assert.Equal("image/png", type);
        Assert.Equal(3, stream.Length);
    }

    [Fact]
    public async Task DownloadCover_throws_on_404()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DownloadCoverAsync("item9", "tok", 600, default));
    }

    [Fact]
    public async Task DownloadEbook_with_fileIno_hits_ebook_ino_path()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 1 }) });
        var dl = await Client(stub).DownloadEbookAsync("item9", "TOK", default, fileIno: "77");
        await using var s = dl.Content;
        Assert.Equal("/api/items/item9/ebook/77", stub.Last!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task DownloadEbookAsync_reports_the_content_type_and_length_abs_sent()
    {
        // A ticket-served raw download has no cookie path to fall back on, so these
        // two headers have to come from here: a response with no Content-Length is
        // one some e-reader download managers refuse.
        var body = new byte[] { 1, 2, 3, 4 };
        var stub = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new("application/epub+zip"), ContentLength = body.Length }
            }
        });

        var (content, type, length) = await Client(stub).DownloadEbookAsync("i1", "tok", default);

        Assert.Equal("application/epub+zip", type);
        Assert.Equal(4, length);
        Assert.Equal(4, (await new StreamReader(content).ReadToEndAsync()).Length);
    }
}
