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

        await using var s = await client.DownloadEbookAsync("item9", "TOKEN123", default);

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
}
