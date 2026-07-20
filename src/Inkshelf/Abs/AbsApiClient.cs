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

    // Fetch expanded items (id, libraryId, structured metadata, coverPath,
    // ebookFile) for a set of ids in ONE call. Cross-library — batch/get queries
    // by id only, not scoped to a library.
    public async Task<List<AbsBatchItem>> GetItemsBatchAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return new();
        using var content = JsonContent.Create(new { libraryItemIds = itemIds });
        using var res = await SendAsync(HttpMethod.Post, "/api/items/batch/get", ct, content);
        var body = await res.Content.ReadFromJsonAsync<AbsBatchItems>(ct);
        return body?.LibraryItems ?? new();
    }

    // Expanded metadata keyed by item id, for the listing's per-row links/state.
    public async Task<Dictionary<string, AbsBatchMedia>> GetItemsMetadataBatchAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken ct = default)
    {
        var map = new Dictionary<string, AbsBatchMedia>();
        foreach (var it in await GetItemsBatchAsync(itemIds, ct))
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

    // Read state lives in ABS as per-user media progress. One call returns the
    // whole finished-set; we key on libraryItemId (matches AbsItem.Id).
    public async Task<HashSet<string>> GetFinishedItemIdsAsync(CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, "/api/me", ct);
        var me = await res.Content.ReadFromJsonAsync<AbsMe>(ct);
        var set = new HashSet<string>();
        foreach (var mp in me?.MediaProgress ?? new())
            if (mp.IsFinished && !string.IsNullOrEmpty(mp.LibraryItemId))
                set.Add(mp.LibraryItemId);
        return set;
    }

    // Mark an item read (isFinished:true) or unread (false). PATCH is symmetric —
    // unmarking leaves a harmless isFinished:false progress row, so no DELETE / no
    // need to know the progress-row id.
    public async Task SetReadAsync(string itemId, bool finished, CancellationToken ct = default)
    {
        var content = JsonContent.Create(new { isFinished = finished });
        using var res = await SendAsync(HttpMethod.Patch,
            $"/api/me/progress/{Uri.EscapeDataString(itemId)}", ct, content);
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
