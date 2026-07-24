using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Inkshelf.Convert;

// In-memory job registry AND the channel producer feeding ConvertWorker.
// Singleton (shared across scoped ConvertService instances and the worker).
// Keyed by the cache-output path, same key as ConvertLock. The registry holds
// only transient states; "done" is inferred from File.Exists so there is no
// dual source of truth, and a restart (empty registry) simply reverts pending
// rows to "Convert" for a re-tap.
public sealed class ConvertQueue
{
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(10);

    private enum Phase { Queued, Running, Failed }
    private sealed class Entry { public Phase Phase; public DateTime FailedAtUtc; public ConvertFailReason Reason; public long? ArchiveBytes; }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private readonly Channel<ConvertJob> _channel =
        Channel.CreateUnbounded<ConvertJob>(new UnboundedChannelOptions
        {
            SingleReader = false, // MaxConcurrentConversions worker loops
            SingleWriter = false,
        });

    public ConvertQueue(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public ChannelReader<ConvertJob> Reader => _channel.Reader;

    // Idempotent. A file that already exists short-circuits to Done; a path that
    // is already Queued/Running is a no-op returning its current status; a Failed
    // (or absent) path is (re-)queued. Returns the resulting status.
    public ConvertStatus Enqueue(ConvertJob job)
    {
        if (File.Exists(job.CachePath)) return ConvertStatus.Done;
        lock (_gate)
        {
            var s = Peek(job.CachePath);
            if (s is ConvertStatus.Queued or ConvertStatus.Running) return s;
            _entries[job.CachePath] = new Entry { Phase = Phase.Queued };
        }
        _channel.Writer.TryWrite(job); // unbounded → always succeeds
        return ConvertStatus.Queued;
    }

    public ConvertStatus Status(string cachePath)
    {
        if (File.Exists(cachePath)) { _entries.TryRemove(cachePath, out _); return ConvertStatus.Done; }
        lock (_gate) return Peek(cachePath);
    }

    public void MarkRunning(string cachePath)
    {
        lock (_gate) if (_entries.TryGetValue(cachePath, out var e)) e.Phase = Phase.Running;
    }

    public void MarkDone(string cachePath) => _entries.TryRemove(cachePath, out _);

    public void MarkFailed(string cachePath, ConvertFailReason reason = ConvertFailReason.ConvertError, long? archiveBytes = null)
    {
        lock (_gate) _entries[cachePath] = new Entry
        {
            Phase = Phase.Failed,
            FailedAtUtc = _clock(),
            Reason = reason,
            ArchiveBytes = archiveBytes,
        };
    }

    // The failure reason while the path is Failed (honours the TTL via Peek), else null.
    public ConvertFailure? FailureFor(string cachePath)
    {
        lock (_gate)
        {
            if (Peek(cachePath) != ConvertStatus.Failed) return null;
            return _entries.TryGetValue(cachePath, out var e)
                ? new ConvertFailure(e.Reason, e.ArchiveBytes)
                : null;
        }
    }

    // Caller holds _gate (except the File.Exists fast paths above).
    private ConvertStatus Peek(string cachePath)
    {
        if (!_entries.TryGetValue(cachePath, out var e)) return ConvertStatus.None;
        switch (e.Phase)
        {
            case Phase.Queued: return ConvertStatus.Queued;
            case Phase.Running: return ConvertStatus.Running;
            default: // Failed — expire past the TTL
                if (_clock() - e.FailedAtUtc > FailedTtl) { _entries.TryRemove(cachePath, out _); return ConvertStatus.None; }
                return ConvertStatus.Failed;
        }
    }
}
