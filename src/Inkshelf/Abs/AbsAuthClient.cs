using System.Net;
using System.Net.Http.Json;
using Inkshelf.Auth;

namespace Inkshelf.Abs;

// Login + refresh only. Deliberately a SEPARATE typed client from AbsApiClient
// with NO auth handler: refresh must not recurse through the handler that calls it.
public class AbsAuthClient
{
    private readonly HttpClient _http;
    public AbsAuthClient(HttpClient http) => _http = http;

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

    // OIDC leg 1: ask ABS to start its "mobile" flow on our behalf. We must keep
    // the Set-Cookie values it hands back — the token exchange in
    // CompleteOidcAsync is refused without them. Requires the client's handler to
    // have AllowAutoRedirect off, or the Location we return here is gone.
    //
    // absPublicBase is ABS's browser-facing URL. ABS derives its own
    // /auth/openid/mobile-redirect URL from this request's Host and
    // x-forwarded-proto, and that URL is both where the provider sends the user
    // back and what the provider matches against its registered redirect URIs. On
    // the internal address (a compose service name, say) it would be neither
    // reachable nor registered, so present the public one — the connection itself
    // still goes to BaseAddress.
    public async Task<(string AuthorizeUrl, string Cookies)> StartOidcAsync(
        Uri absPublicBase, string redirectUri, string challenge, string state,
        CancellationToken ct = default)
    {
        var url = $"/auth/openid?response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256"
            + $"&state={Uri.EscapeDataString(state)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Host = absPublicBase.IsDefaultPort
            ? absPublicBase.Host
            : $"{absPublicBase.Host}:{absPublicBase.Port}";
        // ABS reads this header directly (no trust-proxy setting involved) and
        // compares it to the literal "https".
        if (absPublicBase.Scheme == Uri.UriSchemeHttps)
            req.Headers.Add("x-forwarded-proto", "https");
        using var res = await _http.SendAsync(req, ct);

        var location = res.Headers.Location?.ToString();
        if (res.StatusCode is not (>= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            || string.IsNullOrEmpty(location))
            throw new AbsOidcException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));

        // name=value only; the attributes describe ABS's own cookie jar, and we
        // are building a request Cookie header, not storing cookies.
        var cookies = res.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(c => c.Split(';')[0]))
            : "";
        return (location, cookies);
    }

    // OIDC leg 3: exchange the code ABS sent to our callback. The cookies from
    // leg 1 are replayed by hand because the shared handler has UseCookies off.
    public async Task<Tokens> CompleteOidcAsync(string code, string state, string verifier,
        string cookies, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/auth/openid/callback?code={Uri.EscapeDataString(code)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + $"&code_verifier={Uri.EscapeDataString(verifier)}");
        req.Headers.Add("Cookie", cookies);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new AbsOidcException((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
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
