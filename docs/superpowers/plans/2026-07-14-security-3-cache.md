# Security #3 — scr clamp + EPUB cache LRU cap — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the client-controlled `scr` cookie from minting arbitrary cache-key sizes, and bound the EPUB cache's total disk use with an LRU sweep.

**Architecture:** `ScreenTarget.FromCookie` clamps each parsed dimension to `[1, 4096]`. `EpubCache` gains `EnforceCap(maxBytes)` (delete oldest by `LastWriteTimeUtc` until under the cap) and `Touch(path)` (bump a served file's timestamp for true-LRU). `ConvertService` touches on a cached serve and enforces the cap (`AbsOptions.MaxCacheBytes`, config `MaxCacheBytes`, default 1 GB) after a conversion.

**Tech Stack:** .NET 10, xUnit.

## Global Constraints

- New config on `AbsOptions` (key `MaxCacheBytes`, default `1073741824` = 1 GB). `dotnet test` green after every task.
- Legitimate users unaffected: real device sizes are well under 4096; the cap only evicts once total cache exceeds 1 GB.
- Conventional Commits; no `Co-Authored-By`. Branch `security/hardening`.

## File Structure

**Task 1:** Modify `src/Inkshelf/Convert/ScreenTarget.cs`; `tests/Inkshelf.Tests/ScreenTargetTests.cs`.
**Task 2:** Modify `src/Inkshelf/AbsOptions.cs`, `src/Inkshelf/Program.cs`, `src/Inkshelf/Convert/EpubCache.cs`, `src/Inkshelf/Convert/ConvertService.cs`, `tests/Inkshelf.Tests/EpubCacheTests.cs`, `tests/Inkshelf.Tests/ConvertServiceTests.cs`.

---

## Task 1: Clamp the scr cookie dimensions

**Files:** `src/Inkshelf/Convert/ScreenTarget.cs`, `tests/Inkshelf.Tests/ScreenTargetTests.cs`.

**Interfaces:** adds `public const int MaxDimension = 4096;` to `ScreenTarget`; `FromCookie` behavior clamps but the signature is unchanged.

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (82).

- [ ] **Step 2: Write the failing clamp test**

In `tests/Inkshelf.Tests/ScreenTargetTests.cs`, add:

```csharp
    [Fact]
    public void FromCookie_clamps_oversized_dimensions()
    {
        var (w, h, dpr) = ScreenTarget.FromCookie("9999x9999x1");
        Assert.Equal(ScreenTarget.MaxDimension, w);
        Assert.Equal(ScreenTarget.MaxDimension, h);
        Assert.Equal(1.0, dpr, 3);
    }

    [Fact]
    public void FromCookie_leaves_in_range_dimensions_untouched()
    {
        var (w, h, _) = ScreenTarget.FromCookie("768x1024x1");
        Assert.Equal(768, w);
        Assert.Equal(1024, h);
    }
```

- [ ] **Step 3: Run — verify fail**

Run: `dotnet test --filter FullyQualifiedName~ScreenTargetTests`
Expected: FAIL — `ScreenTarget.MaxDimension` doesn't exist (compile error), and/or the clamp assertion fails.

- [ ] **Step 4: Add the clamp**

In `src/Inkshelf/Convert/ScreenTarget.cs`, add the constant near `Retina`:

```csharp
    // Upper bound on a page dimension fed into the converter + cache key, so a
    // client-set "scr" cookie can't mint absurd sizes (disk exhaustion / OOM).
    public const int MaxDimension = 4096;
```

In `FromCookie`, clamp in both parse paths. For the 3-part path, before the `return`:

```csharp
                && cw > 0 && ch > 0 && dpr > 0)
            {
                cw = Math.Min(cw, MaxDimension);
                ch = Math.Min(ch, MaxDimension);
                return Retina
                    ? ((int)Math.Round(cw * dpr), (int)Math.Round(ch * dpr), dpr)
                    : (cw, ch, 1);
            }
```

For the legacy 2-part path:

```csharp
            if (p.Length == 2 && int.TryParse(p[0], out var w2) && int.TryParse(p[1], out var h2) && w2 > 0 && h2 > 0)
                return (Math.Min(w2, MaxDimension), Math.Min(h2, MaxDimension), 1);
```

(Convert the existing single-expression `if` body to the braced form shown for the 3-part path.)

- [ ] **Step 5: Run ScreenTarget tests — GREEN**

Run: `dotnet test --filter FullyQualifiedName~ScreenTargetTests`
Expected: PASS (existing parse tests + the two new clamp tests).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/ScreenTarget.cs tests/Inkshelf.Tests/ScreenTargetTests.cs
git commit -m "feat: clamp scr cookie dimensions to a safe maximum"
```

---

## Task 2: EPUB cache LRU cap + touch-on-serve

**Files:** as above.

**Interfaces:**
- `AbsOptions` gains `long MaxCacheBytes` (default `1073741824`).
- `EpubCache` gains `void EnforceCap(long maxBytes)` and `void Touch(string path)`.
- `ConvertService` constructor gains `AbsOptions options`.

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (84 after Task 1).

- [ ] **Step 2: Add the option + bind it**

`src/Inkshelf/AbsOptions.cs`:

```csharp
    // Soft cap on total EPUB cache bytes; oldest entries are evicted past it. Default 1 GiB.
    public long MaxCacheBytes { get; set; } = 1_073_741_824;
```

`Program.cs` `AbsOptions` initializer:

```csharp
    MaxCacheBytes = long.TryParse(builder.Configuration["MaxCacheBytes"], out var mcb) && mcb > 0 ? mcb : 1_073_741_824,
```

- [ ] **Step 3: Write the failing EpubCache tests**

In `tests/Inkshelf.Tests/EpubCacheTests.cs`, add (use a temp dir; write files with staggered `LastWriteTimeUtc`):

```csharp
    [Fact]
    public void EnforceCap_deletes_oldest_until_under_cap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inkshelf-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new Inkshelf.Convert.EpubCache(dir);
        try
        {
            // three 100-byte files, ages oldest→newest
            var now = DateTime.UtcNow;
            for (var i = 0; i < 3; i++)
            {
                var p = Path.Combine(dir, $"item{i}-1-1-10x10.epub");
                File.WriteAllBytes(p, new byte[100]);
                File.SetLastWriteTimeUtc(p, now.AddMinutes(i)); // item0 oldest, item2 newest
            }
            cache.EnforceCap(250); // room for ~2 files → oldest (item0) evicted
            Assert.False(File.Exists(Path.Combine(dir, "item0-1-1-10x10.epub")));
            Assert.True(File.Exists(Path.Combine(dir, "item2-1-1-10x10.epub")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Touch_bumps_last_write_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inkshelf-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new Inkshelf.Convert.EpubCache(dir);
        try
        {
            var p = Path.Combine(dir, "x-1-1-10x10.epub");
            File.WriteAllBytes(p, new byte[10]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-1));
            cache.Touch(p);
            Assert.True(File.GetLastWriteTimeUtc(p) > DateTime.UtcNow.AddMinutes(-1));
        }
        finally { Directory.Delete(dir, true); }
    }
```

- [ ] **Step 4: Run — verify fail to compile**

Run: `dotnet test --filter FullyQualifiedName~EpubCacheTests`
Expected: FAIL to compile — `EnforceCap`/`Touch` don't exist.

- [ ] **Step 5: Implement in `EpubCache.cs`**

Append to `src/Inkshelf/Convert/EpubCache.cs`:

```csharp
    // Bump a served file's timestamp so EnforceCap treats recently-used entries as
    // "new" (approximate LRU — serving a file doesn't otherwise touch its mtime).
    public void Touch(string path)
    {
        try { if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
    }

    // Evict oldest-by-write-time entries until total cache bytes are under the cap.
    // No-op when maxBytes <= 0 or already under. Best-effort (ignores IO races).
    public void EnforceCap(long maxBytes)
    {
        if (maxBytes <= 0) return;
        var files = new DirectoryInfo(_dir).GetFiles("*.epub");
        var total = files.Sum(f => f.Length);
        if (total <= maxBytes) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= maxBytes) break;
            try { total -= f.Length; f.Delete(); } catch (IOException) { }
        }
    }
```

(`System.Linq` is available via implicit usings.)

- [ ] **Step 6: Run EpubCache tests — GREEN**

Run: `dotnet test --filter FullyQualifiedName~EpubCacheTests`
Expected: PASS (existing + 2 new).

- [ ] **Step 7: Wire into `ConvertService`**

Add `AbsOptions` to the constructor and use the new cache methods. Constructor:

```csharp
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly EpubConverter _converter;
    private readonly AbsOptions _options;
    private readonly ILogger<ConvertService> _logger;

    public ConvertService(AbsApiClient api, EpubCache cache, EpubConverter converter,
        AbsOptions options, ILogger<ConvertService> logger)
    {
        _api = api; _cache = cache; _converter = converter; _options = options; _logger = logger;
    }
```

In `ConvertAsync`, in the convert-on-miss branch, after the successful `ConvertAsync` + log line, enforce the cap:

```csharp
            _cache.EnforceCap(_options.MaxCacheBytes);
```

In the `else` (cached-serve) branch, touch the file so it counts as recently used:

```csharp
        else
        {
            _cache.Touch(path);
            _logger.LogInformation("Serving cached EPUB for {Id} ({OutBytes} bytes)", id, new FileInfo(path).Length);
        }
```

(`AbsOptions` resolves via the enclosing `Inkshelf` namespace — the file is in `Inkshelf.Convert`.)

- [ ] **Step 8: Fix `ConvertServiceTests` construction**

`tests/Inkshelf.Tests/ConvertServiceTests.cs`'s `Service` helper constructs `ConvertService`; add the `AbsOptions` arg (a large cap so the existing tests never trigger eviction):

```csharp
    private static ConvertService Service(AbsApiClient api, EpubCache cache) =>
        new(api, cache, new EpubConverter(), new AbsOptions { MaxCacheBytes = long.MaxValue },
            NullLogger<ConvertService>.Instance);
```

Add `using Inkshelf;` to the test file if `AbsOptions` doesn't resolve.

- [ ] **Step 9: Full suite**

Run: `dotnet test`
Expected: PASS (84 + 2 new EpubCache = 86). Existing `ConvertServiceTests` (cached/warm/not-found) still pass — the cached path now also touches the file (harmless) and the cap is effectively unlimited in tests.

- [ ] **Step 10: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs src/Inkshelf/Convert/EpubCache.cs src/Inkshelf/Convert/ConvertService.cs tests/Inkshelf.Tests/EpubCacheTests.cs tests/Inkshelf.Tests/ConvertServiceTests.cs
git commit -m "feat: cap EPUB cache with LRU eviction and touch-on-serve"
```

---

## Self-Review

**Spec coverage (#3):** `scr` dimensions clamped to `[1,4096]` in both parse paths (Task 1); global max-bytes LRU sweep via `EnforceCap` after conversion + `Touch` on cached serve for true-LRU (Task 2); `MaxCacheBytes` config, default 1 GB. ✓

**Placeholder scan:** None — clamp asserts exact values; cache tests assert which file survives eviction and that Touch advances the timestamp.

**Type consistency:** `ScreenTarget.MaxDimension` used in impl + tests. `EpubCache.EnforceCap(long)`/`Touch(string)` match tests and the `ConvertService` call sites. `ConvertService`'s new 5-arg constructor matches the `ConvertServiceTests.Service` helper.

**Edge note:** `EnforceCap` could in principle delete a just-written file if a single EPUB exceeded the whole cap; at a 1 GB default vs. tens-of-MB EPUBs this can't occur in practice. Documented rather than special-cased.

**Scope:** Two tasks. #5 (ConvertLock) and #4 (archive ceiling) are the next just-in-time plans — both also touch `ConvertService`, building on the `AbsOptions` dependency added here.
