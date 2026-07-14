using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Inkshelf.Auth;

namespace Inkshelf.Abs;

// Injects the ABS Bearer token and performs refresh-on-401-then-retry-once,
// transparently, for AbsApiClient. Refresh goes through AbsAuthClient, a
// separate handler-free client, so it can never recurse through this handler.
//
// Scoped services (TokenStore, and the request-scoped AbsAuthClient) are
// resolved from HttpContext.RequestServices per call — never constructor-
// injected, because this handler is pooled by IHttpClientFactory for longer
// than a request scope.
public class AbsAuthHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;
    public AbsAuthHandler(IHttpContextAccessor accessor) => _accessor = accessor;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var services = _accessor.HttpContext?.RequestServices
            ?? throw new AbsAuthException();
        var store = services.GetRequiredService<TokenStore>();
        var tokens = store.Read() ?? throw new AbsAuthException();

        // Buffer the body once so the request can be rebuilt for a retry. Only
        // the metadata-batch POST carries a body (JsonContent); streaming request
        // uploads are unsupported by this retry (none exist).
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var contentType = request.Content?.Headers.ContentType;

        var first = await SendOnce(request, tokens.Access, body, contentType, ct);
        if (first.StatusCode != HttpStatusCode.Unauthorized) return first;
        first.Dispose();

        Tokens refreshed;
        try
        {
            var auth = services.GetRequiredService<AbsAuthClient>();
            refreshed = await auth.RefreshAsync(tokens.Refresh, ct);
        }
        catch (Exception)
        {
            store.Clear();
            throw new AbsAuthException();
        }
        store.Save(refreshed);

        var second = await SendOnce(request, refreshed.Access, body, contentType, ct);
        if (second.StatusCode == HttpStatusCode.Unauthorized)
        {
            second.Dispose();
            throw new AbsAuthException();
        }
        return second;
    }

    // Build a fresh request each attempt (an HttpRequestMessage can only be sent
    // once). Copy the incoming request's headers first so the User-Agent that the
    // HttpClient applied before this handler ran survives onto the retry — the ABS
    // proxy 403s an empty User-Agent — then set/overwrite the Bearer.
    private async Task<HttpResponseMessage> SendOnce(HttpRequestMessage template,
        string bearer, byte[]? body, MediaTypeHeaderValue? contentType, CancellationToken ct)
    {
        var req = new HttpRequestMessage(template.Method, template.RequestUri);
        foreach (var h in template.Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            if (contentType is not null) req.Content.Headers.ContentType = contentType;
        }
        return await base.SendAsync(req, ct);
    }
}
