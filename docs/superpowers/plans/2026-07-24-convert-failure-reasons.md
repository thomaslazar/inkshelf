# Conversion Failure Reasons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a comic conversion fails, show the user *why* (too large / download failed / bad archive / unexpected) on a plain-HTML page, in their language, with Retry and Back.

**Architecture:** The in-memory `ConvertQueue` stores a reason + optional byte count on each Failed entry (same 10-min TTL). `ConvertWorker` categorizes failures by stage and fails oversized archives *before* downloading. A new server-rendered Razor page `/convert/{id}/why` reads the reason via `ConvertService` and renders a localized explanation. The Failed row gains a "why?" link; the layout's status-poll JS auto-navigates there on failure. The uicheck harness seeds an oversized comic and runs Inkshelf with a tiny archive ceiling to exercise the whole path.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 10), xUnit, Playwright (.NET) for uicheck, file-backed JSON localisation catalog.

## Global Constraints

- **No client JS unless unavoidable.** The reason page must work with zero JS; the poll-JS auto-nav is progressive enhancement only.
- **Plain HTML: `<form>` and `<a>` only.** No new dependencies.
- **All new user-facing strings go through `@L["English source string"]`** and get a matching key in `src/Inkshelf/locales/de.json`. English source string *is* the lookup key; there is no `en.json`.
- **Conventional Commits**, imperative lowercase subject, no `Co-Authored-By`/generated-by lines. Ask before committing is waived per-task here (commit steps are part of the plan), but do NOT push or open a PR without asking.
- **Never edit `CHANGELOG.md`** (release skill owns it). Shipped work is recorded in `docs/ROADMAP.md` Done + `docs/ARCHITECTURE.md` only.
- Do all .NET work inside the devcontainer. Build/test: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj`.
- The shipped default `MaxArchiveBytes` (1 GiB) must stay unchanged; only the uicheck run overrides it via env.

---

### Task 1: Store the failure reason on the queue entry

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertJob.cs` (add enum + failure struct)
- Modify: `src/Inkshelf/Convert/ConvertQueue.cs`
- Test: `tests/Inkshelf.Tests/ConvertQueueTests.cs`

**Interfaces:**
- Produces:
  - `enum ConvertFailReason { TooLarge, DownloadFailed, BadArchive, ConvertError }`
  - `readonly record struct ConvertFailure(ConvertFailReason Reason, long? ArchiveBytes)`
  - `ConvertQueue.MarkFailed(string cachePath, ConvertFailReason reason = ConvertFailReason.ConvertError, long? archiveBytes = null)`
  - `ConvertQueue.ConvertFailure? FailureFor(string cachePath)` — the reason while the entry is Failed (respecting TTL), else null.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ConvertQueueTests.cs`:

```csharp
[Fact]
public void FailureFor_returns_reason_and_bytes_while_failed()
{
    var q = new ConvertQueue();
    var path = TempPath();
    q.Enqueue(Job(path));
    q.Reader.TryRead(out _);
    q.MarkFailed(path, ConvertFailReason.TooLarge, 1_500_000_000);
    var f = q.FailureFor(path);
    Assert.NotNull(f);
    Assert.Equal(ConvertFailReason.TooLarge, f!.Value.Reason);
    Assert.Equal(1_500_000_000, f.Value.ArchiveBytes);
}

[Fact]
public void FailureFor_is_null_when_not_failed()
{
    var q = new ConvertQueue();
    var path = TempPath();
    q.Enqueue(Job(path));            // Queued, not Failed
    Assert.Null(q.FailureFor(path));
    Assert.Null(q.FailureFor(TempPath())); // unknown path
}

[Fact]
public void FailureFor_is_null_after_ttl()
{
    var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var q = new ConvertQueue(() => now);
    var path = TempPath();
    q.Enqueue(Job(path));
    q.Reader.TryRead(out _);
    q.MarkFailed(path, ConvertFailReason.BadArchive);
    now = now.AddMinutes(11);
    Assert.Null(q.FailureFor(path));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj --filter FullyQualifiedName~ConvertQueueTests`
Expected: FAIL — `FailureFor` / `ConvertFailReason` / `ConvertFailure` do not exist (compile error).

- [ ] **Step 3: Add the enum and failure struct**

In `src/Inkshelf/Convert/ConvertJob.cs`, below the existing `ConvertStatus` enum, add:

```csharp
// Why a conversion failed. Stored transiently on the Failed queue entry (same
// 10-min TTL) so the user can be shown a reason; a re-tap reproduces a
// deterministic failure like TooLarge.
public enum ConvertFailReason { TooLarge, DownloadFailed, BadArchive, ConvertError }

// A failure snapshot from the queue. ArchiveBytes is the archive's byte size for
// TooLarge (so the page can say "1.3 GB, over the 1 GB limit"); null otherwise.
public readonly record struct ConvertFailure(ConvertFailReason Reason, long? ArchiveBytes);
```

- [ ] **Step 4: Store the reason on the entry and expose FailureFor**

In `src/Inkshelf/Convert/ConvertQueue.cs`:

Extend `Entry` (line 17):

```csharp
private sealed class Entry { public Phase Phase; public DateTime FailedAtUtc; public ConvertFailReason Reason; public long? ArchiveBytes; }
```

Replace `MarkFailed` (lines 62-65):

```csharp
public void MarkFailed(string cachePath, ConvertFailReason reason = ConvertFailReason.ConvertError, long? archiveBytes = null)
{
    lock (_gate) _entries[cachePath] = new Entry
    {
        Phase = Phase.Failed, FailedAtUtc = _clock(), Reason = reason, ArchiveBytes = archiveBytes,
    };
}

// The failure reason while the path is Failed (honours the TTL via Peek), else null.
public ConvertFailure? FailureFor(string cachePath)
{
    lock (_gate)
    {
        if (Peek(cachePath) != ConvertStatus.Failed) return null;
        var e = _entries[cachePath];
        return new ConvertFailure(e.Reason, e.ArchiveBytes);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj --filter FullyQualifiedName~ConvertQueueTests`
Expected: PASS (all ConvertQueueTests, old and new).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/ConvertJob.cs src/Inkshelf/Convert/ConvertQueue.cs tests/Inkshelf.Tests/ConvertQueueTests.cs
git commit -m "feat: store failure reason on the convert queue entry"
```

---

### Task 2: Carry archive size on the job and expose the failure via ConvertService

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertJob.cs` (add `ArchiveBytes` to the record)
- Modify: `src/Inkshelf/Convert/ConvertService.cs`
- Test: `tests/Inkshelf.Tests/ConvertServiceTests.cs`

**Interfaces:**
- Consumes: `ConvertQueue.FailureFor` (Task 1).
- Produces:
  - `ConvertJob(..., string? FileIno = null, long ArchiveBytes = 0)` — new trailing member, ABS-reported archive size.
  - `readonly record struct FailureView(string Title, ConvertFailReason Reason, long? ArchiveBytes)` in `ConvertService`.
  - `ConvertService.FailureAsync(string id, RenderTarget target, CancellationToken ct, string? fileIno = null) : Task<FailureView?>` — resolves the cache path, returns the queue failure enriched with the item title, or null if not resolvable / not currently Failed.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ConvertServiceTests.cs`:

```csharp
[Fact]
public async Task FailureAsync_returns_title_and_reason_when_failed()
{
    using var dir = new TempDir();
    var cache = new EpubCache(dir.Path);
    var queue = new ConvertQueue();
    var api = DetailClient(DetailJson("cbz", "Big Comic", "Mika Manga", 300_000, 42));
    var svc = Service(api, cache, queue, TokenStoreWith("tok"));
    var target = new RenderTarget(0, 0, 1.0, false);

    // Mark the exact path the service will resolve as Failed.
    var path = cache.PathFor("item1", 300_000, 42, target.MaxW, target.MaxH, target.Grayscale);
    queue.MarkFailed(path, ConvertFailReason.TooLarge, 300_000);

    var f = await svc.FailureAsync("item1", target, default);
    Assert.NotNull(f);
    Assert.Equal("Big Comic", f!.Value.Title);
    Assert.Equal(ConvertFailReason.TooLarge, f.Value.Reason);
    Assert.Equal(300_000, f.Value.ArchiveBytes);
}

[Fact]
public async Task FailureAsync_is_null_when_not_failed()
{
    using var dir = new TempDir();
    var cache = new EpubCache(dir.Path);
    var queue = new ConvertQueue();
    var api = DetailClient(DetailJson("cbz", "Big Comic", "Mika Manga", 300_000, 42));
    var svc = Service(api, cache, queue, TokenStoreWith("tok"));
    Assert.Null(await svc.FailureAsync("item1", new RenderTarget(0, 0, 1.0, false), default));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj --filter FullyQualifiedName~ConvertServiceTests`
Expected: FAIL — `FailureAsync` / `FailureView` do not exist (compile error).

- [ ] **Step 3: Add ArchiveBytes to ConvertJob**

In `src/Inkshelf/Convert/ConvertJob.cs`, change the record (currently ends `string? FileIno = null);`) to:

```csharp
public sealed record ConvertJob(
    string ItemId, string AccessToken, string CachePath,
    EbookMeta Meta, RenderTarget Target, string? FileIno = null, long ArchiveBytes = 0);
```

- [ ] **Step 4: Pass the size when enqueuing and add FailureAsync**

In `src/Inkshelf/Convert/ConvertService.cs`:

The size is already computed in `ResolveAsync`, but only `path`/`meta`/`downloadName` are returned. Widen the tuple to include size. Change the return type of `ResolveAsync` (line 60) and its two return sites so it also yields `size`:

```csharp
private async Task<(string Path, EbookMeta Meta, string DownloadName, long Size)?> ResolveAsync(
    string id, RenderTarget target, string? fileIno, CancellationToken ct)
```

At the end of `ResolveAsync` (line 95), return `(path, meta, downloadName, size)`.

Update `KickAsync` (line 34) destructuring to `var (path, meta, downloadName, size) = r.Value;` and the enqueue (line 41) to pass the size:

```csharp
var status = _queue.Enqueue(new ConvertJob(id, tokens.Access, path, meta, target, fileIno, size));
```

Update `StatusAsync` (line 51) destructuring to `var (path, _, downloadName, _) = r.Value;`.

Add the failure struct and method (place `FailureView` near `KickResult` at the top of the file, and the method after `StatusAsync`):

```csharp
public readonly record struct FailureView(string Title, ConvertFailReason Reason, long? ArchiveBytes);

// The current failure for this item's per-device conversion, enriched with the
// item title for display. null when the item can't be resolved or isn't Failed.
public async Task<FailureView?> FailureAsync(string id, RenderTarget target,
    CancellationToken ct, string? fileIno = null)
{
    var r = await ResolveAsync(id, target, fileIno, ct);
    if (r is null) return null;
    var (path, meta, _, _) = r.Value;
    var f = _queue.FailureFor(path);
    return f is null ? null : new FailureView(meta.Title, f.Value.Reason, f.Value.ArchiveBytes);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj --filter FullyQualifiedName~ConvertServiceTests`
Expected: PASS (all ConvertServiceTests, old and new).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/ConvertJob.cs src/Inkshelf/Convert/ConvertService.cs tests/Inkshelf.Tests/ConvertServiceTests.cs
git commit -m "feat: carry archive size and expose failure via ConvertService"
```

---

### Task 3: Categorize failures in the worker + fail oversized archives before download

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs`
- Test: `tests/Inkshelf.Tests/ConvertWorkerTests.cs`

**Interfaces:**
- Consumes: `ConvertQueue.MarkFailed(path, reason, archiveBytes)`, `ConvertQueue.FailureFor` (Task 1); `ConvertJob.ArchiveBytes` (Task 2).
- Produces: no new public surface — behaviour only. Existing `MarkFailed(path)` (no reason) calls are replaced by reasoned ones.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ConvertWorkerTests.cs`. Add a helper that returns garbage bytes (a non-archive) near `Cbz()`:

```csharp
private static byte[] Garbage() => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

// A download stub that always throws — proves the pre-download size check runs
// BEFORE any download attempt.
private static IServiceScopeFactory ScopeFactoryDownloadThrows()
{
    var services = new ServiceCollection();
    services.AddSingleton(new AbsDownloadClient(
        new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)))
        { BaseAddress = new Uri("http://abs.local") }));
    return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
}
```

Add a job builder that sets the reported size, and the tests:

```csharp
private static ConvertJob JobSized(string path, long bytes) =>
    new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"),
        new RenderTarget(0, 0, 1.0, false), null, bytes);

[Fact]
public async Task An_oversized_reported_archive_fails_TooLarge_before_downloading()
{
    using var dir = new TempDir();
    var cache = new EpubCache(dir.Path);
    var queue = new ConvertQueue();
    var path = cache.PathFor("item1", 1, 2, 0, 0);
    queue.Enqueue(JobSized(path, 500)); // reported 500 bytes

    // Ceiling 8 < 500 → pre-check trips. Download stub throws if ever called:
    // reaching it would yield DownloadFailed, so TooLarge proves the order.
    var worker = Worker(queue, ScopeFactoryDownloadThrows(), cache, maxArchiveBytes: 8);
    await worker.StartAsync(default);
    await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
    await worker.StopAsync(default);

    Assert.Equal(ConvertFailReason.TooLarge, queue.FailureFor(path)!.Value.Reason);
    Assert.False(File.Exists(path));
}

[Fact]
public async Task An_over_ceiling_download_fails_TooLarge_via_copy_guard()
{
    using var dir = new TempDir();
    var cache = new EpubCache(dir.Path);
    var queue = new ConvertQueue();
    var path = cache.PathFor("item1", 1, 2, 0, 0);
    queue.Enqueue(Job(path)); // ArchiveBytes 0 → pre-check skipped, copy guard trips

    var worker = Worker(queue, ScopeFactoryReturning(Cbz()), cache, maxArchiveBytes: 8);
    await worker.StartAsync(default);
    await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
    await worker.StopAsync(default);

    Assert.Equal(ConvertFailReason.TooLarge, queue.FailureFor(path)!.Value.Reason);
}

[Fact]
public async Task A_download_failure_is_categorized_DownloadFailed()
{
    using var dir = new TempDir();
    var cache = new EpubCache(dir.Path);
    var queue = new ConvertQueue();
    var path = cache.PathFor("item1", 1, 2, 0, 0);
    queue.Enqueue(Job(path));

    var worker = Worker(queue, ScopeFactoryDownloadThrows(), cache);
    await worker.StartAsync(default);
    await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
    await worker.StopAsync(default);

    Assert.Equal(ConvertFailReason.DownloadFailed, queue.FailureFor(path)!.Value.Reason);
}

[Fact]
public async Task A_non_archive_download_is_categorized_BadArchive()
{
    using var dir = new TempDir();
    var cache = new EpubCache(dir.Path);
    var queue = new ConvertQueue();
    var path = cache.PathFor("item1", 1, 2, 0, 0);
    queue.Enqueue(Job(path));

    // Downloads successfully, but the bytes aren't a valid archive → convert stage
    // throws from ArchiveFactory → BadArchive.
    var worker = Worker(queue, ScopeFactoryReturning(Garbage()), cache);
    await worker.StartAsync(default);
    await WaitUntil(() => queue.Status(path) == ConvertStatus.Failed, TimeSpan.FromSeconds(10));
    await worker.StopAsync(default);

    Assert.Equal(ConvertFailReason.BadArchive, queue.FailureFor(path)!.Value.Reason);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj --filter FullyQualifiedName~ConvertWorkerTests`
Expected: FAIL — reasons are all `ConvertError` (the default) instead of the expected categories; the pre-check test may also fail with `DownloadFailed`.

- [ ] **Step 3: Implement the pre-check, reasoned copy-guard, and stage classification**

In `src/Inkshelf/Convert/ConvertWorker.cs`, edit `ProcessAsync`.

After `if (File.Exists(job.CachePath)) { _queue.MarkDone(job.CachePath); return; }` inside the lock (line 61), add the pre-download check:

```csharp
if (_options.MaxArchiveBytes > 0 && job.ArchiveBytes > _options.MaxArchiveBytes)
{
    _logger.LogWarning("Archive for {Id} \"{Title}\" is {Size} bytes, over {Limit} — refusing before download.",
        job.ItemId, job.Meta.Title, job.ArchiveBytes, _options.MaxArchiveBytes);
    _queue.MarkFailed(job.CachePath, ConvertFailReason.TooLarge, job.ArchiveBytes);
    return;
}
```

Declare a stage flag just inside the `try` (before the pre-check block, at the top of `try` on line 54):

```csharp
var downloaded = false;
```

In the copy-guard branch (lines 73-77), change the log + mark:

```csharp
if (!await CopyWithLimitAsync(archive, spool, _options.MaxArchiveBytes, ct))
{
    _logger.LogWarning("Archive for {Id} \"{Title}\" exceeds {Limit} bytes — refusing.",
        job.ItemId, job.Meta.Title, _options.MaxArchiveBytes);
    _queue.MarkFailed(job.CachePath, ConvertFailReason.TooLarge, job.ArchiveBytes);
    return;
}
```

Immediately after the download/spool `await using` block closes (after line 78, before the cover fetch), mark the download stage complete:

```csharp
downloaded = true;
```

Replace the generic catch (lines 96-100):

```csharp
catch (Exception ex)
{
    // ponytail: stage flag + type sniff, not an exhaustive exception taxonomy.
    // Pre-convert failures are download; convert-stage archive-format errors
    // (SharpCompress / bad zip) are BadArchive; anything else is ConvertError.
    var reason = !downloaded
        ? ConvertFailReason.DownloadFailed
        : (ex is InvalidDataException || (ex.GetType().Namespace?.StartsWith("SharpCompress") ?? false))
            ? ConvertFailReason.BadArchive
            : ConvertFailReason.ConvertError;
    _logger.LogWarning(ex, "Conversion failed for {Id} \"{Title}\" ({Reason})", job.ItemId, job.Meta.Title, reason);
    _queue.MarkFailed(job.CachePath, reason, job.ArchiveBytes);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj --filter FullyQualifiedName~ConvertWorkerTests`
Expected: PASS (all ConvertWorkerTests — the existing `A_download_failure_marks_Failed_not_Running` and `An_over_ceiling_archive_marks_Failed_and_writes_no_file` still pass since `Status` is unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Convert/ConvertWorker.cs tests/Inkshelf.Tests/ConvertWorkerTests.cs
git commit -m "feat: categorize conversion failures and reject oversized archives early"
```

---

### Task 4: The reason page (`/convert/{id}/why`) + localized strings

**Files:**
- Create: `src/Inkshelf/Pages/ConvertWhy.cshtml`
- Create: `src/Inkshelf/Pages/ConvertWhy.cshtml.cs`
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs` (make the return-URL guard reusable)
- Modify: `src/Inkshelf/locales/de.json`

**Interfaces:**
- Consumes: `ConvertService.FailureAsync` / `FailureView` (Task 2); `ScreenTarget.FromCookie`, `DeviceSettings.Read`, `AbsOptions.MaxArchiveBytes`.
- Produces: a GET page at `/convert/{id}/why?file={ino}&return={url}`; `ConvertEndpoints.LocalReturn` becomes `internal static`.

- [ ] **Step 1: Make the return-URL guard reusable**

In `src/Inkshelf/Endpoints/ConvertEndpoints.cs`, change `private static string LocalReturn` (line 39) to `internal static string LocalReturn` so the page can reuse the same open-redirect guard. No behaviour change.

- [ ] **Step 2: Write the page model**

Create `src/Inkshelf/Pages/ConvertWhy.cshtml.cs`:

```csharp
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Inkshelf.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

// Plain-HTML explanation for a failed conversion. Reached by the poll-JS auto-nav
// on failure, or the "why?" link on a Failed row (JS and no-JS alike). If the
// item isn't currently Failed (expired TTL, re-queued, converted), redirect back
// so the page never shows stale state.
public class ConvertWhyModel : PageModel
{
    private readonly ConvertService _convert;
    private readonly AbsOptions _options;
    public ConvertWhyModel(ConvertService convert, AbsOptions options)
    { _convert = convert; _options = options; }

    [FromRoute] public string Id { get; set; } = "";
    [FromQuery] public string? File { get; set; }
    [FromQuery(Name = "return")] public string? Return { get; set; }

    public string Title { get; private set; } = "";
    public ConvertFailReason Reason { get; private set; }
    public long? ArchiveBytes { get; private set; }
    public long LimitBytes { get; private set; }
    public string BackUrl { get; private set; } = "/";
    public string RetryUrl { get; private set; } = "/";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        var ds = DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], ds.Retina, ds.Grayscale);

        var f = await _convert.FailureAsync(Id, target, ct, File);
        BackUrl = ConvertEndpoints.LocalReturn(Return);
        if (f is null) return Redirect(BackUrl); // not (any longer) Failed → nothing to explain

        Title = f.Value.Title;
        Reason = f.Value.Reason;
        ArchiveBytes = f.Value.ArchiveBytes;
        LimitBytes = _options.MaxArchiveBytes;

        var fileQ = string.IsNullOrEmpty(File) ? "" : $"file={Uri.EscapeDataString(File)}&";
        RetryUrl = $"/convert/{Id}?{fileQ}return={Uri.EscapeDataString(BackUrl)}";
        return Page();
    }

    // Binary units, one decimal (e.g. "1.3 GiB", "293.0 KiB"). Static so the view
    // formats both the actual size and the limit identically.
    public static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double n = bytes; var u = 0;
        while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return u == 0 ? $"{bytes} {units[u]}" : $"{n:0.0} {units[u]}";
    }
}
```

- [ ] **Step 3: Write the view**

Create `src/Inkshelf/Pages/ConvertWhy.cshtml`:

```cshtml
@page "/convert/{id}/why"
@model Inkshelf.Pages.ConvertWhyModel
@using Inkshelf.Convert
@{
    ViewData["Title"] = L["Conversion failed"];
    var message = Model.Reason switch
    {
        ConvertFailReason.TooLarge => Model.ArchiveBytes is { } b
            ? L["The archive is {0}, over the {1} limit.",
                Inkshelf.Pages.ConvertWhyModel.HumanBytes(b),
                Inkshelf.Pages.ConvertWhyModel.HumanBytes(Model.LimitBytes)]
            : L["The comic archive is too large to convert."],
        ConvertFailReason.DownloadFailed => L["The download from Audiobookshelf failed. Try again."],
        ConvertFailReason.BadArchive => L["The comic archive could not be read (it may be corrupt or an unsupported format)."],
        _ => L["The conversion failed unexpectedly. Try again."],
    };
}
<h1>@L["Conversion failed"]</h1>
<p class="item-title">@Model.Title</p>
<p>@message</p>
<p class="actions">
    <a href="@Model.RetryUrl">@L["Retry"]</a>
    <a href="@Model.BackUrl">@L["Back"]</a>
</p>
```

- [ ] **Step 4: Add the German strings**

In `src/Inkshelf/locales/de.json`, add these keys (before the closing brace; keep valid JSON — add a comma to the current last line):

```json
  "Conversion failed": "Konvertierung fehlgeschlagen",
  "The archive is {0}, over the {1} limit.": "Das Archiv ist {0} groß und überschreitet das Limit von {1}.",
  "The comic archive is too large to convert.": "Das Comic-Archiv ist zu groß zum Konvertieren.",
  "The download from Audiobookshelf failed. Try again.": "Der Download von Audiobookshelf ist fehlgeschlagen. Bitte erneut versuchen.",
  "The comic archive could not be read (it may be corrupt or an unsupported format).": "Das Comic-Archiv konnte nicht gelesen werden (möglicherweise beschädigt oder ein nicht unterstütztes Format).",
  "The conversion failed unexpectedly. Try again.": "Die Konvertierung ist unerwartet fehlgeschlagen. Bitte erneut versuchen.",
  "Retry": "Erneut versuchen",
  "Back": "Zurück",
  "why?": "warum?"
```

- [ ] **Step 5: Build and manually verify the page resolves**

Run: `dotnet build src/Inkshelf/Inkshelf.csproj`
Expected: build succeeds. (Runtime behaviour is covered end-to-end by uicheck in Task 6.)

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Pages/ConvertWhy.cshtml src/Inkshelf/Pages/ConvertWhy.cshtml.cs src/Inkshelf/Endpoints/ConvertEndpoints.cs src/Inkshelf/locales/de.json
git commit -m "feat: add conversion failure reason page"
```

---

### Task 5: "why?" link on Failed rows + poll-JS auto-navigation

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml`

**Interfaces:**
- Consumes: the `/convert/{id}/why` page (Task 4); `ConvertActionModel` (`Id`, `FileIno`, `ReturnUrl`, `State`).
- Produces: a `data-why` attribute on the convert link and a visible "why?" link in the Failed case.

- [ ] **Step 1: Add the why URL + link to the partial**

In `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`, after the existing `freshHref` line (line 6), add:

```cshtml
    var whyHref = $"/convert/{Model.Id}/why?{fileQ}return={ret}";
```

Change the `data-warm` links to also carry `data-why="@whyHref"` so the poll JS knows where to send the user on failure. Update the four `<a>` cases (Converting, Failed, default) that carry `data-warm` — e.g. the Failed case becomes:

```cshtml
        case ConvertRowState.Failed:
            <a href="@baseHref" data-warm data-why="@whyHref">@L["Convert (retry)"]</a>
            <a class="why" href="@whyHref">@L["why?"]</a>
            break;
```

Add `data-why="@whyHref"` to the Converting case (`<a href="@baseHref" data-warm data-poll data-why="@whyHref">`) and the default case (`<a href="@baseHref" data-warm data-why="@whyHref">`). The `Cached` case has no `data-warm` and needs no `data-why`.

- [ ] **Step 2: Auto-navigate on failure in the poll JS**

In `src/Inkshelf/Pages/Shared/_Layout.cshtml`, in the `poll(a)` function's terminal `else` branch (lines 59-62), replace:

```javascript
                    } else {
                        // failed, 404/None, or any error → terminal; revert to a tappable link.
                        a.firstChild.nodeValue = (s === 'failed') ? I18N.retry : I18N.convert;
                    }
```

with:

```javascript
                    } else {
                        // failed → send the watcher to the reason page; else revert to a tappable link.
                        var why = a.getAttribute('data-why');
                        if (s === 'failed' && why) { window.location.href = why; return; }
                        a.firstChild.nodeValue = (s === 'failed') ? I18N.retry : I18N.convert;
                    }
```

In the `kick(a)` function's terminal `else` branch (lines 77-79), replace:

```javascript
                    } else {
                        a.firstChild.nodeValue = I18N.retry;
                    }
```

with:

```javascript
                    } else {
                        var why = a.getAttribute('data-why');
                        if (why) { window.location.href = why; return; }
                        a.firstChild.nodeValue = I18N.retry;
                    }
```

- [ ] **Step 3: Build to verify the Razor compiles**

Run: `dotnet build src/Inkshelf/Inkshelf.csproj`
Expected: build succeeds. (Behaviour verified in Task 6.)

- [ ] **Step 4: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_ConvertAction.cshtml src/Inkshelf/Pages/Shared/_Layout.cshtml
git commit -m "feat: link failed rows to the reason page and auto-navigate on failure"
```

---

### Task 6: Exercise the failure path in the uicheck harness

**Files:**
- Modify: `docker/seed.sh` (seed an oversized comic)
- Modify: `tools/uicheck/run.sh` (tiny archive ceiling for the run)
- Modify: `tools/uicheck/Program.cs` (assert the reason page)

**Interfaces:**
- Consumes: the full feature (Tasks 1-5); the seeded ABS + Playwright flow already in `Program.cs`.
- Produces: a new authenticated check that clicks Convert on the oversized comic and asserts the German over-the-limit reason page.

- [ ] **Step 1: Seed an oversized comic fixture**

In `docker/seed.sh`, after the CBZ is created (`(cd "$TMP" && zip -jq sample.cbz page-01.png)`, ~line 78), add an oversized CBZ built from incompressible bytes so its stored size is comfortably over the uicheck ceiling:

```bash
# An oversized CBZ: one ~300 KiB incompressible page, stored (no compression),
# so ABS reports a file size well over the uicheck run's tiny archive ceiling.
# Exercises the TooLarge failure-reason path end to end.
python3 -c "import os;open('$TMP/big.jpg','wb').write(b'\xff\xd8'+os.urandom(300000))"
(cd "$TMP" && zip -j0q big.cbz big.jpg)
```

Add its upload after the existing cbz upload (near the `uploadf "Neon Blade Vol. 1" ...` block, ~line 148) and bump `EXPECT`:

```bash
uploadf "Big Comic Vol. 1" "Mika Manga" "Neon Blade" "$TMP/big.cbz"
```

The base `EXPECT=18` (line ~150) becomes `EXPECT=19`, and the cbr branch's `EXPECT=19` becomes `EXPECT=20`.

- [ ] **Step 2: Disambiguate the two CBZ items in the metadata PATCH**

In `docker/seed.sh`, the python PATCH block keys metadata by `ebookFormat`, so two CBZ items would both be renamed. Change the loop body so CBZ items are distinguished by size:

Replace the `for it in ...` loop body:

```python
for it in req('GET', '/api/libraries/%s/items?limit=200' % lib)['results']:
    media = it.get('media') or {}
    fmt = media.get('ebookFormat')
    if fmt == 'cbz':
        # Two CBZ items: the oversized one (larger file) is "Big Comic Vol. 1".
        size = ((media.get('ebookFile') or {}).get('metadata') or {}).get('size') or 0
        m = {'title': 'Big Comic Vol. 1', 'authors': [{'name': 'Mika Manga'}], 'series': [{'name': 'Neon Blade', 'sequence': '1'}]} \
            if size > 150000 else meta['cbz']
        req('PATCH', '/api/items/%s/media' % it['id'], {'metadata': m})
        print('  patched cbz (%d bytes) -> %s' % (size, m['title']))
    elif fmt in meta:
        body = {'metadata': meta[fmt]}
        if fmt in tags:
            body['tags'] = tags[fmt]
        req('PATCH', '/api/items/%s/media' % it['id'], body)
        print('  patched %s ebook -> %s' % (fmt, meta[fmt]['title']))
```

(Keep the `meta`/`tags` dicts above unchanged; the small CBZ still becomes "Neon Blade Vol. 1".)

- [ ] **Step 3: Run Inkshelf with a tiny archive ceiling during uicheck**

In `tools/uicheck/run.sh`, in the block that exports env before `dotnet run --project "$REPO/src/Inkshelf"` (the `export ABS_URL` / `export CachePath` / `export ASPNETCORE_URLS` lines), add:

```bash
# Tiny archive ceiling so the seeded oversized comic trips the TooLarge failure
# path while the small sample.cbz still converts. Shipped default (1 GiB) unchanged.
export MaxArchiveBytes=102400
```

- [ ] **Step 4: Assert the reason page in the Playwright flow**

In `tools/uicheck/Program.cs`, inside the authed `try` block, after the existing Convert-click assertions (after line 135, before the `Console.WriteLine("[authed] ...")`), add:

```csharp
        // Failure reason: the seeded "Big Comic" is over the run's tiny ceiling.
        // Clicking Convert must land on the German reason page (poll-JS auto-nav).
        await page.GotoAsync(libUrl);
        await page.FillAsync("input[name=q]", "Big Comic");
        await page.PressAsync("input[name=q]", "Enter");
        await page.ClickAsync("a[href^='/item/']:has-text('Big Comic')");
        await page.WaitForSelectorAsync("a[data-warm]", new() { Timeout = 15000 });
        await page.Locator("a[data-warm]").First.ClickAsync();
        // The poll flips to failed within a couple of cycles, then navigates to /why.
        await page.WaitForURLAsync("**/convert/**/why**", new() { Timeout = 20000 });
        await Shot("convert-failed-de");
        var whyBody = await page.InnerTextAsync("body");
        Expect("convert-failed-de", whyBody,
            "Konvertierung fehlgeschlagen", "überschreitet", "Erneut versuchen", "Zurück");
```

Update the final console line to mention the new check:

```csharp
        Console.WriteLine("[authed] index / library / item / converted / convert-click / convert-failed captured");
```

- [ ] **Step 5: Run the full UI check**

Run: `tools/uicheck/run.sh`
Expected: `PASS — screenshots in …, all assertions held.` and a `convert-failed-de.png` screenshot showing the German reason page with the size-over-limit sentence. If ABS was already seeded from a prior run without the oversized item, reseed: `docker compose -f docker/docker-compose.yml down -v && tools/uicheck/run.sh`.

- [ ] **Step 6: Commit**

```bash
git add docker/seed.sh tools/uicheck/run.sh tools/uicheck/Program.cs
git commit -m "test: cover the conversion failure reason path in uicheck"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/ROADMAP.md` (move item to Done)
- Modify: `docs/ARCHITECTURE.md` (present-tense design note)

**Interfaces:** none — docs only. Do NOT touch `CHANGELOG.md`.

- [ ] **Step 1: Move the roadmap item to Done**

In `docs/ROADMAP.md`, remove the "**Surface conversion failure reasons.**" bullet from the Conversion / rendering section and add a short Done entry:

```markdown
- **Conversion failure reasons** — a failed convert records a reason category
  (TooLarge / DownloadFailed / BadArchive / ConvertError) on its transient queue
  entry; oversized archives are rejected before download. The row's "why?" link
  (and the poll-JS auto-nav on failure) opens a plain-HTML `/convert/{id}/why`
  page explaining the failure — actionable for TooLarge ("archive is X, over the
  Y limit"). Failure log lines carry the item title. All strings localized.
```

- [ ] **Step 2: Add the ARCHITECTURE design note**

In `docs/ARCHITECTURE.md`, in the conversion section (near where `ConvertQueue`/`ConvertWorker` are described), add a present-tense sentence:

```markdown
A Failed queue entry also carries a `ConvertFailReason` (+ the archive size for
TooLarge), surfaced by the `/convert/{id}/why` page via `ConvertService.FailureAsync`.
The worker categorizes by stage (download vs convert) and rejects archives whose
ABS-reported size exceeds `MaxArchiveBytes` before downloading.
```

Match the surrounding heading style; do not add changelog/shipped-status wording.

- [ ] **Step 3: Commit**

```bash
git add docs/ROADMAP.md docs/ARCHITECTURE.md
git commit -m "docs: record conversion failure reasons"
```

---

## Self-Review

**Spec coverage:**
- Capture (reason category + detail on Failed entry, TTL) → Task 1. ✓
- Pre-download TooLarge check (archive size on job) → Tasks 2, 3. ✓
- Stage categorization (TooLarge/DownloadFailed/BadArchive/ConvertError) → Task 3. ✓
- Reason page (Razor, plain HTML, Retry + Back, redirect when not Failed, actionable TooLarge text) → Task 4. ✓
- "why?" link on Failed rows (listing + item detail via shared partial) + JS auto-nav → Task 5. ✓
- No-JS path (redirect-back listing + "why?" link) → Task 5 (link) + existing behaviour unchanged. ✓
- Logging (title + actual size) → Task 3. ✓
- Localisation (all strings + de.json) → Task 4. ✓
- uicheck (oversized fixture, tiny ceiling, assertion) → Task 6. ✓
- Unit tests (queue reason, worker categorization, service failure view) → Tasks 1-3. ✓
- Docs (ROADMAP Done, ARCHITECTURE) → Task 7. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code. ✓

**Type consistency:** `ConvertFailReason`, `ConvertFailure(Reason, ArchiveBytes)`, `FailureFor`, `MarkFailed(path, reason, archiveBytes)`, `ConvertJob(..., ArchiveBytes)`, `FailureView(Title, Reason, ArchiveBytes)`, `FailureAsync`, `LocalReturn`, `data-why` — used consistently across tasks. The four-tuple `ResolveAsync` return is updated at all three call sites (KickAsync, StatusAsync, FailureAsync). ✓
