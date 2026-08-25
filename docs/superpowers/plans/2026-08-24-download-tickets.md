# Download Tickets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every download link carry a short-lived server-side ticket, so an e-reader's download manager - which re-requests the URL without the browser's cookies - can complete the transfer.

**Architecture:** A singleton in-memory table maps a 22-character random handle to "one file this server will stream". Download links carry the handle as `?t=…`, minted when the page renders. The two file endpoints redeem it to serve bytes and nothing else; anything a ticket does not cover falls through to today's cookie path unchanged.

**Tech Stack:** ASP.NET Core Razor Pages + minimal-API endpoints, .NET 10, xUnit with `WebApplicationFactory<Program>`. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-08-24-download-tickets-design.md` (read it - it carries the hardware evidence and the rejected alternatives).

## Global Constraints

- Sliding idle window is exactly **15 minutes**, re-stamped by any request presenting the ticket.
- A ticket id is **16 random bytes, base64url** - 22 characters, `[A-Za-z0-9_-]`.
- The query parameter is named exactly **`t`**.
- **A ticket serves bytes only.** It never authorises `fresh=1`, `warm=1`, `status=1`, or a conversion kick.
- **A ticket is additive.** Missing, unknown or expired → fall through to the existing cookie path. No request that works today may start failing.
- `enableRangeProcessing: true` goes on the cached-EPUB file result **only**. No range work for raw ABS streams - that is explicitly out of scope (see the spec's Notes).
- **No new NuGet packages.** `TimeProvider` and `System.Buffers.Text.Base64Url` are in the framework.
- Inside `namespace Inkshelf`, the identifier `Convert` resolves to the **namespace** `Inkshelf.Convert`, not `System.Convert`. Never write bare `Convert.ToBase64String`.
- **Comments state rules and reasons, not narration.** This codebase's comments explain *why a thing must stay as it is*; a comment restating what the next line does will be rejected in review. Keep them short.
- **Do not touch `CHANGELOG.md`.** It is written only by the release process.
- `docs/ARCHITECTURE.md` is a map, not a changelog: it gets the one invariant this feature introduces, and nothing else. No per-feature entry.
- Run `dotnet format --verify-no-changes` before each commit - CI fails on formatting.

---

## File Structure

**Created:**
- `src/Inkshelf/DownloadTickets.cs` - the ticket table. Mint, redeem, expire. No HTTP, no ABS.
- `src/Inkshelf/Convert/EpubName.cs` - the converted-EPUB download filename, shared by the mint sites and `ConvertService`.
- `tests/Inkshelf.Tests/DownloadTicketsTests.cs` - unit tests for the table.
- `tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs` - endpoint tests for both ticket paths.

**Modified:**
- `src/Inkshelf/Program.cs` - register the singleton.
- `src/Inkshelf/RequestLog.cs` - redact the ticket from the logged query.
- `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs` - return the cache path it already computes.
- `src/Inkshelf/Abs/AbsDownloadClient.cs` - `DownloadEbookAsync` returns content type and length too.
- `src/Inkshelf/Convert/ConvertWorker.cs` - the one call site of the above.
- `src/Inkshelf/Convert/ConvertService.cs` - use `EpubName`.
- `src/Inkshelf/Auth/DeviceSettings.cs` - one shared `EnsureDid`.
- `src/Inkshelf/Endpoints/ConvertEndpoints.cs`, `DownloadEndpoints.cs` - redeem tickets.
- `src/Inkshelf/Pages/Library.cshtml.cs`, `Converted.cshtml.cs`, `Item.cshtml.cs` - mint tickets.
- `src/Inkshelf/Pages/Support/ItemRowModel.cs`, `ConvertActionModel.cs` - carry them.
- `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, `_ConvertAction.cshtml` - put them in hrefs.
- `tools/uicheck/Program.cs` - assert the links carry one.
- `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`.

---

### Task 1: The ticket table

**Files:**
- Create: `src/Inkshelf/DownloadTickets.cs`
- Create: `tests/Inkshelf.Tests/DownloadTicketsTests.cs`
- Modify: `src/Inkshelf/Program.cs` (next to the `DownloadMarks` registration, ~line 90)
- Modify: `src/Inkshelf/RequestLog.cs`
- Modify: `tests/Inkshelf.Tests/RequestLogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Inkshelf.DownloadTickets` (DI singleton)
  - `sealed record DownloadTickets.Ticket(string ItemId, string? FileIno, string Did, string DownloadName, string? FilePath = null, string? Access = null)`
  - `string MintEpub(string itemId, string? fileIno, string did, string downloadName, string filePath)`
  - `string MintRaw(string itemId, string? fileIno, string did, string downloadName, string access)`
  - `Ticket? Redeem(string? id)`
  - `internal static string? RequestLog.Redact(string? query)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/DownloadTicketsTests.cs`:

```csharp
namespace Inkshelf.Tests;

public class DownloadTicketsTests
{
    // TimeProvider is abstract with a virtual GetUtcNow, so a fake needs no package.
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private const string Did = "abc123def4560000";

    [Fact]
    public void An_epub_ticket_redeems_to_a_cache_path_and_no_bearer()
    {
        var tickets = new DownloadTickets();

        var id = tickets.MintEpub("item1", null, Did, "Author - Title.epub", "/cache/x.epub");
        var tk = tickets.Redeem(id);

        Assert.Equal(22, id.Length);                     // 16 random bytes, base64url
        Assert.Matches("^[A-Za-z0-9_-]{22}$", id);       // URL-safe, no padding
        Assert.NotNull(tk);
        Assert.Equal("item1", tk!.ItemId);
        Assert.Null(tk.FileIno);
        Assert.Equal(Did, tk.Did);
        Assert.Equal("Author - Title.epub", tk.DownloadName);
        Assert.Equal("/cache/x.epub", tk.FilePath);
        Assert.Null(tk.Access);                          // serving a cache file needs no ABS
    }

    [Fact]
    public void A_raw_ticket_redeems_to_a_bearer_and_no_cache_path()
    {
        var tickets = new DownloadTickets();

        var tk = tickets.Redeem(tickets.MintRaw("item1", "3", Did, "My Book.epub", "access-tok"));

        Assert.NotNull(tk);
        Assert.Equal("3", tk!.FileIno);
        Assert.Equal("access-tok", tk.Access);
        Assert.Null(tk.FilePath);
    }

    [Fact]
    public void An_unknown_or_absent_id_redeems_to_null()
    {
        var tickets = new DownloadTickets();
        tickets.MintEpub("item1", null, Did, "x.epub", "/cache/x.epub");

        Assert.Null(tickets.Redeem("nope"));
        Assert.Null(tickets.Redeem(""));
        Assert.Null(tickets.Redeem(null));
    }

    [Fact]
    public void A_ticket_dies_after_fifteen_idle_minutes()
    {
        var clock = new FakeClock();
        var tickets = new DownloadTickets(clock);
        var id = tickets.MintEpub("item1", null, Did, "x.epub", "/cache/x.epub");

        clock.Utc = clock.Utc.AddMinutes(15).AddSeconds(1);

        Assert.Null(tickets.Redeem(id));
    }

    [Fact]
    public void Use_re_stamps_the_window_so_a_polled_link_outlives_it()
    {
        // The convert poll hits the same href every 5s. A ticket must survive a long
        // conversion, then die once the page goes quiet.
        var clock = new FakeClock();
        var tickets = new DownloadTickets(clock);
        var id = tickets.MintEpub("item1", null, Did, "x.epub", "/cache/x.epub");

        for (var i = 0; i < 6; i++)
        {
            clock.Utc = clock.Utc.AddMinutes(10);   // an hour in total, never idle 15
            Assert.NotNull(tickets.Redeem(id));
        }

        clock.Utc = clock.Utc.AddMinutes(16);
        Assert.Null(tickets.Redeem(id));
    }
}
```

Add to `tests/Inkshelf.Tests/RequestLogTests.cs`:

```csharp
    [Fact]
    public void The_logged_query_keeps_everything_but_the_ticket()
    {
        Assert.Equal("?file=2&t=…&return=%2F",
            RequestLog.Redact("?file=2&t=Ab_1Cd-2Ef3Gh4Ij5Kl6&return=%2F"));
        Assert.Equal("?t=…", RequestLog.Redact("?t=Ab_1Cd-2Ef3Gh4Ij5Kl6"));
        Assert.Equal("?sort=t", RequestLog.Redact("?sort=t"));   // not a ticket
        Assert.Equal("", RequestLog.Redact(""));
        Assert.Null(RequestLog.Redact(null));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests --filter "DownloadTicketsTests|RequestLogTests"`
Expected: build failure - `DownloadTickets` and `RequestLog.Redact` do not exist.

- [ ] **Step 3: Write `DownloadTickets`**

Create `src/Inkshelf/DownloadTickets.cs`:

```csharp
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

    public DownloadTickets(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public string MintEpub(string itemId, string? fileIno, string did, string downloadName, string filePath) =>
        Mint(new Ticket(itemId, fileIno, did, downloadName, FilePath: filePath));

    public string MintRaw(string itemId, string? fileIno, string did, string downloadName, string access) =>
        Mint(new Ticket(itemId, fileIno, did, downloadName, Access: access));

    // Re-stamps on success. Unknown and expired are indistinguishable: both null.
    public Ticket? Redeem(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_live.TryGetValue(id, out var e)) return null;
        if (Expired(e.Stamp)) { _live.TryRemove(id, out _); return null; }
        _live[id] = (e.T, Now);
        return e.T;
    }

    private string Mint(Ticket t)
    {
        foreach (var (k, v) in _live) if (Expired(v.Stamp)) _live.TryRemove(k, out _);
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        _live[id] = (t, Now);
        return id;
    }

    private long Now => _clock.GetUtcNow().UtcTicks;
    private bool Expired(long stamp) => Now - stamp > IdleWindow.Ticks;
}
```

- [ ] **Step 4: Register the singleton**

In `src/Inkshelf/Program.cs`, immediately after the `DownloadMarks` registration:

```csharp
builder.Services.AddSingleton(new DownloadTickets());
```

Register the instance, not the type: the constructor's `TimeProvider?` is an optional parameter, and this matches how `EpubCache` and `DownloadMarks` are registered.

- [ ] **Step 5: Redact the ticket from the request log**

In `src/Inkshelf/RequestLog.cs`, add `using System.Text.RegularExpressions;`, change the log call's query argument to `Redact(ctx.Request.QueryString.Value)`, and add:

```csharp
    // A ticket in a URL is a capability for one file (see DownloadTickets). The
    // query is logged because it is what makes a failure readable; the ticket's
    // value adds nothing to that and would outlive the log line's usefulness.
    private static readonly Regex TicketValue = new(@"(?<=[?&]t=)[^&]*", RegexOptions.Compiled);

    internal static string? Redact(string? query) =>
        string.IsNullOrEmpty(query) ? query : TicketValue.Replace(query, "…");
```

Also correct the file's header comment: the claim that "No URL in this app ever carries anything authorising" is no longer true. Replace that sentence with one saying the ticket is the exception and is redacted.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Inkshelf.Tests --filter "DownloadTicketsTests|RequestLogTests"`
Expected: PASS (5 ticket tests + the existing 2 log tests + the new one).

- [ ] **Step 7: Full suite and format**

Run: `dotnet test` then `dotnet format --verify-no-changes`
Expected: all green, no formatting diff.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/DownloadTickets.cs src/Inkshelf/Program.cs src/Inkshelf/RequestLog.cs \
        tests/Inkshelf.Tests/DownloadTicketsTests.cs tests/Inkshelf.Tests/RequestLogTests.cs
git commit -m "feat: add the download ticket table"
```

---

### Task 2: The row state resolver returns its cache path

**Files:**
- Modify: `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs` (`_states`, `ComputeConvertStates`, `RowState`, `RowFor`)
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs` (~line 98)
- Modify: `src/Inkshelf/Pages/Item.cshtml.cs` (~line 75)
- Modify: `tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `static (ConvertRowState State, string? Path) Resolve(AbsItem item, AbsBatchMedia? media, RenderTarget target, EpubCache cache, ConvertQueue queue)`
  - `static (ConvertRowState State, string? Path) ResolveFor(string itemId, long size, long mtimeMs, string? fmt, RenderTarget target, EpubCache cache, ConvertQueue queue)`
  - `Path` is null exactly when `State == ConvertRowState.NotConvertible`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/ConvertRowStateResolverTests.cs`:

```csharp
    [Fact]
    public void ResolveFor_hands_back_the_cache_path_it_keyed_on()
    {
        // The path is what a download ticket holds, so it must be the SAME path the
        // state was decided from - not one the caller re-derives and gets wrong.
        var cache = new EpubCache(TempDirPath());
        var target = new RenderTarget(800, 1000, 1.0, false);

        var r = ConvertRowStateResolver.ResolveFor("i1", 99, 88, "cbz", target, cache, new ConvertQueue());

        Assert.Equal(cache.PathFor("i1", 99, 88, target.MaxW, target.MaxH,
            target.Grayscale, target.Spread, target.Scale, target.Dpr), r.Path);
    }

    [Fact]
    public void ResolveFor_has_no_path_when_the_item_is_not_convertible()
    {
        var r = ConvertRowStateResolver.ResolveFor("i1", 1, 2, "pdf",
            new RenderTarget(800, 1000, 1.0, false), new EpubCache(TempDirPath()), new ConvertQueue());

        Assert.Equal(ConvertRowState.NotConvertible, r.State);
        Assert.Null(r.Path);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Inkshelf.Tests --filter ConvertRowStateResolverTests`
Expected: build failure - `ConvertRowState` has no `.Path`.

- [ ] **Step 3: Change the resolver**

```csharp
    public static (ConvertRowState State, string? Path) Resolve(AbsItem item, AbsBatchMedia? media,
        RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        // Listing items (minified) carry media.ebookFormat; search/batch items
        // (expanded) carry the format only on the ebookFile. Check all.
        var fmt = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat ?? media?.EbookFile?.EbookFormat;
        var efm = media?.EbookFile?.Metadata;
        if (efm is null) return (ConvertRowState.NotConvertible, null); // can't key the cache
        return ResolveFor(item.Id, efm.Size, efm.MtimeMs, fmt, target, cache, queue);
    }

    // Lower-level: state for one specific (itemId, file size+mtime, format), plus the
    // cache path it was decided from - a download ticket has to hold that exact path.
    public static (ConvertRowState State, string? Path) ResolveFor(string itemId, long size, long mtimeMs,
        string? fmt, RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        if (fmt != "cbz" && fmt != "cbr") return (ConvertRowState.NotConvertible, null);
        var path = cache.PathFor(itemId, size, mtimeMs, target.MaxW, target.MaxH, target.Grayscale, target.Spread, target.Scale, target.Dpr);
        return (queue.Status(path) switch
        {
            ConvertStatus.Done => ConvertRowState.Cached,
            ConvertStatus.Queued or ConvertStatus.Running => ConvertRowState.Converting,
            ConvertStatus.Failed => ConvertRowState.Failed,
            _ => ConvertRowState.Convert,
        }, path);
    }
```

- [ ] **Step 4: Update the three call sites**

`Library.cshtml.cs`: `_states` becomes `Dictionary<string, (ConvertRowState State, string? Path)>`; `RowState` returns the tuple; `ComputeConvertStates` tests `.State` for `AnyConverting`; `RowFor` reads `.State` where it read the enum. Keep the tuple in `_states` - Task 7 needs the path.

`Converted.cshtml.cs` (~line 98): `var (state, cachePath) = ConvertRowStateResolver.Resolve(...);` and the `AnyConverting` check uses `state`. Keep `cachePath` in scope; Task 7 uses it. Until then, silence the unused variable by using `_` and restoring it in Task 7 - do NOT leave a build warning.

`Item.cshtml.cs` (~line 75): `var (state, cachePath) = ConvertRowStateResolver.ResolveFor(...);` with the same note.

Also fix the stale comment in `Library.cshtml.cs`'s `RowFor`: it claims `ComputeConvertStates` runs only for the listing branch, which stopped being true when the search branch started calling it (`Library.cshtml.cs:78-82`). The fallback it guards is now only reached when the batch-metadata call failed - say that instead.

- [ ] **Step 5: Update the existing resolver tests**

The six existing assertions compare the return value to a `ConvertRowState`. Change each to compare `r.State`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: PASS - including `ListingRenderTests`, `ConvertedRenderTests` and `ItemRenderTests`, which must be untouched by this task.

- [ ] **Step 7: Format and commit**

```bash
dotnet format --verify-no-changes
git add -A src/Inkshelf tests/Inkshelf.Tests
git commit -m "refactor: return the cache path the row state was keyed on"
```

---

### Task 3: The download client reports content type and length

**Files:**
- Modify: `src/Inkshelf/Abs/AbsDownloadClient.cs` (`DownloadEbookAsync`, ~line 22)
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs` (~line 80)
- Modify: `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Task<(Stream Content, string ContentType, long? Length)> DownloadEbookAsync(string itemId, string accessToken, CancellationToken ct, string? fileIno = null)`

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/AbsDownloadClientTests.cs`, following the file's existing `Client(stub)` helper:

```csharp
    [Fact]
    public async Task DownloadEbookAsync_reports_the_content_type_and_length_abs_sent()
    {
        // A ticket-served raw download has no cookie path to fall back on, so these
        // two headers have to come from here: a response with no Content-Length is
        // one some e-reader download managers refuse.
        var body = new byte[] { 1, 2, 3, 4 };
        var stub = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new("application/epub+zip"), ContentLength = body.Length }
            }
        });

        var (content, type, length) = await Client(stub).DownloadEbookAsync("i1", "tok", default);

        Assert.Equal("application/epub+zip", type);
        Assert.Equal(4, length);
        Assert.Equal(4, (await new StreamReader(content).ReadToEndAsync()).Length);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Inkshelf.Tests --filter AbsDownloadClientTests`
Expected: build failure - cannot deconstruct a `Stream`.

- [ ] **Step 3: Widen the return**

In `AbsDownloadClient.DownloadEbookAsync`, replace the final `return await res.Content.ReadAsStreamAsync(ct);` with:

```csharp
        // Content type and length come back too: a ticket-served download has no
        // second source for them, and AbsApiClient.StreamAsync uses the same fallback.
        return (await res.Content.ReadAsStreamAsync(ct),
            res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            res.Content.Headers.ContentLength);
```

and change the signature to `Task<(Stream Content, string ContentType, long? Length)>`.

- [ ] **Step 4: Fix the worker's call site**

In `ConvertWorker.cs`, the `await using (var archive = await download.DownloadEbookAsync(...))` line cannot bind a tuple. Change to:

```csharp
                var dl = await download.DownloadEbookAsync(job.ItemId, job.AccessToken, ct, job.FileIno);
                await using (var archive = dl.Content)
```

leaving the rest of the block as it is.

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: PASS, including all `ConvertWorkerTests`.

- [ ] **Step 6: Format and commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf/Abs/AbsDownloadClient.cs src/Inkshelf/Convert/ConvertWorker.cs tests/Inkshelf.Tests/AbsDownloadClientTests.cs
git commit -m "refactor: report content type and length from the ebook download"
```

---

### Task 4: `/convert` serves a ticket

**Files:**
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs`
- Create: `tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs`

**Interfaces:**
- Consumes: `DownloadTickets.Redeem`, `DownloadTickets.MintEpub`, `DownloadTickets.Ticket` (Task 1).
- Produces: `GET /convert/{id}?t=<ticket>` streams the cached EPUB with no cookie and no ABS call.

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs`:

```csharp
using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Tests;

// A download manager on an e-reader re-requests the URL with NO cookies (issue
// #40). These tests are that request: no cookie header anywhere.
public class DownloadTicketEndpointTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tix-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string Did = "abc123def4560000";

    // ABS_URL points at a dead port on purpose: anything that reaches ABS fails, so
    // a passing test proves the ticket path never needed it.
    private static WebApplicationFactory<Program> CreateFactory(string cachePath, string keysPath,
        Action<IServiceCollection>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://localhost:1");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
                extra?.Invoke(services);
            });
        });

    private static string CachedEpub(string dir, string body = "EPUBBYTES")
    {
        var path = Path.Combine(dir, "item1-1-2-800x1000-f.epub");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public async Task A_cookie_less_convert_download_with_a_ticket_streams_the_cached_file()
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("EPUBBYTES", await res.Content.ReadAsStringAsync());
        Assert.Equal("Author - Title.epub", res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("bytes", res.Headers.AcceptRanges);   // a physical file can resume
    }

    [Fact]
    public async Task A_convert_ticket_marks_the_download_against_its_own_device()
    {
        // The cookie-less request has no settings cookie either, so without the
        // ticket's did the app would mint a fresh one per download - the trail of
        // four device ids in 90 minutes seen on the shine.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(DownloadMarks.EpubKey("item1", null),
            factory.Services.GetRequiredService<DownloadMarks>().Read(Did));
        // No new device id minted: the ticket already carried one.
        res.Headers.TryGetValues("Set-Cookie", out var setCookies);
        Assert.DoesNotContain("inkshelf_settings", string.Join(";", setCookies ?? []));
    }

    [Theory]
    [InlineData("&status=1")]
    [InlineData("&warm=1")]
    [InlineData("&fresh=1")]
    public async Task A_ticket_authorises_bytes_only_never_a_kick_or_a_poll(string extra)
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/item1?t={t}{extra}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_ticket_cannot_be_replayed_on_another_item()
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/other?t={t}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task An_expired_or_bogus_ticket_falls_through_to_the_cookie_path()
    {
        // Additive, never a gate: with no cookie either, that path is today's 401 -
        // never a worse outcome than before tickets existed.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        CachedEpub(cache.Path);

        var res = await client.GetAsync("/convert/item1?t=Ab_1Cd-2Ef3Gh4Ij5Kl6");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.StartsWith("text/plain", res.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task A_ticket_wins_over_a_session_cookie_so_both_requests_take_one_path()
    {
        // The browser's request and the manager's must be served identically; ABS is
        // unreachable here, so a 200 proves the cookie path was not taken.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/convert/item1?t={t}");
        req.Headers.Add("Cookie", $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}");

        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("EPUBBYTES", await res.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests --filter DownloadTicketEndpointTests`
Expected: the download tests FAIL with 401 (no ticket handling yet); the serve-only, replay and fall-through tests may already pass - that is fine, they are guards.

- [ ] **Step 3: Redeem the ticket in the endpoint**

In `ConvertEndpoints.MapConvertEndpoints`, add `string? t` and `DownloadTickets tickets` to the handler parameters, and insert this as the **first** statement of the handler body:

```csharp
            // Redeem unconditionally: a poll carries the same href, and re-stamping
            // there is what keeps a long conversion's link alive.
            var tk = tickets.Redeem(t);
            // A ticket serves bytes and nothing else - no kick, no poll, no fresh.
            if (status is null && warm is null && fresh is not ("1" or "true")
                && tk is { FilePath: { } cached } && tk.ItemId == id && File.Exists(cached))
            {
                marks.Add(tk.Did, DownloadMarks.EpubKey(id, tk.FileIno));
                return Results.File(cached, "application/epub+zip",
                    fileDownloadName: tk.DownloadName, enableRangeProcessing: true);
            }
```

Add `enableRangeProcessing: true` to the existing cookie-path `Results.File(result.FilePath!, …)` as well, so both paths advertise the same thing.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Inkshelf.Tests --filter DownloadTicketEndpointTests`
Expected: PASS (all 8 cases).

- [ ] **Step 5: Full suite, format, commit**

```bash
dotnet test
dotnet format --verify-no-changes
git add src/Inkshelf/Endpoints/ConvertEndpoints.cs tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs
git commit -m "feat: serve a converted comic from a download ticket"
```

---

### Task 5: `/download` serves a ticket

**Files:**
- Modify: `src/Inkshelf/Endpoints/DownloadEndpoints.cs`
- Modify: `tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs`

**Interfaces:**
- Consumes: `DownloadTickets.MintRaw`, `DownloadTickets.Redeem` (Task 1); `AbsDownloadClient.DownloadEbookAsync` returning `(Stream, string, long?)` (Task 3).
- Produces: `GET /download/{id}?t=<ticket>` streams from ABS on the ticket's bearer with no cookie.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs`:

```csharp
    private static StubHandler EbookStub(byte[] body) => new(_ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new("application/epub+zip"), ContentLength = body.Length }
            }
        });

    [Fact]
    public async Task A_cookie_less_raw_download_with_a_ticket_streams_on_the_tickets_bearer()
    {
        var body = new byte[] { 9, 8, 7 };
        var stub = EbookStub(body);
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path, services =>
            services.AddSingleton(new AbsDownloadClient(new HttpClient(stub) { BaseAddress = new Uri("http://abs.local") })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintRaw("item1", null, Did, "My Book.epub", "access-tok");

        var res = await client.GetAsync($"/download/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(body, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, res.Content.Headers.ContentLength);
        Assert.Equal("My Book.epub", res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("/api/items/item1/ebook", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("access-tok", stub.Last.Headers.Authorization?.Parameter);
        Assert.Contains(DownloadMarks.RawKey("item1", null),
            factory.Services.GetRequiredService<DownloadMarks>().Read(Did));
    }

    [Fact]
    public async Task A_raw_ticket_for_one_ebook_file_asks_abs_for_that_ino()
    {
        var stub = EbookStub([1]);
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path, services =>
            services.AddSingleton(new AbsDownloadClient(new HttpClient(stub) { BaseAddress = new Uri("http://abs.local") })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintRaw("item1", "3", Did, "Second.pdf", "access-tok");

        var res = await client.GetAsync($"/download/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("/api/items/item1/ebook/3", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Contains(DownloadMarks.RawKey("item1", "3"),
            factory.Services.GetRequiredService<DownloadMarks>().Read(Did));
    }

    [Fact]
    public async Task An_epub_ticket_does_not_authorise_a_raw_download()
    {
        // Wrong kind: no bearer in it, so /download must fall through to the cookie
        // path rather than invent one.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/download/item1?t={t}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests --filter DownloadTicketEndpointTests`
Expected: the two raw-download tests FAIL with 401.

- [ ] **Step 3: Redeem the ticket in the endpoint**

In `DownloadEndpoints.MapDownloadEndpoints`, add `string? t`, `DownloadTickets tickets` and `AbsDownloadClient dl` to the handler parameters, and insert before the existing `try`:

```csharp
            // A cookie-less download manager (issue #40): the ticket carries both the
            // filename and the ABS bearer, so this path needs neither the cookie nor
            // an item-detail lookup.
            if (tickets.Redeem(t) is { Access: { } access } tk && tk.ItemId == id)
            {
                try
                {
                    var (stream, type, len) = await dl.DownloadEbookAsync(id, access, ct, tk.FileIno);
                    marks.Add(tk.Did, DownloadMarks.RawKey(id, tk.FileIno));
                    ctx.Response.ContentLength = len;
                    return Results.File(stream, type, fileDownloadName: tk.DownloadName);
                }
                catch (HttpRequestException) { return Results.NotFound(); }
            }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Inkshelf.Tests --filter DownloadTicketEndpointTests`
Expected: PASS (11 cases).

- [ ] **Step 5: Full suite, format, commit**

```bash
dotnet test
dotnet format --verify-no-changes
git add src/Inkshelf/Endpoints/DownloadEndpoints.cs tests/Inkshelf.Tests/DownloadTicketEndpointTests.cs
git commit -m "feat: serve a raw ebook download from a download ticket"
```

---

### Task 6: Shared helpers the mint sites need

**Files:**
- Create: `src/Inkshelf/Convert/EpubName.cs`
- Modify: `src/Inkshelf/Convert/ConvertService.cs` (its private `Sanitize` and the `downloadName` line)
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs`, `src/Inkshelf/Endpoints/DownloadEndpoints.cs` (drop their duplicate local `EnsureDid`)
- Modify: `tests/Inkshelf.Tests/ConvertServiceTests.cs` (only if it asserts the download name)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `static string Inkshelf.Convert.EpubName.For(string? author, string? title)` → `"Author - Title.epub"`, invalid filename characters replaced with `_`, `"Unknown"` / `"Untitled"` for null or empty.
  - `static DeviceSettings Inkshelf.Auth.DeviceSettings.EnsureDid(HttpContext ctx)` → the request's settings, minting and setting a device id when it carries none.

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/EpubNameTests.cs`:

```csharp
using Inkshelf.Convert;

namespace Inkshelf.Tests;

// The name is minted at page render (into a ticket) and derived again on the
// cookie path. One helper, so the two can never hand the reader different names.
public class EpubNameTests
{
    [Fact]
    public void Joins_author_and_title()
        => Assert.Equal("Alan Moore - Watchmen.epub", EpubName.For("Alan Moore", "Watchmen"));

    [Fact]
    public void Falls_back_when_metadata_is_missing()
        => Assert.Equal("Unknown - Untitled.epub", EpubName.For(null, ""));

    [Fact]
    public void Replaces_characters_a_filename_cannot_hold()
        => Assert.Equal("A_B - C_D.epub", EpubName.For("A/B", "C/D"));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Inkshelf.Tests --filter EpubNameTests`
Expected: build failure - `EpubName` does not exist.

- [ ] **Step 3: Write `EpubName`**

```csharp
namespace Inkshelf.Convert;

// The download name for a converted EPUB. Shared because a download ticket is
// minted with it at page render while the cookie path derives it again at serve
// time; two copies of this would eventually disagree.
public static class EpubName
{
    public static string For(string? author, string? title) =>
        Sanitize($"{(string.IsNullOrWhiteSpace(author) ? "Unknown" : author)}"
            + $" - {(string.IsNullOrWhiteSpace(title) ? "Untitled" : title)}") + ".epub";

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
```

- [ ] **Step 4: Use it in `ConvertService`**

Replace `var downloadName = Sanitize($"{author} - {title}") + ".epub";` with `var downloadName = EpubName.For(author, title);` and delete the now-unused private `Sanitize`. Leave the `title`/`author` fallbacks where they are - `EpubName` only guards against null.

- [ ] **Step 5: Add `DeviceSettings.EnsureDid`**

In `src/Inkshelf/Auth/DeviceSettings.cs`:

```csharp
    // Download marks are keyed to the device id, and a ticket minted on a page
    // render has to carry a real one. Both file endpoints used to mint it
    // themselves; one copy, called from wherever a did is first needed.
    public static DeviceSettings EnsureDid(HttpContext ctx)
    {
        var s = Read(ctx.Request);
        return string.IsNullOrEmpty(s.Did) ? Set(ctx.Response, s) : s;
    }
```

Then delete the local `EnsureDid` in `ConvertEndpoints` (it is inline in the handler, using `ds`) and in `DownloadEndpoints` (a local static function), and call `DeviceSettings.EnsureDid(httpContext)` / `DeviceSettings.EnsureDid(ctx)` instead. Behaviour must not change: `ConvertEndpoints` still needs the settings for `ToRenderTarget`, so read them once via `EnsureDid` at the top of the handler and use the result for both.

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: PASS. `DownloadMarkEndpointTests` covers the did-minting behaviour - if any of it fails, `EnsureDid` changed semantics and must be fixed, not the test.

- [ ] **Step 7: Format and commit**

```bash
dotnet format --verify-no-changes
git add -A src/Inkshelf tests/Inkshelf.Tests
git commit -m "refactor: share the epub download name and the device id mint"
```

---

### Task 7: Mint tickets into every download link

**Files:**
- Modify: `src/Inkshelf/Pages/Support/ItemRowModel.cs`
- Modify: `src/Inkshelf/Pages/Support/ConvertActionModel.cs`
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml` (~line 55)
- Modify: `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml` (lines 5-7)
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs` (ctor, `OnGetAsync`, `RowFor`)
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs` (ctor, `OnGetAsync` row loop)
- Modify: `src/Inkshelf/Pages/Item.cshtml.cs` (ctor, the file loop)
- Modify: `tests/Inkshelf.Tests/ItemRenderTests.cs` (lines 101, 150, 154)

**Interfaces:**
- Consumes: `DownloadTickets.MintEpub` / `MintRaw` (Task 1); `(State, Path)` from `ConvertRowStateResolver` (Task 2); `EpubName.For` and `DeviceSettings.EnsureDid` (Task 6).
- Produces:
  - `ItemRowModel` gains `string? RawTicket = null, string? EpubTicket = null` and `public string DownloadHref`.
  - `ConvertActionModel` gains `string? Ticket = null` (after `ShowRegen`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ItemRenderTests.cs`:

```csharp
    [Fact]
    public async Task Every_download_link_carries_a_ticket()
    {
        // A reader's download manager re-requests these hrefs with no cookies, so a
        // link without a ticket is a download that cannot complete (issue #40).
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, PSize, PMtime, W, H,
            spread: DeviceSettings.Default.Spread, scale: DeviceSettings.Default.Scale), "epub");

        var html = await (await client.SendAsync(Request(factory, $"/item/{ItemId}"))).Content.ReadAsStringAsync();

        Assert.Matches($"href=\"/download/{ItemId}\\?t=[A-Za-z0-9_-]{{22}}\"", html);          // primary raw
        Assert.Matches($"href=\"/download/{ItemId}\\?file=2&amp;t=[A-Za-z0-9_-]{{22}}\"", html); // by ino
        Assert.Matches($"href=\"/convert/{ItemId}\\?return=[^\"]*&amp;t=[A-Za-z0-9_-]{{22}}\"", html);
        // The regenerate link must NOT carry one: a ticket never authorises fresh=1.
        Assert.Matches($"href=\"/convert/{ItemId}\\?fresh=1&amp;return=[^\"]*\"", html);
    }
```

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs` (its `LibraryRequest` helper and `MakeStub` already cover both branches):

```csharp
    [Fact]
    public async Task Listing_and_search_rows_both_carry_download_tickets()
    {
        // The search branch fetches the same batch metadata as the listing
        // (Library.cshtml.cs:78-82), so it can key the cache and mint an EPUB
        // ticket too. A search row that silently lost its ticket would be a
        // download that only fails on a cookie-less reader.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var listing = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        var searchReq = LibraryRequest(factory);
        searchReq.RequestUri = new Uri($"/library/{LibId}?q=comic", UriKind.Relative);
        var search = await (await client.SendAsync(searchReq)).Content.ReadAsStringAsync();

        foreach (var html in new[] { listing, search })
        {
            Assert.Matches($"href=\"/download/{ItemId}\\?t=[A-Za-z0-9_-]{{22}}\"", html);
            Assert.Matches($"href=\"/convert/{ItemId}\\?return=[^\"]*&amp;t=[A-Za-z0-9_-]{{22}}\"", html);
        }
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests --filter "ItemRenderTests|ListingRenderTests"`
Expected: the new tests FAIL (no `t=` in any href). The three existing assertions at `ItemRenderTests.cs:101,150,154` still pass at this point.

- [ ] **Step 3: Carry the tickets on the view models**

`ItemRowModel.cs` - append two parameters and one computed property:

```csharp
public record ItemRowModel(
    AbsItem Item,
    LibraryLinks Links,
    IReadOnlyList<AbsRef>? Authors = null,
    IReadOnlyList<AbsSeriesRef>? Series = null,
    ConvertRowState State = ConvertRowState.NotConvertible,
    string ReturnUrl = "/",
    bool Read = false,
    bool RawDownloaded = false,
    bool EpubDownloaded = false,
    string? RawTicket = null,
    string? EpubTicket = null)
{
    // A download manager re-requests this href without cookies, so the ticket has
    // to be in it, not added later by script.
    public string DownloadHref =>
        RawTicket is null ? $"/download/{Item.Id}" : $"/download/{Item.Id}?t={RawTicket}";
}
```

`ConvertActionModel.cs` - append `string? Ticket = null` after `ShowRegen`.

- [ ] **Step 4: Put them in the hrefs**

`_ItemRow.cshtml` line 55: `href="/download/@item.Id"` → `href="@Model.DownloadHref"`.

Same file, the `_ConvertAction` partial construction: add `Ticket: Model.EpubTicket` to the `new ConvertActionModel(...)` call.

`_ConvertAction.cshtml`, lines 5-7:

```csharp
    var tq = string.IsNullOrEmpty(Model.Ticket) ? "" : $"&t={Model.Ticket}";
    var baseHref = $"/convert/{Model.Id}?{fileQ}return={ret}{tq}";
    var freshHref = $"/convert/{Model.Id}?{fileQ}fresh=1&return={ret}";
    var whyHref = $"/convert/{Model.Id}/why?{fileQ}return={ret}";
```

`freshHref` and `whyHref` stay ticket-free: a ticket must not authorise `fresh=1`, and `why` is a page that needs the cookie anyway.

- [ ] **Step 5: Mint in `Library.cshtml.cs`**

Constructor gains `TokenStore tokens, DownloadTickets tickets`; add the two fields. In `OnGetAsync`, replace `var ds = DeviceSettings.Read(Request);` with `var ds = DeviceSettings.EnsureDid(HttpContext);` and store what `RowFor` needs:

```csharp
        _did = ds.Did;
        _access = _tokens.Read()?.Access;
```

In `RowFor`, after the existing state lookup (now a `(State, Path)` tuple):

```csharp
        // Both hrefs are re-requested by a cookie-less download manager, so each
        // gets a ticket standing for exactly the file that row offers.
        var meta = media?.Metadata;
        var epubTicket = state.Path is { } path
            ? _tickets.MintEpub(item.Id, null, _did,
                EpubName.For(meta?.Authors?.FirstOrDefault()?.Name, meta?.Title ?? item.Media?.Metadata?.Title), path)
            : null;
        var filename = media?.EbookFile?.Metadata?.Filename ?? item.Media?.EbookFile?.Metadata?.Filename;
        var rawTicket = filename is not null && _access is { } acc
            ? _tickets.MintRaw(item.Id, null, _did, filename, acc)
            : null;
```

and pass `rawTicket`/`epubTicket` as the last two `ItemRowModel` arguments. `_states`'
fallback for a missing entry is now `(ConvertRowState.NotConvertible, null)`, so a row
with no cached path simply mints no EPUB ticket.

- [ ] **Step 6: Mint in `Converted.cshtml.cs`**

Same constructor additions. In `OnGetAsync`, use `DeviceSettings.EnsureDid(HttpContext)` for `settings`, read the access token once before the loop, and in the row loop use the `cachePath` from Task 2's tuple to mint the EPUB ticket and `m.EbookFile?.Metadata?.Filename` for the raw one. Pass both to `ItemRowModel`.

- [ ] **Step 7: Mint in `Item.cshtml.cs`**

Same constructor additions; `ds` comes from `DeviceSettings.EnsureDid(HttpContext)`. Read `var access = _tokens.Read()?.Access;` once. In the file loop:

```csharp
            var rawTicket = access is { } acc ? _tickets.MintRaw(Id, keyIno, ds.Did, name, acc) : null;
            var dl = (isPrimary ? $"/download/{Id}" : $"/download/{Id}?file={Uri.EscapeDataString(f.Ino!)}")
                + (rawTicket is null ? "" : (isPrimary ? $"?t={rawTicket}" : $"&t={rawTicket}"));
```

and for the convert action, take the path from the resolver tuple:

```csharp
                var (state, cachePath) = ConvertRowStateResolver.ResolveFor(
                    Id, f.Metadata.Size, f.Metadata.MtimeMs, fmt, target, _cache, _queue);
                var epubTicket = cachePath is null ? null
                    : _tickets.MintEpub(Id, keyIno, ds.Did,
                        EpubName.For(Meta?.AuthorName ?? Meta?.Authors?.FirstOrDefault()?.Name, Meta?.Title), cachePath);
                convert = new ConvertActionModel(Id, keyIno, state, $"/item/{Id}",
                    marks.Contains(DownloadMarks.EpubKey(Id, keyIno)), ShowRegen: true, Ticket: epubTicket);
```

- [ ] **Step 8: Update the three assertions the new hrefs break**

`ItemRenderTests.cs`:
- line 101 `Assert.Contains($"/download/{ItemId}\"", html);` → `Assert.Matches($"/download/{ItemId}\\?t=", html);`
- line 150 → `Regex.Match(html, $"<a [^>]*href=\"/download/{ItemId}\\?t=[^\"]*\">([^<]*)</a>")`
- line 154 → `Regex.Match(html, $"<a [^>]*href=\"/download/{ItemId}\\?file=2&amp;t=[^\"]*\">([^<]*)</a>")`

Do not loosen any other assertion. If a test outside these three fails, the href shape changed in a way this task did not intend - fix the code, not the test.

- [ ] **Step 9: Run the tests**

Run: `dotnet test`
Expected: PASS, all of it.

- [ ] **Step 10: Format and commit**

```bash
dotnet format --verify-no-changes
git add -A src/Inkshelf tests/Inkshelf.Tests
git commit -m "feat: mint a download ticket into every download link"
```

---

### Task 8: Browser pass and docs

**Files:**
- Modify: `tools/uicheck/Program.cs` (after the `item-comic-de` assertions, ~line 166)
- Modify: `docs/ARCHITECTURE.md` (the `## Invariants (do not "clean these up")` section)
- Modify: `docs/ROADMAP.md` (`## Done`)

**Interfaces:**
- Consumes: the rendered hrefs from Task 7.
- Produces: nothing code depends on.

- [ ] **Step 1: Assert the tickets in the browser pass**

In `tools/uicheck/Program.cs`, after `Expect("item-comic-de", …)`:

```csharp
        // A link with no ticket is a download an e-reader's manager cannot finish.
        var comicHtml = await page.ContentAsync();
        if (!Regex.IsMatch(comicHtml, @"href=""/download/[^""]*(\?|&amp;)t=[A-Za-z0-9_-]{22}"""))
            failures.Add("item-comic-de: a raw download link carries no ticket");
        if (!Regex.IsMatch(comicHtml, @"href=""/convert/[^""]*(\?|&amp;)t=[A-Za-z0-9_-]{22}"""))
            failures.Add("item-comic-de: the convert link carries no ticket");
```

Add `using System.Text.RegularExpressions;` if the file lacks it.

- [ ] **Step 2: Run the browser pass**

Run: `tools/uicheck/run.sh`
Expected: exit 0. Then **look at** the new screenshots in `tools/uicheck/shots/` - the exit code is not the check.

- [ ] **Step 3: Add the invariant to ARCHITECTURE.md**

One bullet in `## Invariants (do not "clean these up")`, phrased as the absence it is:

```markdown
- **A download ticket serves bytes and nothing else.** `?t=` authorises streaming
  one already-identified file - never a conversion kick, a status poll or
  `fresh=1`, and never a second item (both endpoints check the ticket's item id
  against the route). It is additive: a missing or expired ticket must fall
  through to the cookie path, so a request that works today cannot start failing.
  Tickets exist because an e-reader's download manager re-requests the URL with
  no cookies, so nothing may be moved out of the URL into a cookie.
```

Nothing else in this file changes - it is a map, not a changelog.

- [ ] **Step 4: Record it in ROADMAP.md**

Add one line to `## Done`, matching the existing entries' style, naming issue #40 and what it fixes.

- [ ] **Step 5: Commit**

```bash
git add tools/uicheck/Program.cs docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "test: assert download links carry a ticket"
```

- [ ] **Step 6: Hand over for the device pass**

Start the dev server against the user's ABS on port 5099 so the shine can be tested with a 54 MB comic and a large raw file, and say which log lines to watch: the manager's second cookie-less request must now be a 200 that runs to completion, not a 26-byte 401.

---

## Notes for the implementer

- The endpoint tests deliberately point `ABS_URL` at a dead port. If a ticket test starts needing ABS to be reachable, the ticket path has grown a dependency it must not have.
- `Results.File(path, …)` needs a rooted path. Ticket file paths come from `EpubCache.PathFor`, which is rooted.
- Do not add `Accept-Ranges` handling to the raw ABS stream. The spec explains what evidence would justify it; we do not have that evidence yet.
- Do not put a ticket in a page URL, a form action, or the reader's image requests. Download links only.
