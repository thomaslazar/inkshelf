# Cache age eviction implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete cached EPUBs older than `MaxCacheAgeDays` (default 30), sweeping at startup and once a day, alongside the existing size cap.

**Architecture:** One new method on `EpubCache` beside `EnforceCap`, one new option, and a daily loop in `ConvertWorker`. Spec: `docs/superpowers/specs/2026-09-28-cache-max-age-design.md`.

**Tech Stack:** ASP.NET Core, .NET 10, `PeriodicTimer`, xUnit.

## Global Constraints

- No em dashes (U+2014) and no en dashes (U+2013) anywhere, including code, comments, commit messages and docs. Plain hyphen only.
- Config key: `MaxCacheAgeDays`, int, default 30, `0` or negative disables.
- The glob stays `*.epub`. Widening it to `*` deletes the `marks/` subdirectory's files, which an existing test guards.
- Age is the file's `LastWriteTimeUtc`, which is conversion time. Nothing re-stamps a served file.
- `EnforceCap` keeps its existing trigger (after a conversion). The daily loop does the age sweep only.

---

### Task 1: EnforceMaxAge on the cache

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs` (new method directly below `EnforceCap`)
- Test: `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Produces: `public void EpubCache.EnforceMaxAge(TimeSpan maxAge)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubCacheTests.cs`, beside the `EnforceCap` tests:

```csharp
    [Fact]
    public void EnforceMaxAge_deletes_entries_past_the_cutoff_and_keeps_the_rest()
    {
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        var now = DateTime.UtcNow;
        var old = Path.Combine(dir, "item0-1-1-10x10.epub");
        var fresh = Path.Combine(dir, "item1-1-1-10x10.epub");
        File.WriteAllBytes(old, new byte[100]);
        File.WriteAllBytes(fresh, new byte[100]);
        File.SetLastWriteTimeUtc(old, now.AddDays(-31));
        File.SetLastWriteTimeUtc(fresh, now.AddDays(-29));

        cache.EnforceMaxAge(TimeSpan.FromDays(30));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void EnforceMaxAge_is_disabled_by_a_non_positive_age()
    {
        // Zero is how an operator turns age eviction off, so it must delete
        // nothing at all rather than meaning "older than now".
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        var ancient = Path.Combine(dir, "item0-1-1-10x10.epub");
        File.WriteAllBytes(ancient, new byte[100]);
        File.SetLastWriteTimeUtc(ancient, DateTime.UtcNow.AddYears(-5));

        cache.EnforceMaxAge(TimeSpan.Zero);

        Assert.True(File.Exists(ancient));
    }

    [Fact]
    public void EnforceMaxAge_does_not_touch_a_marks_subdirectory()
    {
        // Same reasoning as the EnforceCap version: the glob is extension-scoped,
        // and a marks file is older than any cutoff the moment its device stops
        // visiting. Widen "*.epub" to "*" here and this test fails.
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        var marks = Path.Combine(dir, "marks");
        Directory.CreateDirectory(marks);
        var markFile = Path.Combine(marks, "abc123def4560000");
        File.WriteAllText(markFile, "d:item1\n");
        File.SetLastWriteTimeUtc(markFile, DateTime.UtcNow.AddYears(-5));

        cache.EnforceMaxAge(TimeSpan.FromDays(30));

        Assert.True(File.Exists(markFile));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EnforceMaxAge`
Expected: build failure, `error CS1061: 'EpubCache' does not contain a definition for 'EnforceMaxAge'`.

- [ ] **Step 3: Write the implementation**

In `src/Inkshelf/Convert/EpubCache.cs`, directly below `EnforceCap`:

```csharp
    // Delete entries older than maxAge. The second eviction axis: EnforceCap
    // bounds how much the cache holds, this bounds how long it holds it, because
    // a converted EPUB is dead weight once it has reached the reader. Age is the
    // file's write time, which is conversion time - nothing re-stamps a served
    // file, so it is the same timestamp EnforceCap orders by. No-op at or below
    // zero, which is how an operator turns this off. Best-effort (ignores IO races).
    public void EnforceMaxAge(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero) return;
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var f in new DirectoryInfo(_dir).GetFiles("*.epub"))
        {
            if (f.LastWriteTimeUtc >= cutoff) continue;
            try { f.Delete(); } catch (IOException) { }
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~EnforceMaxAge`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: evict cached epubs past a maximum age"
```

---

### Task 2: The option and the daily sweep

**Files:**
- Modify: `src/Inkshelf/AbsOptions.cs` (new property, and the config-key list in the file's header comment)
- Modify: `src/Inkshelf/Program.cs` (binding, beside `MaxCacheBytes`)
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs` (startup call plus the daily loop)

**Interfaces:**
- Consumes: `EpubCache.EnforceMaxAge` (Task 1).
- Produces: `AbsOptions.MaxCacheAgeDays` (`int`, default 30).

- [ ] **Step 1: Add the option**

In `src/Inkshelf/AbsOptions.cs`, directly below the `MaxCacheBytes` property:

```csharp
    // Delete cached EPUBs older than this many days; 0 or negative disables age
    // eviction. Default 30. The second axis beside MaxCacheBytes: a converted
    // EPUB is dead weight once it has been downloaded to the reader.
    public int MaxCacheAgeDays { get; set; } = 30;
```

Add `MaxCacheAgeDays` to the config-key list in the file's header comment, keeping its existing order: it goes with the other non-screaming keys, so the list reads `... CachePath, DataProtectionKeysPath, DIAG_ENABLED, ...` unchanged and gains `MaxCacheAgeDays` before the closing period, matching however `MaxCacheBytes` is already listed there. If `MaxCacheBytes` is absent from that comment, leave the comment alone.

- [ ] **Step 2: Bind it**

In `src/Inkshelf/Program.cs`, directly below the `MaxCacheBytes` line in the `absOptions` initializer:

```csharp
    // Parsed differently from its neighbours on purpose: they treat a
    // non-positive value as "use the default", which would make an explicit 0
    // mean 30 days here. 0 has to mean "off", so only an unparseable value falls
    // back to the default.
    MaxCacheAgeDays = int.TryParse(builder.Configuration["MaxCacheAgeDays"], out var mcad) ? mcad : 30,
```

- [ ] **Step 3: Sweep at startup and daily**

In `src/Inkshelf/Convert/ConvertWorker.cs`, in `ExecuteAsync`, add the startup call after the existing `Prune` line:

```csharp
        _cache.EnforceMaxAge(TimeSpan.FromDays(_options.MaxCacheAgeDays));
```

and add the loop to the task set. Replace:

```csharp
        var loops = Math.Max(1, _options.MaxConcurrentConversions);
        var tasks = new Task[loops];
        for (var i = 0; i < loops; i++) tasks[i] = ConsumeAsync(stoppingToken);
        await Task.WhenAll(tasks);
```

with:

```csharp
        var loops = Math.Max(1, _options.MaxConcurrentConversions);
        var tasks = new Task[loops + 1];
        for (var i = 0; i < loops; i++) tasks[i] = ConsumeAsync(stoppingToken);
        tasks[loops] = SweepAgeAsync(stoppingToken);
        await Task.WhenAll(tasks);
```

Then add the loop below `ConsumeAsync`:

```csharp
    // Entries age while nothing is happening, so this cannot hang off the
    // conversion trigger EnforceCap uses: a deployment that is not converting is
    // exactly the one whose disk is being held. EnforceCap stays where it is,
    // because the cache can only grow by converting.
    private async Task SweepAgeAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                _cache.EnforceMaxAge(TimeSpan.FromDays(_options.MaxCacheAgeDays));
        }
        catch (OperationCanceledException) { /* app shutting down */ }
    }
```

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build src/Inkshelf`
Expected: `Build succeeded`, 0 warnings.

Run: `dotnet test`
Expected: all green. `ConvertWorkerTests` exercises `ExecuteAsync`; if a test now hangs or fails on the extra task, the cause is the worker no longer completing when its queue drains, which is expected and correct - the sweep loop runs until `stoppingToken` fires. Fix the test by cancelling the token it passes, not by removing the loop.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs src/Inkshelf/Convert/ConvertWorker.cs
git commit -m "feat: sweep the epub cache by age at startup and daily"
```

---

### Task 3: Docs

**Files:**
- Modify: `README.md` (configuration table)
- Modify: `docs/ROADMAP.md` (`## Done`)

Not `docs/ARCHITECTURE.md`: a second eviction axis on an existing cache fits the structure already described, and the "map, not a diary" rule says a feature that changes no invariant gets no entry.

Not `CHANGELOG.md`: it is written by the release skill only.

- [ ] **Step 1: README row**

In the configuration table, directly after the `MaxCacheBytes` row:

```markdown
| `MaxCacheAgeDays`         | `30`                 | Delete cached EPUBs older than this many days, swept at startup and once a day. Set `0` to keep them until the size cap evicts them. |
```

- [ ] **Step 2: ROADMAP**

Add at the top of the `## Done` list:

```markdown
- **Cache age eviction** - `MaxCacheAgeDays` (default 30, `0` disables) deletes
  cached EPUBs past that age, swept at startup and daily. The size cap alone let
  an idle deployment hold its cache forever, and a converted EPUB is dead weight
  once it has reached the reader.
```

- [ ] **Step 3: Verify**

Run: `dotnet test`
Expected: all green.

Run: `grep -rnP '[\x{2013}\x{2014}]' README.md docs/ROADMAP.md src/Inkshelf/Convert/EpubCache.cs src/Inkshelf/Convert/ConvertWorker.cs src/Inkshelf/AbsOptions.cs`
Expected: no output.

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

Run: `tools/uicheck/run.sh`
Expected: PASS. No page changes, so this is a regression check that the worker still starts and conversions still run.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/ROADMAP.md
git commit -m "docs: document cache age eviction"
```
