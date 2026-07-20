using System.Net.Http.Headers;

namespace Inkshelf.Abs;

// The background worker's authenticated ebook download. This is a DELIBERATE
// THIRD ABS client, distinct from the two load-bearing ones (AbsAuthClient
// login/refresh; AbsApiClient data). It is HANDLER-FREE: the worker has no
// HttpContext, so AbsAuthHandler (which resolves the token from the request)
// cannot run — the caller supplies the bearer instead. It does NOT refresh on
// 401 (that would need HttpContext to persist the new token); a failure just
// fails the job and the user re-taps with a fresh token.
//
// Registered via ConfigureAbs so it inherits the BaseAddress AND the User-Agent
// the ABS reverse proxy requires (it 403s an empty UA). Never attach
// AbsAuthHandler to it; never use it from a request path (use AbsApiClient there).
public sealed class AbsDownloadClient
{
    private readonly HttpClient _http;
    public AbsDownloadClient(HttpClient http) => _http = http;

    // Caller owns (and must dispose) the returned stream.
    public async Task<Stream> DownloadEbookAsync(string itemId, string accessToken, CancellationToken ct)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/ebook";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new HttpRequestException($"ebook download failed for {itemId}: {(int)res.StatusCode}");
        }
        return await res.Content.ReadAsStreamAsync(ct);
    }

    // The worker's token-less cover fetch. Mirrors DownloadEbookAsync: handler-free,
    // caller-supplied bearer, NO 401 refresh. Caller owns (and must dispose) the stream.
    public async Task<(Stream Content, string ContentType)> DownloadCoverAsync(
        string itemId, string accessToken, int width, CancellationToken ct)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/cover?width={width}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new HttpRequestException($"cover download failed for {itemId}: {(int)res.StatusCode}");
        }
        var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return (await res.Content.ReadAsStreamAsync(ct), type);
    }
}
