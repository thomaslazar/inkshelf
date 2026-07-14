# Security #4 — Archive size ceiling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop a decompression-bomb / oversized CBZ from OOMing the sidecar by bounding how many bytes `ConvertService` will buffer from the ebook stream.

**Architecture:** `ConvertService` copies the ebook stream into its `MemoryStream` through a bounded copy that aborts once `AbsOptions.MaxArchiveBytes` (config `MaxArchiveBytes`, default 500 MB) is exceeded; on abort it logs a warning and returns `ConvertOutcome.NotFound` (the endpoint's existing "can't convert" signal — no new outcome kind). The bounded copy lands inside the already-locked convert-on-miss block from #5.

**Tech Stack:** .NET 10, xUnit.

## Global Constraints

- New config on `AbsOptions` (key `MaxArchiveBytes`, default `524288000` = 500 MB). `dotnet test` green.
- Legitimate archives (well under 500 MB; conversion downscales anyway) convert exactly as before — only oversized/bomb inputs are refused.
- Deterministic bound: abort during the copy (never buffer more than ~one chunk past the limit). No temp-file spooling.
- Conventional Commits; no `Co-Authored-By`. Branch `security/hardening`.

## File Structure

- Modify: `src/Inkshelf/AbsOptions.cs`, `src/Inkshelf/Program.cs`, `src/Inkshelf/Convert/ConvertService.cs`, `tests/Inkshelf.Tests/ConvertServiceTests.cs`

---

## Task 1: Ceiling the archive buffer

**Files:** as above.

**Interfaces:** `AbsOptions` gains `long MaxArchiveBytes` (default `524288000`).

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (89).

- [ ] **Step 2: Add the option + bind it**

`src/Inkshelf/AbsOptions.cs`:

```csharp
    // Max bytes buffered from an ebook archive before conversion; larger archives
    // are refused (decompression-bomb / OOM guard). Default 500 MiB.
    public long MaxArchiveBytes { get; set; } = 524_288_000;
```

`Program.cs` `AbsOptions` initializer:

```csharp
    MaxArchiveBytes = long.TryParse(builder.Configuration["MaxArchiveBytes"], out var mab) && mab > 0 ? mab : 524_288_000,
```

- [ ] **Step 3: Write the failing ceiling test**

In `tests/Inkshelf.Tests/ConvertServiceTests.cs`, first extend the `Service` helper to accept an archive-bytes cap (default huge so existing tests are unaffected):

```csharp
    private static ConvertService Service(AbsApiClient api, EpubCache cache, long maxArchiveBytes = long.MaxValue) =>
        new(api, cache, new EpubConverter(), new ConvertLock(),
            new AbsOptions { MaxCacheBytes = long.MaxValue, MaxArchiveBytes = maxArchiveBytes },
            NullLogger<ConvertService>.Instance);
```

Then add the test (a cbz detail with an empty cache → the convert path runs; the stub returns the detail JSON as the "ebook stream", which exceeds a tiny cap → `NotFound`):

```csharp
    [Fact]
    public async Task ConvertAsync_returns_NotFound_when_archive_exceeds_ceiling()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path); // empty → cache miss → enters convert path
        // Tiny ceiling; the stubbed ebook stream (the detail JSON bytes) is larger.
        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 123, 456)), cache, maxArchiveBytes: 8);
        var outcome = await svc.ConvertAsync("item1", fresh: false, warm: false, 100, 200, 1.0, default);
        Assert.Equal(ConvertResultKind.NotFound, outcome.Kind);
    }
```

- [ ] **Step 4: Run — verify fail**

Run: `dotnet test --filter FullyQualifiedName~ConvertServiceTests`
Expected: FAIL to compile first (the `Service` helper gains a parameter — pre-existing callers still compile via the default; the new test references it), then the new test FAILS because the ceiling isn't enforced yet (conversion proceeds / throws instead of returning `NotFound`).

- [ ] **Step 5: Enforce the ceiling in `ConvertService`**

In `src/Inkshelf/Convert/ConvertService.cs`, inside the locked convert-on-miss block, replace the unbounded buffering:

```csharp
                    var (archive, _) = await _api.GetEbookStreamAsync(id, ct);
                    using var buffered = new MemoryStream();
                    await using (archive) await archive.CopyToAsync(buffered, ct);   // SharpCompress needs a seekable stream
                    buffered.Position = 0;
```

with a bounded copy that bails out past the ceiling:

```csharp
                    var (archive, _) = await _api.GetEbookStreamAsync(id, ct);
                    using var buffered = new MemoryStream();
                    await using (archive)
                    {
                        if (!await CopyWithLimitAsync(archive, buffered, _options.MaxArchiveBytes, ct))
                        {
                            _logger.LogWarning("Archive for {Id} exceeds {Limit} bytes — refusing to convert.", id, _options.MaxArchiveBytes);
                            return ConvertOutcome.NotFound;
                        }
                    }
                    buffered.Position = 0;
```

Add the helper (alongside `Sanitize`):

```csharp
    // Copy src → dst, aborting (returning false) as soon as more than `limit` bytes
    // are read, so a huge/decompression-bomb archive can't be buffered into memory.
    // limit <= 0 disables the cap.
    private static async Task<bool> CopyWithLimitAsync(Stream src, Stream dst, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (limit > 0 && total > limit) return false;
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return true;
    }
```

Note: the early `return ConvertOutcome.NotFound` sits inside the `using (await _lock.AcquireAsync(...))` block — the `using` disposes the lock on the way out, so the semaphore is released correctly. No file was written, so nothing to touch/clean.

- [ ] **Step 6: Run ConvertService tests — GREEN**

Run: `dotnet test --filter FullyQualifiedName~ConvertServiceTests`
Expected: PASS — the new ceiling test plus the existing three (NotFound wrong-format, File cached, Warmed cached; the cached ones pre-write the file so they never reach the copy).

- [ ] **Step 7: Full suite**

Run: `dotnet test`
Expected: PASS (89 + 1 new = 90).

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs src/Inkshelf/Convert/ConvertService.cs tests/Inkshelf.Tests/ConvertServiceTests.cs
git commit -m "feat: bound archive buffering with a MaxArchiveBytes ceiling"
```

---

## Self-Review

**Spec coverage (#4):** archive buffering bounded by `MaxArchiveBytes` (default 500 MB) via a copy that aborts past the limit; over-limit → warning log + `ConvertOutcome.NotFound` (reuses the existing outcome, no new kind); no temp-file spooling. ✓

**Placeholder scan:** None — the new test asserts `NotFound` when the streamed bytes exceed a tiny cap.

**Type consistency:** `AbsOptions.MaxArchiveBytes` used in `Program.cs` binding + `ConvertService`. `CopyWithLimitAsync(Stream, Stream, long, CancellationToken) : Task<bool>` matches its single call site. `Service` helper's new optional `maxArchiveBytes` parameter defaults to `long.MaxValue`, so the three existing tests are unaffected.

**Interaction with #5:** the bounded copy and its early `return` are inside the `using (await _lock…)` block — the lock releases on return; correct. The ceiling check happens before any `.tmp` is written, so an over-limit request never creates a partial cache file.

**Scope:** One task. Only the docs step remains after this.
