# Security #5 — Concurrent-convert lock — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop two simultaneous `/convert` requests for the same target from both converting and both writing the same `.tmp` (wasted CPU, possible corrupt cache file).

**Architecture:** A singleton `ConvertLock` holding per-key `SemaphoreSlim`s (keyed by cache-output path), ref-counted so the map self-cleans. `ConvertService` acquires the lock around the convert-on-miss block and double-checks `File.Exists` inside, so the second waiter serves the file the first just built.

**Tech Stack:** .NET 10, `System.Threading.SemaphoreSlim`, xUnit. `InternalsVisibleTo Inkshelf.Tests` is set.

## Global Constraints

- `dotnet test` green after every task. No behavior change for the single-request path.
- Distinct targets (different item/device-size) must NOT serialize against each other — only identical cache-output paths do.
- Conventional Commits; no `Co-Authored-By`. Branch `security/hardening`.

## File Structure

**Task 1:** Create `src/Inkshelf/Convert/ConvertLock.cs`, `tests/Inkshelf.Tests/ConvertLockTests.cs`; modify `src/Inkshelf/Program.cs` (register singleton).
**Task 2:** Modify `src/Inkshelf/Convert/ConvertService.cs`, `tests/Inkshelf.Tests/ConvertServiceTests.cs`.

---

## Task 1: ConvertLock singleton

**Files:** as above.

**Interfaces:**
- `ConvertLock` (singleton) with `Task<IDisposable> AcquireAsync(string key, CancellationToken ct)` and `internal int ActiveKeys` (test-only).

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (86).

- [ ] **Step 2: Write the failing tests**

Create `tests/Inkshelf.Tests/ConvertLockTests.cs`. Deterministic (no delays): a second acquire on a held key returns an incomplete task synchronously.

```csharp
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertLockTests
{
    [Fact]
    public async Task Same_key_serializes()
    {
        var l = new ConvertLock();
        var first = await l.AcquireAsync("A", default);
        var secondTask = l.AcquireAsync("A", default);
        Assert.False(secondTask.IsCompleted); // blocked while first is held
        first.Dispose();
        var second = await secondTask;         // proceeds after release
        second.Dispose();
    }

    [Fact]
    public async Task Different_keys_run_concurrently()
    {
        var l = new ConvertLock();
        var a = await l.AcquireAsync("A", default);
        var bTask = l.AcquireAsync("B", default);
        Assert.True(bTask.IsCompleted);        // B not blocked by A
        a.Dispose();
        (await bTask).Dispose();
    }

    [Fact]
    public async Task Releases_clean_up_the_map()
    {
        var l = new ConvertLock();
        (await l.AcquireAsync("A", default)).Dispose();
        (await l.AcquireAsync("B", default)).Dispose();
        Assert.Equal(0, l.ActiveKeys);
    }
}
```

- [ ] **Step 3: Run — verify fail to compile**

Run: `dotnet test --filter FullyQualifiedName~ConvertLockTests`
Expected: FAIL to compile — `ConvertLock` doesn't exist.

- [ ] **Step 4: Create `ConvertLock.cs`**

```csharp
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
```

- [ ] **Step 5: Run ConvertLock tests — GREEN**

Run: `dotnet test --filter FullyQualifiedName~ConvertLockTests`
Expected: PASS (3).

- [ ] **Step 6: Register the singleton**

In `Program.cs`, alongside the other `Convert` registrations (near `AddSingleton<EpubConverter>()`):

```csharp
builder.Services.AddSingleton<ConvertLock>();
```

- [ ] **Step 7: Full suite**

Run: `dotnet test`
Expected: PASS (86 + 3 = 89). `ConvertLock` is registered but not yet used by `ConvertService` — app behavior unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Convert/ConvertLock.cs tests/Inkshelf.Tests/ConvertLockTests.cs src/Inkshelf/Program.cs
git commit -m "feat: add keyed ConvertLock for serializing conversions"
```

---

## Task 2: Serialize the convert-on-miss block

**Files:** `src/Inkshelf/Convert/ConvertService.cs`, `tests/Inkshelf.Tests/ConvertServiceTests.cs`.

**Interfaces:** `ConvertService` constructor gains `ConvertLock convertLock`.

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (89).

- [ ] **Step 2: Inject `ConvertLock` and lock the miss path**

In `src/Inkshelf/Convert/ConvertService.cs`, add the field + constructor parameter:

```csharp
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly EpubConverter _converter;
    private readonly ConvertLock _lock;
    private readonly AbsOptions _options;
    private readonly ILogger<ConvertService> _logger;

    public ConvertService(AbsApiClient api, EpubCache cache, EpubConverter converter,
        ConvertLock convertLock, AbsOptions options, ILogger<ConvertService> logger)
    {
        _api = api; _cache = cache; _converter = converter;
        _lock = convertLock; _options = options; _logger = logger;
    }
```

Replace the current `if (!File.Exists(path)) { …convert… } else { Touch; log }` block with a lock-guarded, double-checked version:

```csharp
        var path = _cache.PathFor(id, size, mtime, maxW, maxH);
        if (!System.IO.File.Exists(path))
        {
            // Serialize concurrent converts of the SAME target so they don't both
            // build + write the .tmp. The second waiter re-checks and skips.
            using (await _lock.AcquireAsync(path, ct))
            {
                if (!System.IO.File.Exists(path))
                {
                    _logger.LogInformation("Converting {Id} ({Fmt}, {Bytes} bytes, cap {W}x{H} @dpr {Dpr}) to EPUB…", id, fmt, size, maxW, maxH, dpr);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (archive, _) = await _api.GetEbookStreamAsync(id, ct);
                    using var buffered = new MemoryStream();
                    await using (archive) await archive.CopyToAsync(buffered, ct);   // SharpCompress needs a seekable stream
                    buffered.Position = 0;
                    await _converter.ConvertAsync(buffered, new EbookMeta(title, author, seriesName, seq, id), path, maxW, maxH, dpr, ct);
                    _logger.LogInformation("Converted {Id} in {Ms} ms → {OutBytes} bytes", id, sw.ElapsedMilliseconds, new FileInfo(path).Length);
                    _cache.EnforceCap(_options.MaxCacheBytes);
                }
            }
        }
        _cache.Touch(path);   // count this serve as recent use (fresh or cached)
```

Remove the old `else { _cache.Touch(path); _logger.LogInformation("Serving cached EPUB…") }` branch — touch now happens once after the block for both paths. (Dropping the "Serving cached" info log is acceptable; it carried no behavior.)

- [ ] **Step 3: Update `ConvertServiceTests` construction**

The `Service` helper needs the new `ConvertLock` arg:

```csharp
    private static ConvertService Service(AbsApiClient api, EpubCache cache) =>
        new(api, cache, new EpubConverter(), new ConvertLock(),
            new AbsOptions { MaxCacheBytes = long.MaxValue }, NullLogger<ConvertService>.Instance);
```

- [ ] **Step 4: Full suite**

Run: `dotnet test`
Expected: PASS (89). The three `ConvertServiceTests` still hold: `NotFound` (wrong format — never reaches the lock), `File`/`Warmed` (cached — `File.Exists` true, so the lock block is skipped and the file is touched + served).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/ConvertService.cs tests/Inkshelf.Tests/ConvertServiceTests.cs
git commit -m "fix: serialize same-target conversions to avoid double-work and corrupt cache"
```

---

## Self-Review

**Spec coverage (#5):** keyed `SemaphoreSlim` in a singleton `ConvertLock` (home per the design), keyed by cache-output path; convert-on-miss runs inside the lock with a double-checked `File.Exists`; ref-counted map cleanup. ✓

**Placeholder scan:** None — lock tests assert serialization (incomplete task while held), concurrency (different keys don't block), and map cleanup (`ActiveKeys == 0`).

**Type consistency:** `ConvertLock.AcquireAsync(string, CancellationToken) : Task<IDisposable>` matches tests and the `ConvertService` `using (await …)` site. `ConvertService`'s new 6-arg constructor matches the `Service` test helper. Cancellation during `WaitAsync` undoes the ref (no leak).

**Ordering note:** Done before #4 (archive ceiling) so #4's size check lands *inside* this locked convert block, as the spec sequences.

**Scope:** Two tasks — additive lock, then wire. #4 (archive ceiling) and docs remain.
