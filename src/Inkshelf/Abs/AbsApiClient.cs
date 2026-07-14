using System.Net.Http.Json;

namespace Inkshelf.Abs;

// The ABS data API. Bearer injection + refresh-on-401 are handled transparently
// by AbsAuthHandler in this client's pipeline, so methods take no access token.
public class AbsApiClient
{
    private readonly HttpClient _http;
    public AbsApiClient(HttpClient http) => _http = http;

    public async Task<List<AbsLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, "/api/libraries", ct);
        var body = await res.Content.ReadFromJsonAsync<AbsLibrariesResponse>(ct);
        return body?.Libraries ?? new();
    }

    public async Task<AbsItemsPage> GetItemsAsync(string libraryId,
        int page, int limit, string? filter = null, string? sort = null, bool desc = false,
        CancellationToken ct = default)
    {
        // Full item JSON (no minified) so author/series carry ids for filter links.
        var url = $"/api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit={limit}&page={page}";
        if (!string.IsNullOrEmpty(filter))
            url += $"&filter={Uri.EscapeDataString(filter)}";
        if (!string.IsNullOrEmpty(sort))
        {
            url += $"&sort={Uri.EscapeDataString(sort)}";
            if (desc) url += "&desc=1";
        }
        using var res = await SendAsync(HttpMethod.Get, url, ct);
        return await res.Content.ReadFromJsonAsync<AbsItemsPage>(ct)
            ?? new AbsItemsPage(new(), 0, limit, page);
    }

    // Fetch expanded metadata (structured authors/series) for a page of items in
    // one call, so listing rows can render accurate per-author/per-series links.
    public async Task<Dictionary<string, AbsBatchMedia>> GetItemsMetadataBatchAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken ct = default)
    {
        var map = new Dictionary<string, AbsBatchMedia>();
        if (itemIds.Count == 0) return map;
        using var content = JsonContent.Create(new { libraryItemIds = itemIds });
        using var res = await SendAsync(HttpMethod.Post, "/api/items/batch/get", ct, content);
        var body = await res.Content.ReadFromJsonAsync<AbsBatchItems>(ct);
        foreach (var it in body?.LibraryItems ?? new())
            if (it.Media is not null) map[it.Id] = it.Media;
        return map;
    }

    public async Task<AbsItemDetail> GetItemDetailAsync(string itemId, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, $"/api/items/{Uri.EscapeDataString(itemId)}", ct);
        return await res.Content.ReadFromJsonAsync<AbsItemDetail>(ct)
            ?? new AbsItemDetail(null);
    }

    public async Task<(Stream Content, string ContentType)> GetEbookStreamAsync(string itemId, CancellationToken ct = default)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/ebook";
        var res = await SendAsync(HttpMethod.Get, url, ct); // caller owns the stream
        var type = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (await res.Content.ReadAsStreamAsync(ct), type);
    }

    public async Task<AbsSearchResults> SearchAsync(string libraryId,
        string query, int limit, CancellationToken ct = default)
    {
        var url = $"/api/libraries/{Uri.EscapeDataString(libraryId)}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        using var res = await SendAsync(HttpMethod.Get, url, ct);
        return await res.Content.ReadFromJsonAsync<AbsSearchResults>(ct)
            ?? new AbsSearchResults(new(), new(), new());
    }

    public async Task<(Stream Content, string ContentType)> GetCoverAsync(
        string itemId, int width, CancellationToken ct = default)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/cover?width={width}";
        var res = await SendAsync(HttpMethod.Get, url, ct); // caller disposes via stream
        var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return (await res.Content.ReadAsStreamAsync(ct), type);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url,
        CancellationToken ct, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        try { res.EnsureSuccessStatusCode(); }
        catch { res.Dispose(); throw; }
        return res;
    }
}
