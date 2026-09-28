using System.Text.Json;

namespace Inkshelf;

// Daily check for a newer GitHub release, shown next to the version on the
// libraries page. Poke() returns immediately and the render uses whatever was
// last fetched: a synchronous call would put github.com in the critical path of
// a page load on an e-reader, and one load a day would appear to hang.
public sealed class UpdateCheck
{
    private const string LatestUrl = "https://api.github.com/repos/thomaslazar/inkshelf/releases/latest";
    private static readonly TimeSpan AfterSuccess = TimeSpan.FromHours(24);
    // Shorter, but not short: a deployment with no outbound network must not
    // re-attempt on every render.
    private static readonly TimeSpan AfterFailure = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _clients;
    private readonly bool _enabled;
    private readonly ILogger<UpdateCheck> _log;
    private readonly Lock _gate = new();
    private DateTimeOffset _due = DateTimeOffset.MinValue;

    public UpdateCheck(IHttpClientFactory clients, AbsOptions options, ILogger<UpdateCheck> log)
    {
        _clients = clients;
        _enabled = options.UpdateCheck;
        _log = log;
    }

    // The newer release's version, or null. Null covers disabled, not yet
    // checked, up to date, fetch failed and unparseable alike - the page has one
    // thing to render, so no failure path needs its own UI.
    public string? Newer { get; internal set; }

    public void Poke()
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _due) return;
            // Claim the slot before starting, so concurrent renders fetch once.
            _due = DateTimeOffset.UtcNow + AfterSuccess;
        }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var json = await _clients.CreateClient("github").GetStringAsync(LatestUrl);
            using var doc = JsonDocument.Parse(json);
            Newer = NewerThan(AppVersion.Current, doc.RootElement.GetProperty("tag_name").GetString() ?? "");
        }
        catch (Exception ex)
        {
            // Nothing here is worth a user-visible error or a warning in the log:
            // no network, rate limiting and a shape change all mean "no hint".
            lock (_gate) _due = DateTimeOffset.UtcNow + AfterFailure;
            _log.LogDebug(ex, "Update check failed.");
        }
    }

    // Null unless the remote tag parses and is strictly newer than ours. Both
    // sides drop a leading "v" and anything from the first "+": the Docker build
    // stamps non-release images "<version>+pr-34.a1b2c3d", and such a build of
    // 1.0.0 should still be told about 1.0.1.
    internal static string? NewerThan(string local, string remote)
    {
        static Version? Parse(string s)
        {
            s = s.TrimStart('v', 'V');
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            return Version.TryParse(s, out var v) ? v : null;
        }

        var mine = Parse(local);
        var theirs = Parse(remote);
        return mine is not null && theirs is not null && theirs > mine ? theirs.ToString() : null;
    }
}
