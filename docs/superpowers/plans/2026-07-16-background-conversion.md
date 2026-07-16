# Background Conversion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move CBZ/CBR→EPUB conversion off the HTTP request onto a background worker so a client disconnect can't kill it, and surface progress via a JS poll (5s) with a no-JS `<noscript>` listing meta-refresh (30s).

**Architecture:** A singleton `ConvertQueue` (in-memory registry + `System.Threading.Channels` producer) fed by a reshaped `ConvertService` "kick" (which fetches ABS detail, captures the access token, and enqueues). A `ConvertWorker` `BackgroundService` drains the channel on the app lifetime, downloads the archive with a new handler-free `AbsDownloadClient` (the worker has no `HttpContext`, so it can't use `AbsApiClient`), converts, and records terminal state. "Done" is the atomic existence of the `.epub` on disk; the registry only tracks `Queued`/`Running`/`Failed`.

**Tech Stack:** ASP.NET Core Razor Pages + minimal APIs, .NET 10, xUnit, ImageSharp (existing). No new NuGet — `System.Threading.Channels` and `BackgroundService` ship in the shared framework.

**Spec:** `docs/superpowers/specs/2026-07-16-background-conversion-design.md`

## Global Constraints

- **No AOT. No new NuGet dependency.** Queue is built-in `Channel` + `BackgroundService`.
- **Near-zero client JS.** Only the two inline scripts in `_Layout.cshtml` may exist; this evolves the convert-warm one. **Real e-ink device test required before merge** (Task 6). Defensive CSS only — no `object-fit`, no flex `gap`. ES5 in inline scripts (old e-reader engines).
- **New config extends `AbsOptions`** (one config surface). Booleans/longs parse from strings with a default fallback.
- **`dotnet test` green after every task.** Run from repo root inside the devcontainer.
- **Conventional Commits**; `type: subject`, imperative, lowercase, ≤72 chars. **No** `Co-Authored-By` / "Generated with" lines. **Ask before committing** — the per-task commit steps below are the plan's intent, but confirm with the owner.
- **Load-bearing auth conventions** (`ARCHITECTURE.md`): the two ABS clients stay as-is; the new `AbsDownloadClient` is a deliberate **third** client — handler-free, caller-supplied bearer, **no** refresh, worker-only. It MUST send a non-empty `User-Agent` (the ABS proxy 403s an empty one). Never attach `AbsAuthHandler` to it.
- **ABS source of truth** for API shapes: `temp/audiobookshelf/` (not the stale hosted docs).

---

## File Structure

**Create:**
- `src/Inkshelf/Convert/ConvertJob.cs` — `ConvertJob` record, `ConvertStatus` enum.
- `src/Inkshelf/Convert/ConvertQueue.cs` — registry + channel producer (singleton).
- `src/Inkshelf/Abs/AbsDownloadClient.cs` — handler-free authenticated ebook download.
- `src/Inkshelf/Convert/ConvertWorker.cs` — `BackgroundService` consumer.
- `tests/Inkshelf.Tests/ConvertQueueTests.cs`
- `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`
- `tests/Inkshelf.Tests/ConvertWorkerTests.cs`

**Modify:**
- `src/Inkshelf/AbsOptions.cs` — add `MaxConcurrentConversions`.
- `src/Inkshelf/Convert/EpubCache.cs` — add `SweepTemp()`.
- `src/Inkshelf/Convert/ConvertService.cs` — reshape into the kick (`KickAsync`/`StatusAsync`); drop the download/convert body.
- `src/Inkshelf/Endpoints/ConvertEndpoints.cs` — new param surface (`warm`/`status`/`fresh`/`return`).
- `src/Inkshelf/Program.cs` — register `ConvertQueue` (singleton), `ConvertWorker` (hosted), `AbsDownloadClient` (typed client).
- `src/Inkshelf/Pages/Library.cshtml.cs` — precompute per-row convert state + `AnyConverting`.
- `src/Inkshelf/Pages/Support/ItemRowModel.cs` — carry `State` + `ReturnUrl` instead of `Cached`.
- `src/Inkshelf/Pages/Shared/_ItemRow.cshtml` — render the four convert states.
- `src/Inkshelf/Pages/Library.cshtml` — `<noscript>` meta-refresh when `AnyConverting`.
- `src/Inkshelf/Pages/Shared/_Layout.cshtml` — evolve the warm script (kick → poll; resume-on-load).
- `tests/Inkshelf.Tests/ConvertServiceTests.cs` — rewrite for the kick.
- `tests/Inkshelf.Tests/EndpointTests.cs` — add convert-endpoint cases.
- `docs/ARCHITECTURE.md`, `docs/ROADMAP.md` — Task 7.

---

## Task 1: `AbsOptions.MaxConcurrentConversions`

**Files:**
- Modify: `src/Inkshelf/AbsOptions.cs`
- Modify: `src/Inkshelf/Program.cs:13-23` (bind the new key)

**Interfaces:**
- Produces: `AbsOptions.MaxConcurrentConversions` (int, default `1`), config key `MaxConcurrentConversions`.

- [ ] **Step 1: Add the property**

In `src/Inkshelf/AbsOptions.cs`, after `MaxArchiveBytes`:

```csharp
    // Max conversions the background worker runs at once. Default 1 — a small
    // host must not run two ImageSharp resizes concurrently (CPU/RAM thrash).
    public int MaxConcurrentConversions { get; set; } = 1;
```

- [ ] **Step 2: Bind it in Program.cs**

In `src/Inkshelf/Program.cs`, inside the `absOptions` initializer (after the `MaxArchiveBytes` line):

```csharp
    MaxConcurrentConversions = int.TryParse(builder.Configuration["MaxConcurrentConversions"], out var mcc) && mcc > 0 ? mcc : 1,
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Inkshelf/Inkshelf.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs
git commit -m "feat: add MaxConcurrentConversions option"
```

---

## Task 2: `ConvertJob` + `ConvertQueue`

**Files:**
- Create: `src/Inkshelf/Convert/ConvertJob.cs`
- Create: `src/Inkshelf/Convert/ConvertQueue.cs`
- Test: `tests/Inkshelf.Tests/ConvertQueueTests.cs`

**Interfaces:**
- Consumes: `EbookMeta` (from `EpubWriter.cs`, existing).
- Produces:
  - `enum ConvertStatus { None, Queued, Running, Done, Failed }`
  - `record ConvertJob(string ItemId, string AccessToken, string CachePath, EbookMeta Meta, int MaxW, int MaxH, double Dpr)`
  - `ConvertQueue` (singleton) with: `ChannelReader<ConvertJob> Reader`, `ConvertStatus Enqueue(ConvertJob)`, `ConvertStatus Status(string cachePath)`, `void MarkRunning(string)`, `void MarkDone(string)`, `void MarkFailed(string)`. Constructor takes optional `Func<DateTime>? clock` (defaults to `() => DateTime.UtcNow`) for TTL testing.

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/ConvertQueueTests.cs`:

```csharp
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertQueueTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "cq-" + Guid.NewGuid().ToString("N") + ".epub");

    private static ConvertJob Job(string path) =>
        new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"), 100, 200, 1.0);

    [Fact]
    public void Enqueue_new_path_returns_Queued_and_writes_one_channel_item()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        Assert.Equal(ConvertStatus.Queued, q.Enqueue(Job(path)));
        Assert.True(q.Reader.TryRead(out var job));
        Assert.Equal(path, job!.CachePath);
        Assert.False(q.Reader.TryRead(out _)); // exactly one
    }

    [Fact]
    public void Enqueue_is_idempotent_while_queued()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        Assert.Equal(ConvertStatus.Queued, q.Enqueue(Job(path))); // no-op
        Assert.True(q.Reader.TryRead(out _));
        Assert.False(q.Reader.TryRead(out _)); // still only one
    }

    [Fact]
    public void Status_is_Done_when_file_exists_and_clears_registry()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        File.WriteAllText(path, "epub");
        try
        {
            q.Enqueue(Job(path)); // even if we thought it queued...
            Assert.Equal(ConvertStatus.Done, q.Status(path)); // file wins
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Status_is_None_for_unknown_path() =>
        Assert.Equal(ConvertStatus.None, new ConvertQueue().Status(TempPath()));

    [Fact]
    public void MarkFailed_shows_Failed_and_a_retry_reenqueues()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkFailed(path);
        Assert.Equal(ConvertStatus.Failed, q.Status(path));
        Assert.Equal(ConvertStatus.Queued, q.Enqueue(Job(path))); // retry clears Failed
        Assert.True(q.Reader.TryRead(out _));
    }

    [Fact]
    public void Failed_sweeps_to_None_after_ttl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var q = new ConvertQueue(() => now);
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkFailed(path);
        now = now.AddMinutes(11); // past the 10-min TTL
        Assert.Equal(ConvertStatus.None, q.Status(path));
    }

    [Fact]
    public void MarkDone_removes_the_entry()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkRunning(path);
        q.MarkDone(path);
        Assert.Equal(ConvertStatus.None, q.Status(path)); // no file, no entry
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ConvertQueueTests`
Expected: FAIL — `ConvertQueue` / `ConvertStatus` / `ConvertJob` don't exist (compile error).

- [ ] **Step 3: Create `ConvertJob.cs`**

```csharp
namespace Inkshelf.Convert;

// Client-facing conversion status. "Done" is the atomic existence of the .epub
// on disk; the registry only ever holds Queued/Running/Failed.
public enum ConvertStatus { None, Queued, Running, Done, Failed }

// Everything the token-less background worker needs to convert one item. The
// access token is captured in the request (the kick) because the worker has no
// HttpContext to read the session cookie from.
public sealed record ConvertJob(
    string ItemId, string AccessToken, string CachePath,
    EbookMeta Meta, int MaxW, int MaxH, double Dpr);
```

- [ ] **Step 4: Create `ConvertQueue.cs`**

```csharp
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
    private sealed class Entry { public Phase Phase; public DateTime FailedAtUtc; }

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

    public void MarkFailed(string cachePath)
    {
        lock (_gate) _entries[cachePath] = new Entry { Phase = Phase.Failed, FailedAtUtc = _clock() };
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ConvertQueueTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/ConvertJob.cs src/Inkshelf/Convert/ConvertQueue.cs tests/Inkshelf.Tests/ConvertQueueTests.cs
git commit -m "feat: add ConvertQueue job registry and channel"
```

---

## Task 3: `AbsDownloadClient`

**Files:**
- Create: `src/Inkshelf/Abs/AbsDownloadClient.cs`
- Modify: `src/Inkshelf/Program.cs` (register the typed client)
- Test: `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`

**Interfaces:**
- Produces: `AbsDownloadClient` with `Task<Stream> DownloadEbookAsync(string itemId, string accessToken, CancellationToken ct)`. Sends `GET /api/items/{id}/ebook` with `Authorization: Bearer <token>`; throws `HttpRequestException` on non-success (worker maps to `Failed`).

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`:

```csharp
using System.Net;
using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsDownloadClientTests
{
    private static AbsDownloadClient Client(StubHandler stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://abs.local") });

    [Fact]
    public async Task Sends_bearer_and_returns_stream()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });
        var client = Client(stub);

        await using var s = await client.DownloadEbookAsync("item9", "TOKEN123", default);

        Assert.Equal("/api/items/item9/ebook", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", stub.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("TOKEN123", stub.Last!.Headers.Authorization!.Parameter);
        Assert.Equal(3, s.Length);
    }

    [Fact]
    public async Task Throws_on_401()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DownloadEbookAsync("item9", "stale", default));
    }
}
```

Note: the production `User-Agent` is applied by `ConfigureAbs` in `Program.cs`, not inside the client, so these unit tests (which construct the `HttpClient` directly) don't assert it. Task 5's endpoint test exercises the real DI wiring.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AbsDownloadClientTests`
Expected: FAIL — `AbsDownloadClient` doesn't exist.

- [ ] **Step 3: Create `AbsDownloadClient.cs`**

```csharp
using System.Net.Http.Headers;

namespace Inkshelf.Abs;

// The background worker's authenticated ebook download. This is a DELIBERATE
// THIRD ABS client, distinct from the two load-bearing ones (AbsAuthClient
// login/refresh; AbsApiClient data). It is HANDLER-FREE: the worker has no
// HttpContext, so AbsAuthHandler (which resolves the token from the request)
// cannot run — the caller supplies the bearer instead. It does NOT refresh on
// 401 (that would need HttpContext to persist the new token); a failure just
// fails the job and the user re-taps with a fresh token.
//
// Registered via ConfigureAbs so it inherits the BaseAddress AND the User-Agent
// the ABS reverse proxy requires (it 403s an empty UA). Never attach
// AbsAuthHandler to it; never use it from a request path (use AbsApiClient there).
public sealed class AbsDownloadClient
{
    private readonly HttpClient _http;
    public AbsDownloadClient(HttpClient http) => _http = http;

    // Caller owns (and must dispose) the returned stream.
    public async Task<Stream> DownloadEbookAsync(string itemId, string accessToken, CancellationToken ct)
    {
        var url = $"/api/items/{Uri.EscapeDataString(itemId)}/ebook";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new HttpRequestException($"ebook download failed for {itemId}: {(int)res.StatusCode}");
        }
        return await res.Content.ReadAsStreamAsync(ct);
    }
}
```

- [ ] **Step 4: Register it in Program.cs**

In `src/Inkshelf/Program.cs`, right after the `AbsApiClient` registration (line ~53):

```csharp
// Handler-FREE (no AbsAuthHandler) — the worker supplies the bearer; ConfigureAbs
// gives it the BaseAddress + required User-Agent. See AbsDownloadClient.
builder.Services.AddHttpClient<AbsDownloadClient>(ConfigureAbs);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter AbsDownloadClientTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Abs/AbsDownloadClient.cs src/Inkshelf/Program.cs tests/Inkshelf.Tests/AbsDownloadClientTests.cs
git commit -m "feat: add handler-free AbsDownloadClient for the worker"
```

---

## Task 4: `ConvertWorker` + `EpubCache.SweepTemp`

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs` (add `SweepTemp`)
- Create: `src/Inkshelf/Convert/ConvertWorker.cs`
- Modify: `src/Inkshelf/Program.cs` (register hosted service)
- Test: `tests/Inkshelf.Tests/ConvertWorkerTests.cs`

**Interfaces:**
- Consumes: `ConvertQueue`, `AbsDownloadClient` (resolved per-job via `IServiceScopeFactory`), `EpubConverter`, `ConvertLock`, `EpubCache`, `AbsOptions` (all existing / from Tasks 2–3).
- Produces: `ConvertWorker : BackgroundService`; `EpubCache.SweepTemp()` (deletes `*.tmp` in the cache dir).

- [ ] **Step 1: Write the failing test for `SweepTemp`**

Add to `tests/Inkshelf.Tests/EpubCacheTests.cs` (new `[Fact]`):

```csharp
    [Fact]
    public void SweepTemp_deletes_tmp_but_keeps_epub()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "a.epub.tmp"), "partial");
        File.WriteAllText(Path.Combine(dir.Path, "b.epub"), "real");
        cache.SweepTemp();
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.epub.tmp")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b.epub")));
    }
```

(`EpubCacheTests` already has a `TempDir` helper; reuse it. If not present in that file, copy the `TempDir` class from `ConvertServiceTests.cs`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter EpubCacheTests`
Expected: FAIL — `SweepTemp` not defined.

- [ ] **Step 3: Add `SweepTemp` to `EpubCache.cs`**

```csharp
    // Delete orphan .tmp files (a crash/shutdown between EpubWriter's temp write
    // and its atomic rename leaves one). Called once at worker startup.
    public void SweepTemp()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*.tmp"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
```

- [ ] **Step 4: Run to verify SweepTemp passes**

Run: `dotnet test --filter EpubCacheTests`
Expected: PASS.

- [ ] **Step 5: Write the failing tests for the worker**

Create `tests/Inkshelf.Tests/ConvertWorkerTests.cs`:

```csharp
using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertWorkerTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "worker-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static byte[] Cbz()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("p1.jpg").Open())
        {
            using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(80, 120);
            img.Save(s, new JpegEncoder());
        }
        return ms.ToArray();
    }

    // A DI provider exposing an AbsDownloadClient whose HttpClient returns `bytes`.
    private static IServiceScopeFactory ScopeFactoryReturning(byte[] bytes)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) }))
            { BaseAddress = new Uri("http://abs.local") }));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ConvertWorker Worker(ConvertQueue queue, IServiceScopeFactory scopes, EpubCache cache) =>
        new(queue, scopes, new EpubConverter(), new ConvertLock(), cache,
            new AbsOptions { MaxConcurrentConversions = 1, MaxArchiveBytes = long.MaxValue, MaxCacheBytes = long.MaxValue },
            NullLogger<ConvertWorker>.Instance);

    private static ConvertJob Job(string path) =>
        new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"), 0, 0, 1.0);

    [Fact]
    public async Task Processes_a_job_to_a_cached_epub()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache);
        await worker.StartAsync(default);
        await WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.True(File.Exists(path));
        Assert.Equal(ConvertStatus.Done, queue.Status(path));
    }

    [Fact]
    public async Task A_download_failure_marks_Failed_not_Running()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var path = cache.PathFor("item1", 1, 2, 0, 0);
        queue.Enqueue(Job(path));

        // Download client that always 401s → DownloadEbookAsync throws.
        var services = new ServiceCollection();
        services.AddSingleton(new AbsDownloadClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("http://abs.local") }));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var worker = Worker(queue, scopes, cache);
        await worker.StartAsync(default);
        await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        Assert.Equal(ConvertStatus.Failed, queue.Status(path));
        Assert.False(File.Exists(path));
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!cond() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(cond(), "condition not met within timeout");
    }
}
```

- [ ] **Step 6: Run to verify the worker tests fail**

Run: `dotnet test --filter ConvertWorkerTests`
Expected: FAIL — `ConvertWorker` doesn't exist.

- [ ] **Step 7: Create `ConvertWorker.cs`**

```csharp
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Inkshelf.Abs;

namespace Inkshelf.Convert;

// Drains ConvertQueue on the APP LIFETIME (never a request token), so a client
// disconnect can't cancel an in-flight conversion. Runs MaxConcurrentConversions
// consumer loops (default 1). Per job it downloads the archive with the captured
// token, buffers it under the archive ceiling, converts, and records terminal
// state. ConvertLock + a double-checked File.Exists dedup identical targets.
public sealed class ConvertWorker : BackgroundService
{
    private readonly ConvertQueue _queue;
    private readonly IServiceScopeFactory _scopes; // resolve AbsDownloadClient per job
    private readonly EpubConverter _converter;
    private readonly ConvertLock _lock;
    private readonly EpubCache _cache;
    private readonly AbsOptions _options;
    private readonly ILogger<ConvertWorker> _logger;

    public ConvertWorker(ConvertQueue queue, IServiceScopeFactory scopes, EpubConverter converter,
        ConvertLock convertLock, EpubCache cache, AbsOptions options, ILogger<ConvertWorker> logger)
    {
        _queue = queue; _scopes = scopes; _converter = converter;
        _lock = convertLock; _cache = cache; _options = options; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cache.SweepTemp(); // clear orphan .tmp from a prior crash/shutdown

        var loops = Math.Max(1, _options.MaxConcurrentConversions);
        var tasks = new Task[loops];
        for (var i = 0; i < loops; i++) tasks[i] = ConsumeAsync(stoppingToken);
        await Task.WhenAll(tasks);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
                await ProcessAsync(job, ct);
        }
        catch (OperationCanceledException) { /* app shutting down */ }
    }

    private async Task ProcessAsync(ConvertJob job, CancellationToken ct)
    {
        try
        {
            if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }
            _queue.MarkRunning(job.CachePath);

            using (await _lock.AcquireAsync(job.CachePath, ct))
            {
                if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }

                var sw = Stopwatch.StartNew();
                using var scope = _scopes.CreateScope();
                var download = scope.ServiceProvider.GetRequiredService<AbsDownloadClient>();

                await using var archive = await download.DownloadEbookAsync(job.ItemId, job.AccessToken, ct);
                using var buffered = new MemoryStream();
                if (!await CopyWithLimitAsync(archive, buffered, _options.MaxArchiveBytes, ct))
                {
                    _logger.LogWarning("Archive for {Id} exceeds {Limit} bytes — refusing.", job.ItemId, _options.MaxArchiveBytes);
                    _queue.MarkFailed(job.CachePath);
                    return;
                }
                buffered.Position = 0;
                await _converter.ConvertAsync(buffered, job.Meta, job.CachePath, job.MaxW, job.MaxH, job.Dpr, ct);
                _cache.EnforceCap(_options.MaxCacheBytes);
                _logger.LogInformation("Converted {Id} in {Ms} ms", job.ItemId, sw.ElapsedMilliseconds);
            }
            _queue.MarkDone(job.CachePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App stopping mid-convert: leave the .tmp for the next startup sweep,
            // don't mark Failed (a restart re-queues on the next tap anyway).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversion failed for {Id}", job.ItemId);
            _queue.MarkFailed(job.CachePath);
        }
    }

    // Copy src→dst, returning false as soon as more than `limit` bytes are read
    // (decompression-bomb / OOM guard). limit <= 0 disables the cap.
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
}
```

- [ ] **Step 8: Register the hosted service in Program.cs**

In `src/Inkshelf/Program.cs`, after the `ConvertLock` singleton registration (line ~56):

```csharp
builder.Services.AddSingleton<ConvertQueue>();
builder.Services.AddHostedService<ConvertWorker>();
```

- [ ] **Step 9: Run the worker tests to verify they pass**

Run: `dotnet test --filter ConvertWorkerTests`
Expected: PASS (2 tests).

- [ ] **Step 10: Run the full suite (nothing regressed)**

Run: `dotnet test`
Expected: PASS (all).

- [ ] **Step 11: Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs src/Inkshelf/Convert/ConvertWorker.cs src/Inkshelf/Program.cs tests/Inkshelf.Tests/ConvertWorkerTests.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: add ConvertWorker background service and tmp sweep"
```

---

## Task 5: Reshape `ConvertService` into the kick + rewrite the endpoint

This is the switchover: `ConvertService` stops downloading/converting and becomes the kick (detail fetch → validate → capture token → enqueue); the endpoint gains the `warm`/`status`/`fresh`/`return` surface.

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertService.cs` (full rewrite of the body)
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs` (full rewrite)
- Modify: `tests/Inkshelf.Tests/ConvertServiceTests.cs` (rewrite for the kick)
- Modify: `tests/Inkshelf.Tests/EndpointTests.cs` (add convert cases)

**Interfaces:**
- Consumes: `ConvertQueue` (Task 2), `TokenStore` (existing), `AbsApiClient` (existing), `EpubCache` (existing).
- Produces:
  - `readonly record struct KickResult(ConvertStatus Status, string? FilePath, string? DownloadName)`
  - `ConvertService.KickAsync(string id, bool fresh, int maxW, int maxH, double dpr, CancellationToken ct) : Task<KickResult>`
  - `ConvertService.StatusAsync(string id, int maxW, int maxH, double dpr, CancellationToken ct) : Task<KickResult>`
  - `ConvertOutcome` and `ConvertService.ConvertAsync` are **removed**.

- [ ] **Step 1: Rewrite `ConvertServiceTests.cs`**

Replace the file contents (the old outcome-based tests no longer apply; conversion now lives in the worker):

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertServiceTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "svc-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static AbsApiClient DetailClient(string detailJson) =>
        new(new HttpClient(new StubHandler(_ => StubHandler.Json(detailJson)))
        { BaseAddress = new Uri("http://abs.local") });

    private static string DetailJson(string format, string title, string author, long size, long mtime) =>
        $$"""
        {"media":{"metadata":{"title":"{{title}}","authorName":"{{author}}"},
         "ebookFile":{"ebookFormat":"{{format}}","metadata":{"filename":"x.{{format}}","size":{{size}},"mtimeMs":{{mtime}} } } } }
        """;

    // A TokenStore backed by an HttpContext carrying a valid session cookie.
    private static TokenStore TokenStoreWith(string access)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var opts = new AbsOptions();
        var store = new TokenStore(dp, accessor, opts);
        store.Save(new Tokens(access, "refresh"));
        // Save wrote the cookie to the RESPONSE; copy it onto the REQUEST so Read() sees it.
        var setCookie = accessor.HttpContext!.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';')[0].Split('=', 2)[1];
        accessor.HttpContext!.Request.Headers.Cookie = $"inkshelf_session={value}";
        return store;
    }

    private static ConvertService Service(AbsApiClient api, EpubCache cache, ConvertQueue queue, TokenStore store) =>
        new(api, cache, queue, store, NullLogger<ConvertService>.Instance);

    [Fact]
    public async Task KickAsync_returns_None_for_non_comic()
    {
        using var dir = new TempDir();
        var svc = Service(DetailClient(DetailJson("epub", "T", "A", 1, 2)),
            new EpubCache(dir.Path), new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.KickAsync("item1", fresh: false, 100, 200, 1.0, default);
        Assert.Equal(ConvertStatus.None, r.Status);
    }

    [Fact]
    public async Task KickAsync_returns_Done_with_name_when_cached()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(cache.PathFor("item1", 123, 456, 100, 200), "epub");
        var svc = Service(DetailClient(DetailJson("cbz", "My: Comic", "Jane Doe", 123, 456)),
            cache, new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.KickAsync("item1", fresh: false, 100, 200, 1.0, default);
        Assert.Equal(ConvertStatus.Done, r.Status);
        Assert.StartsWith("Jane Doe - My", r.DownloadName);
        Assert.EndsWith(".epub", r.DownloadName);
    }

    [Fact]
    public async Task KickAsync_enqueues_a_job_carrying_the_token_on_miss()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        var queue = new ConvertQueue();
        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 123, 456)),
            cache, queue, TokenStoreWith("TOKEN123"));
        var r = await svc.KickAsync("item1", fresh: false, 100, 200, 1.0, default);
        Assert.Equal(ConvertStatus.Queued, r.Status);
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal("TOKEN123", job!.AccessToken);
        Assert.Equal(cache.PathFor("item1", 123, 456, 100, 200), job.CachePath);
    }

    [Fact]
    public async Task StatusAsync_reports_none_before_any_kick()
    {
        using var dir = new TempDir();
        var svc = Service(DetailClient(DetailJson("cbz", "T", "A", 123, 456)),
            new EpubCache(dir.Path), new ConvertQueue(), TokenStoreWith("tok"));
        var r = await svc.StatusAsync("item1", 100, 200, 1.0, default);
        Assert.Equal(ConvertStatus.None, r.Status);
    }
}
```

- [ ] **Step 2: Run to verify the rewritten tests fail**

Run: `dotnet test --filter ConvertServiceTests`
Expected: FAIL — `KickAsync`/`StatusAsync`/`KickResult` and the new constructor don't exist.

- [ ] **Step 3: Rewrite `ConvertService.cs`**

```csharp
using Inkshelf.Abs;
using Inkshelf.Auth;

namespace Inkshelf.Convert;

public readonly record struct KickResult(ConvertStatus Status, string? FilePath = null, string? DownloadName = null);

// The convert "kick": HTTP-free orchestration that runs IN THE REQUEST SCOPE.
// It fetches item detail (needs the ABS token), validates the format, computes
// the per-device cache path, and — on a miss — captures the access token and
// enqueues a background job. It never downloads or converts; ConvertWorker does
// that on the app lifetime. Kept HTTP-free so it unit-tests without a request.
public class ConvertService
{
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    private readonly TokenStore _tokens;
    private readonly ILogger<ConvertService> _logger;

    public ConvertService(AbsApiClient api, EpubCache cache, ConvertQueue queue,
        TokenStore tokens, ILogger<ConvertService> logger)
    {
        _api = api; _cache = cache; _queue = queue; _tokens = tokens; _logger = logger;
    }

    // Kick a conversion (or serve the cached result). fresh=true regenerates.
    public async Task<KickResult> KickAsync(string id, bool fresh, int maxW, int maxH, double dpr, CancellationToken ct)
    {
        var r = await ResolveAsync(id, maxW, maxH, ct);
        if (r is null) return new KickResult(ConvertStatus.None);
        var (path, meta, downloadName) = r.Value;

        if (fresh) _cache.RemoveForItem(id);
        if (System.IO.File.Exists(path)) return new KickResult(ConvertStatus.Done, path, downloadName);

        var tokens = _tokens.Read();
        if (tokens is null) return new KickResult(ConvertStatus.None); // no session
        var status = _queue.Enqueue(new ConvertJob(id, tokens.Access, path, meta, maxW, maxH, dpr));
        return new KickResult(status);
    }

    // Poll status WITHOUT enqueuing. Done carries the file path + name to stream.
    public async Task<KickResult> StatusAsync(string id, int maxW, int maxH, double dpr, CancellationToken ct)
    {
        var r = await ResolveAsync(id, maxW, maxH, ct);
        if (r is null) return new KickResult(ConvertStatus.None);
        var (path, _, downloadName) = r.Value;
        var status = _queue.Status(path);
        return status == ConvertStatus.Done
            ? new KickResult(ConvertStatus.Done, path, downloadName)
            : new KickResult(status);
    }

    // Fetch detail, validate cbz/cbr, and derive (cache path, EPUB metadata,
    // download filename). null = not found / not a comic.
    private async Task<(string Path, EbookMeta Meta, string DownloadName)?> ResolveAsync(
        string id, int maxW, int maxH, CancellationToken ct)
    {
        AbsItemDetail detail;
        try { detail = await _api.GetItemDetailAsync(id, ct); }
        catch (HttpRequestException) { return null; }

        var ef = detail.Media?.EbookFile;
        var fmt = ef?.EbookFormat;
        if (ef?.Metadata is null || (fmt != "cbz" && fmt != "cbr")) return null;

        var md = detail.Media!.Metadata!;
        var title = md.Title ?? "Untitled";
        var author = md.AuthorName is { Length: > 0 } an ? an
            : (md.Authors is { Count: > 0 } ? md.Authors[0].Name : "Unknown");
        var seq = md.Series is { Count: > 0 } ? md.Series[0].Sequence : null;
        var seriesName = md.Series is { Count: > 0 } ? md.Series[0].Name : md.SeriesName;

        var path = _cache.PathFor(id, ef.Metadata.Size, ef.Metadata.MtimeMs, maxW, maxH);
        var meta = new EbookMeta(title, author, seriesName, seq, id);
        var downloadName = Sanitize($"{author} - {title}") + ".epub";
        return (path, meta, downloadName);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
```

- [ ] **Step 4: Rewrite `ConvertEndpoints.cs`**

```csharp
using Inkshelf.Convert;

namespace Inkshelf.Endpoints;

public static class ConvertEndpoints
{
    public static void MapConvertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm, string? status,
            string? @return, HttpContext httpContext, ConvertService convert, CancellationToken ct) =>
        {
            var (maxW, maxH, dpr) = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"]);

            // JS poll: report status, no enqueue.
            if (status is "1")
            {
                var s = await convert.StatusAsync(id, maxW, maxH, dpr, ct);
                return s.Status == ConvertStatus.None
                    ? Results.NotFound()
                    : Results.Text(Text(s.Status));
            }

            var result = await convert.KickAsync(id, fresh is "1" or "true", maxW, maxH, dpr, ct);
            if (result.Status == ConvertStatus.None) return Results.NotFound();

            // JS kick (warm): return the status as text; 202 while not yet done.
            if (warm is "1")
                return result.Status == ConvertStatus.Done
                    ? Results.Text("done")
                    : Results.Text(Text(result.Status), statusCode: StatusCodes.Status202Accepted);

            // Plain navigation (no JS): download when ready, else back to the listing.
            return result.Status == ConvertStatus.Done
                ? Results.File(result.FilePath!, "application/epub+zip", fileDownloadName: result.DownloadName)
                : Results.Redirect(LocalReturn(@return));
        });
    }

    private static string Text(ConvertStatus s) => s.ToString().ToLowerInvariant();

    // Open-redirect guard: only same-site absolute paths are honored.
    private static string LocalReturn(string? r) =>
        !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
```

- [ ] **Step 5: Add endpoint tests to `EndpointTests.cs`**

Append these `[Fact]`s (they exercise the unauthenticated redirect + the local-return guard without a live ABS):

```csharp
    [Fact]
    public async Task Convert_status_without_session_redirects_to_login()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // No session → GetItemDetailAsync throws AbsAuthException → middleware → /login.
        var res = await client.GetAsync("/convert/abc?status=1");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }
```

Note: deeper endpoint behavior (202 on warm, download on done, 302 to a local return) needs a stubbed authenticated ABS and is covered at the unit level by `ConvertServiceTests`. If a fuller integration test is wanted, inject a fake `AbsApiClient` via `WithWebHostBuilder(b => b.ConfigureServices(...))` — optional, not required for green.

- [ ] **Step 6: Run the affected tests**

Run: `dotnet test --filter "ConvertServiceTests|EndpointTests"`
Expected: PASS.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS (all). If the old `ConvertOutcome`/`ConvertAsync` is referenced anywhere else, the compiler will flag it — there should be no remaining references outside the endpoint (now rewritten).

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Convert/ConvertService.cs src/Inkshelf/Endpoints/ConvertEndpoints.cs tests/Inkshelf.Tests/ConvertServiceTests.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: reshape convert into a background-kick + status endpoint"
```

---

## Task 6: Client — poll, listing states, meta-refresh (real-device test)

**Files:**
- Modify: `src/Inkshelf/Pages/Support/ItemRowModel.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`
- Modify: `src/Inkshelf/Pages/Library.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml`

**Interfaces:**
- Consumes: `ConvertQueue.Status`, `ScreenTarget.FromCookie`, `EpubCache.PathFor` (existing).
- Produces: `ItemRowModel.State` (`ConvertRowState`), `ItemRowModel.ReturnUrl`; `LibraryModel.AnyConverting`.

- [ ] **Step 1: Update `ItemRowModel.cs`**

```csharp
using Inkshelf.Abs;

namespace Inkshelf.Pages;

// Per-row convert state for the listing. NotConvertible = not a cbz/cbr (or no
// size/mtime to key the cache). Convert = convertible, nothing cached/pending.
public enum ConvertRowState { NotConvertible, Convert, Converting, Failed, Cached }

// One item row. Links is the shared URL builder for the current library/facet.
// State drives the convert action; ReturnUrl is the listing URL a no-JS convert
// navigation returns to.
public record ItemRowModel(
    AbsItem Item,
    LibraryLinks Links,
    IReadOnlyList<AbsRef>? Authors = null,
    IReadOnlyList<AbsSeriesRef>? Series = null,
    ConvertRowState State = ConvertRowState.NotConvertible,
    string ReturnUrl = "/");
```

- [ ] **Step 2: Update `Library.cshtml.cs`** — inject `ConvertQueue`, precompute states + `AnyConverting`

Change the constructor and add the state map. Replace the constructor and `IsCached`/`RowFor`:

```csharp
    private readonly AbsApiClient _api;
    private readonly EpubCache _cache;
    private readonly ConvertQueue _queue;
    public LibraryModel(AbsApiClient api, EpubCache cache, ConvertQueue queue)
    { _api = api; _cache = cache; _queue = queue; }
```

Add fields + compute after items are fetched (inside `OnGetAsync`, right after `_structured = await FetchStructuredAsync(...)`):

```csharp
        ComputeConvertStates();
```

Add near the bottom of the class:

```csharp
    // Per-row convert state, precomputed so the head (which renders before the
    // rows) can decide whether to emit the no-JS <noscript> meta-refresh.
    public bool AnyConverting { get; private set; }
    private readonly Dictionary<string, ConvertRowState> _states = new();

    private void ComputeConvertStates()
    {
        var (w, h, _) = ScreenTarget.FromCookie(Request.Cookies["scr"]);
        foreach (var item in Items)
        {
            _structured.TryGetValue(item.Id, out var media);
            var state = RowState(item, media, w, h);
            _states[item.Id] = state;
            if (state == ConvertRowState.Converting) AnyConverting = true;
        }
    }

    private ConvertRowState RowState(AbsItem item, AbsBatchMedia? media, int w, int h)
    {
        var fmt = item.Media?.EbookFormat;
        if (fmt != "cbz" && fmt != "cbr") return ConvertRowState.NotConvertible;
        var efm = media?.EbookFile?.Metadata;
        if (efm is null) return ConvertRowState.NotConvertible; // can't key the cache
        var path = _cache.PathFor(item.Id, efm.Size, efm.MtimeMs, w, h);
        return _queue.Status(path) switch
        {
            ConvertStatus.Done => ConvertRowState.Cached,
            ConvertStatus.Queued or ConvertStatus.Running => ConvertRowState.Converting,
            ConvertStatus.Failed => ConvertRowState.Failed,
            _ => ConvertRowState.Convert,
        };
    }
```

Update `RowFor` to pass state + return URL:

```csharp
    public ItemRowModel RowFor(AbsItem item)
    {
        _structured.TryGetValue(item.Id, out var media);
        var state = _states.TryGetValue(item.Id, out var s) ? s : ConvertRowState.NotConvertible;
        var ret = Request.Path + Request.QueryString; // exact current listing URL
        return new ItemRowModel(item, Links, media?.Metadata?.Authors, media?.Metadata?.Series, state, ret);
    }
```

(Add `using Inkshelf.Convert;` at the top if not present.)

**Search rows:** the search branch (`IsSearch`) returns before `ComputeConvertStates`, so `_states` is empty and search rows get `ConvertRowState.NotConvertible`. To keep search convert working (it did before via the endpoint), set a minimal state in the search branch: after `SearchResults = ...`, the row partial for search should still show a plain `Convert`. Simplest: in `RowFor`, when `_states` has no entry but the item is a cbz/cbr, fall back to `Convert`:

```csharp
        if (state == ConvertRowState.NotConvertible)
        {
            var f = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat;
            if (f is "cbz" or "cbr") state = ConvertRowState.Convert;
        }
```

- [ ] **Step 3: Update `_ItemRow.cshtml`** — render the four states

Replace the `@if (fmt == "cbz" || fmt == "cbr")` convert block with a `switch` on `Model.State`. The convert/retry/converting anchors carry `data-warm` (+ `data-poll` while converting) and the escaped `return`:

```razor
    @{ var fmt = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat; }
    @if (!string.IsNullOrEmpty(fmt))
    {
        <div class="actions">
            <a href="/download/@item.Id">Download</a>
            @if (Model.State != ConvertRowState.NotConvertible)
            {
                var ret = Uri.EscapeDataString(Model.ReturnUrl);
                <span class="convert">
                    @switch (Model.State)
                    {
                        case ConvertRowState.Cached:
                            <a href="/convert/@item.Id" title="Already converted — downloads right away">EPUB &#10003;</a>
                            break;
                        case ConvertRowState.Converting:
                            <a href="/convert/@item.Id?return=@ret" data-warm data-poll>Converting&#8230;</a>
                            break;
                        case ConvertRowState.Failed:
                            <a href="/convert/@item.Id?return=@ret" data-warm>Convert (retry)</a>
                            break;
                        default:
                            <a href="/convert/@item.Id?return=@ret" data-warm>Convert</a>
                            break;
                    }
                    <a class="regen" href="/convert/@item.Id?fresh=1&return=@ret" title="Regenerate">&#8635;</a>
                </span>
            }
        </div>
    }
```

(Add `@using Inkshelf.Pages` is unnecessary — same namespace; `ConvertRowState` is in `Inkshelf.Pages`.)

- [ ] **Step 4: Update `Library.cshtml`** — emit `<noscript>` meta-refresh + no-store

At the very top of `Library.cshtml` (page `@{ }` block), set the response header and a flag for the layout. Since the meta tag must land in `<head>` (rendered by `_Layout`), pass it via `ViewData` and have the layout emit it.

In `Library.cshtml` `@{ }` block:

```razor
@{
    Response.Headers["Cache-Control"] = "no-store";
    if (Model.AnyConverting) { ViewData["MetaRefresh"] = 30; }
}
```

In `_Layout.cshtml` `<head>`, after the `<title>`:

```razor
    @if (ViewData["MetaRefresh"] is int secs)
    {
        <noscript><meta http-equiv="refresh" content="@secs" /></noscript>
    }
```

- [ ] **Step 5: Update the `_Layout.cshtml` convert script** — kick → poll, resume-on-load

Replace the second inline script (the convert-warm one) with:

```javascript
    /* Background convert: tap kicks (?warm=1) then polls (?status=1) every 5s,
       updating the link text in place — the conversion runs server-side even if
       this page goes away. Links the server marked data-poll are already in
       flight, so we resume polling them on load. No-JS falls back to the
       <noscript> meta-refresh. ES5 for old e-readers. */
    (function () {
        try {
            var POLL_MS = 5000;
            function poll(a) {
                var xhr = new XMLHttpRequest();
                xhr.open('GET', a.href + (a.href.indexOf('?') < 0 ? '?' : '&') + 'status=1');
                xhr.onreadystatechange = function () {
                    if (xhr.readyState !== 4) { return; }
                    var s = (xhr.responseText || '').replace(/^\s+|\s+$/g, '');
                    if (xhr.status === 200 && s === 'done') {
                        a.firstChild.nodeValue = 'EPUB ↓';
                        a.setAttribute('data-ready', '1');
                    } else if (xhr.status === 200 && s === 'failed') {
                        a.firstChild.nodeValue = 'Convert (retry)';
                    } else {
                        setTimeout(function () { poll(a); }, POLL_MS);
                    }
                };
                xhr.send();
            }
            function kick(a) {
                a.firstChild.nodeValue = 'Converting…';
                var xhr = new XMLHttpRequest();
                xhr.open('GET', a.href + (a.href.indexOf('?') < 0 ? '?' : '&') + 'warm=1');
                xhr.onreadystatechange = function () {
                    if (xhr.readyState !== 4) { return; }
                    if (xhr.status === 200 && (xhr.responseText || '').indexOf('done') >= 0) {
                        a.firstChild.nodeValue = 'EPUB ↓';
                        a.setAttribute('data-ready', '1');
                    } else if (xhr.status === 200 || xhr.status === 202) {
                        setTimeout(function () { poll(a); }, POLL_MS);
                    } else {
                        a.firstChild.nodeValue = 'Convert (retry)';
                    }
                };
                xhr.send();
            }
            var links = document.querySelectorAll('a[data-warm]');
            for (var i = 0; i < links.length; i++) {
                (function (a) {
                    a.addEventListener('click', function (e) {
                        if (a.getAttribute('data-ready') === '1') { return; } // let it download
                        e.preventDefault();
                        kick(a);
                    });
                    if (a.getAttribute('data-poll') === '1' || a.hasAttribute('data-poll')) {
                        poll(a); // already converting server-side → resume
                    }
                })(links[i]);
            }
        } catch (e) {}
    })();
```

- [ ] **Step 6: Build + full test suite**

Run: `dotnet build src/Inkshelf/Inkshelf.csproj && dotnet test`
Expected: build succeeds; all tests pass. (The `LibraryModel` constructor change is satisfied by DI — `ConvertQueue` is registered singleton in Task 4.)

- [ ] **Step 7: Manual smoke on desktop (JS path)**

Run the app (`/run` skill or `dotnet run --project src/Inkshelf`), log in, open a library with a CBZ/CBR item. Tap **Convert** → text shows `Converting…`, then flips to `EPUB ↓` within ~5s of the server finishing; second tap downloads. Reload mid-convert → row shows `Converting…` and resumes polling.

- [ ] **Step 8: REAL E-INK DEVICE TEST (required before merge)**

On the actual e-ink reader: (a) JS path — Convert flips to `EPUB ↓` and downloads; (b) disable JS (or confirm the engine ignores it) — tapping Convert returns to the listing, which reloads every 30s via `<noscript>` and flips to `EPUB ✓`; the download then works. Confirm no layout breakage (defensive CSS). **Do not merge without this.**

- [ ] **Step 9: Commit**

```bash
git add src/Inkshelf/Pages/
git commit -m "feat: poll convert status client-side, meta-refresh fallback"
```

---

## Task 7: Docs

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Update `ARCHITECTURE.md`**

- Under **Layout**, add `Convert/ConvertQueue.cs`, `Convert/ConvertWorker.cs`, `Abs/AbsDownloadClient.cs` with one-line descriptions.
- Under **Load-bearing conventions**, add a "Conversion runs in the background" entry: the request only kicks (detail + token capture + enqueue); `ConvertWorker` downloads+converts on the app lifetime so a disconnect can't cancel it; "done" is the on-disk `.epub`; the registry is in-memory (restart = re-tap).
- Extend the "two ABS clients" note to name the **third** (`AbsDownloadClient`): handler-free, caller-supplied bearer, no refresh, worker-only — and why (no `HttpContext`).
- Add `MaxConcurrentConversions` to the **Configuration** table (default 1).
- Note the `/convert/{id}` param surface (`warm`/`status`/`fresh`/`return`).

- [ ] **Step 2: Update `ROADMAP.md`**

- Remove/मtick **Background conversion** from Priority and *Convert UX / feedback*.
- Fold in **Listing freshness** (`Cache-Control: no-store` done) and **Regen (↻) feedback** (now rides the same status flow).
- Note the startup `.tmp` sweep closes the security-roadmap "assert no partial file" concern.

- [ ] **Step 3: Commit**

```bash
git add docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "docs: document background conversion and third ABS client"
```

---

## Final verification

- [ ] `dotnet test` green (full suite).
- [ ] Real e-ink device test done (Task 6 Step 8).
- [ ] `git log --oneline` shows the sequence of small commits.
- [ ] Open the PR (ask the owner first per `CLAUDE.md`); present the URL as a clickable link.
