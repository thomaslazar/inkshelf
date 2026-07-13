using System.Net;
using System.Net.Http.Json;
using Inkshelf.Auth;

namespace Inkshelf.Abs;

public class AbsClient
{
    private readonly HttpClient _http;
    public AbsClient(HttpClient http) => _http = http;

    public async Task<Tokens> LoginAsync(string user, string pass, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/login");
        req.Headers.Add("x-return-tokens", "true");
        req.Content = JsonContent.Create(new { username = user, password = pass });
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest)
            throw new AbsLoginFailedException();
        res.EnsureSuccessStatusCode();
        return await ReadTokens(res, ct);
    }

    public async Task<Tokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        req.Headers.Add("x-refresh-token", refreshToken);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new AbsUnauthorizedException();
        res.EnsureSuccessStatusCode();
        return await ReadTokens(res, ct);
    }

    private static async Task<Tokens> ReadTokens(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadFromJsonAsync<AbsAuthResponse>(ct)
            ?? throw new InvalidOperationException("Empty auth response.");
        var u = body.User;
        if (string.IsNullOrEmpty(u.AccessToken) || string.IsNullOrEmpty(u.RefreshToken))
            throw new InvalidOperationException("Auth response missing tokens.");
        return new Tokens(u.AccessToken, u.RefreshToken!);
    }

    public async Task<List<AbsLibrary>> GetLibrariesAsync(string accessToken, CancellationToken ct = default)
    {
        using var res = await SendAuthedAsync(HttpMethod.Get, "/api/libraries", accessToken, ct);
        var body = await res.Content.ReadFromJsonAsync<AbsLibrariesResponse>(ct);
        return body?.Libraries ?? new();
    }

    public async Task<AbsItemsPage> GetItemsAsync(string accessToken, string libraryId,
        int page, int limit, string? filter = null, CancellationToken ct = default)
    {
        // Full item JSON (no minified) so author/series carry ids for filter links.
        var url = $"/api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit={limit}&page={page}";
        if (!string.IsNullOrEmpty(filter))
            url += $"&filter={Uri.EscapeDataString(filter)}";
        using var res = await SendAuthedAsync(HttpMethod.Get, url, accessToken, ct);
        return await res.Content.ReadFromJsonAsync<AbsItemsPage>(ct)
            ?? new AbsItemsPage(new(), 0, limit, page);
    }

    public async Task<AbsSearchResults> SearchAsync(string accessToken, string libraryId,
        string query, int limit, CancellationToken ct = default)
    {
        var url = $"/api/libraries/{Uri.EscapeDataString(libraryId)}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        using var res = await SendAuthedAsync(HttpMethod.Get, url, accessToken, ct);
        return await res.Content.ReadFromJsonAsync<AbsSearchResults>(ct)
            ?? new AbsSearchResults(new(), new(), new());
    }

    public async Task<(Stream Content, string ContentType)> GetCoverAsync(string accessToken,
        string itemId, int width, CancellationToken ct = default)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/cover?width={width}";
        var res = await SendAuthedAsync(HttpMethod.Get, url, accessToken, ct); // caller disposes via stream
        var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return (await res.Content.ReadAsStreamAsync(ct), type);
    }

    private async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string url,
        string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) { res.Dispose(); throw new AbsUnauthorizedException(); }
        try { res.EnsureSuccessStatusCode(); }
        catch { res.Dispose(); throw; }
        return res;
    }
}
