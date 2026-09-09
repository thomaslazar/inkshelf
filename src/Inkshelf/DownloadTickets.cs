using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Inkshelf;

// A download ticket: a URL-safe handle standing for one file this server will
// stream. An e-reader's download manager takes over the transfer WITHOUT the
// browser's cookies (issue #40), so the URL has to authorise on its own - and a
// handle, rather than a signed blob, keeps the credential out of the URL, the
// browser history and the request log.
//
// SERVES BYTES ONLY. A ticket never authorises a conversion kick, a status poll
// or fresh=1; those still need the session cookie.
public sealed class DownloadTickets
{
    // Sliding, not absolute: any request presenting a ticket re-stamps it, so the
    // 5s convert poll keeps a link alive for as long as its conversion runs.
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(15);

    // Exactly one of FilePath / Access is set. FilePath = a converted EPUB in the
    // cache, servable with no ABS call at all. Access = the ABS bearer a raw
    // download streams with, which never leaves this process.
    public sealed record Ticket(string ItemId, string? FileIno, string Did, string DownloadName,
        string? FilePath = null, string? Access = null);

    private readonly ConcurrentDictionary<string, (Ticket T, long Stamp)> _live = new();
    private readonly TimeProvider _clock;

    // Live tickets, for a test that pins the converted page building rows only
    // for the page it renders. Cheap on ConcurrentDictionary.
    public int LiveCount => _live.Count;

    public DownloadTickets(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public string MintEpub(string itemId, string? fileIno, string did, string downloadName, string filePath) =>
        Mint(new Ticket(itemId, fileIno, did, downloadName, FilePath: filePath));

    public string MintRaw(string itemId, string? fileIno, string did, string downloadName, string access) =>
        Mint(new Ticket(itemId, fileIno, did, downloadName, Access: access));

    // Re-stamps on success. Unknown and expired are indistinguishable: both null.
    public Ticket? Redeem(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_live.TryGetValue(id, out var e)) return null;
        // Compare-and-remove: a concurrent Redeem may have re-stamped this id after
        // the snapshot above, and a blind key-only removal would delete that fresh
        // stamp instead of the expired one.
        if (Expired(e.Stamp)) { _live.TryRemove(new(id, e)); return null; }
        _live[id] = (e.T, Now);
        return e.T;
    }

    // One page render mints ten to twenty tickets, so sweeping on every call is
    // that many O(n) passes over the same map. Expiry itself never depends on the
    // sweep - Redeem checks the stamp - so this only decides when dead entries stop
    // occupying memory.
    // ponytail: full sweep above SweepAbove entries; per-shard expiry if the table
    // ever gets big.
    private const int SweepAbove = 256;

    private string Mint(Ticket t)
    {
        if (_live.Count > SweepAbove)
        {
            // Same compare-and-remove as Redeem's expired branch, and for the same
            // reason: the snapshot (k, v) here can go stale mid-sweep.
            foreach (var (k, v) in _live) if (Expired(v.Stamp)) _live.TryRemove(new(k, v));
        }
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        _live[id] = (t, Now);
        return id;
    }

    private long Now => _clock.GetUtcNow().UtcTicks;
    private bool Expired(long stamp) => Now - stamp > IdleWindow.Ticks;
}
