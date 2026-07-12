using System.Net;

namespace Inkshelf.Tests;

public class StubHandler : HttpMessageHandler
{
    public HttpRequestMessage? Last { get; private set; }
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // buffer content so assertions can read the body after return
        if (request.Content is not null) await request.Content.LoadIntoBufferAsync();
        Last = request;
        return _respond(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
