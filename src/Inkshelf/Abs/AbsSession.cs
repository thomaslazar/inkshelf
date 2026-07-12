using Inkshelf.Auth;

namespace Inkshelf.Abs;

public class AbsSession
{
    private readonly TokenStore _store;
    private readonly AbsClient _client;
    public AbsSession(TokenStore store, AbsClient client) { _store = store; _client = client; }

    public async Task<T> ExecuteAsync<T>(Func<string, CancellationToken, Task<T>> call,
        CancellationToken ct = default)
    {
        var tokens = _store.Read() ?? throw new AbsAuthException();
        try
        {
            return await call(tokens.Access, ct);
        }
        catch (AbsUnauthorizedException)
        {
            Tokens refreshed;
            try { refreshed = await _client.RefreshAsync(tokens.Refresh, ct); }
            catch (Exception) { _store.Clear(); throw new AbsAuthException(); }
            _store.Save(refreshed);
            return await call(refreshed.Access, ct);
        }
    }
}
