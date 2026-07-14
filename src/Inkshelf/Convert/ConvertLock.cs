namespace Inkshelf.Convert;

// Serializes conversions targeting the same cache-output path so concurrent
// /convert requests don't double-convert or corrupt the .tmp file. Keyed per
// path; distinct targets run in parallel. Ref-counted so the map self-cleans.
// Registered as a singleton (shared across the scoped ConvertService instances).
public sealed class ConvertLock
{
    private sealed class Entry { public readonly SemaphoreSlim Sem = new(1, 1); public int Refs; }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();

    internal int ActiveKeys { get { lock (_gate) return _entries.Count; } }

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!)) { entry = new Entry(); _entries[key] = entry; }
            entry.Refs++;
        }
        try
        {
            await entry.Sem.WaitAsync(ct);
        }
        catch
        {
            // Never acquired the semaphore — undo the ref (and drop the entry if last).
            lock (_gate) { if (--entry.Refs == 0) _entries.Remove(key); }
            throw;
        }
        return new Releaser(this, key);
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            entry.Sem.Release();
            if (--entry.Refs == 0) _entries.Remove(key);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly ConvertLock _owner;
        private readonly string _key;
        private bool _released;
        public Releaser(ConvertLock owner, string key) { _owner = owner; _key = key; }
        public void Dispose() { if (_released) return; _released = true; _owner.Release(_key); }
    }
}
