using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
}
