# Downloaded-File Marks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show, on each download action, whether *this device* has already fetched *that file* — and retire the now-redundant `EPUB ✓` checkmark.

**Architecture:** A random device id (`did`) becomes one more key in the existing `inkshelf_settings` cookie, minted inside `DeviceSettings.Set` so no call site can forget. A new singleton `DownloadMarks` stores one append-only file per device under `{CachePath}/marks/`, holding opaque keys that distinguish raw ebooks from converted EPUBs. Both download endpoints append a key on request; the three render sites load the device's set once and pass per-action booleans into the existing row/action models.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit. No new dependencies.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-28-downloaded-file-marks-design.md`. Read it before starting — sections A (identity, trust boundary), C (key scheme) and E (the `✓` retirement) are the ones with traps.
- **No new dependencies. No new configuration keys.**
- **All work happens inside the devcontainer.** There is no `dotnet` on the host.
- **Branch:** `feat/downloaded-marks` (already created, spec already committed).
- **Conventional Commits**, imperative lowercase subject, max ~72 chars.
- **Do NOT add `Co-Authored-By:` or "Generated with Claude Code" lines to commits.**
- **Do NOT edit `CHANGELOG.md`.** Shipped work goes to `ROADMAP.md`'s `## Done`.
- **`docs/ARCHITECTURE.md` is a map, not a diary** — see the rules in `CLAUDE.md`. This feature earns at most two or three lines of *invariant*, not a description of how it works.
- **The device id is a trust boundary.** It arrives in a client cookie and ends up in a file path. Always validate through `SanitizeId`; **a blank or invalid id means "no marks", never a fallback filename.**
- **Keys must distinguish raw from converted** (`d:` vs `e:` prefix). A single `{itemId}` key would make downloading the raw ebook light up the EPUB action as already fetched.
- Run `dotnet format Inkshelf.sln --verify-no-changes` before the final commit; CI runs it over the whole solution.
- Run the suite with `dotnet test` from `/workspaces/inkshelf`. It should report **256 passed** before you start.

---

### Task 1: A device id in the settings cookie

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `DeviceSettings.Did` (`string`, `init`, default `""`); `DeviceSettings.Set` now **returns** the effective `DeviceSettings` (with any minted id) instead of `void`; `Serialize()` emits a trailing `&did=<id>`; `Read` parses it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`:

```csharp
    [Fact]
    public void Set_mints_a_device_id_when_absent_and_returns_it()
    {
        var ctx = new DefaultHttpContext();
        var written = DeviceSettings.Set(ctx.Response, new DeviceSettings(true, false, "de"));

        Assert.NotEqual("", written.Did);
        Assert.Equal(16, written.Did.Length);
        Assert.Matches("^[0-9a-f]{16}$", written.Did);
        // ...and it really went into the cookie, not just the return value.
        Assert.Contains($"did%3D{written.Did}", ctx.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Set_keeps_an_existing_device_id()
    {
        var ctx = new DefaultHttpContext();
        var written = DeviceSettings.Set(ctx.Response,
            new DeviceSettings(true, false, "") { Did = "abc123def456abcd" });
        Assert.Equal("abc123def456abcd", written.Did);
    }

    [Fact]
    public void Read_parses_the_device_id()
    {
        var s = DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&did=feedface00001111"));
        Assert.Equal("feedface00001111", s.Did);
    }

    [Fact]
    public void Read_sanitizes_a_hostile_device_id_to_empty()
    {
        // The id becomes part of a file path, so a traversal shape must collapse.
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&did=../../etc/passwd")).Did);
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&did=a/b")).Did);
    }

    [Fact]
    public void Two_mints_differ()
    {
        var a = DeviceSettings.Set(new DefaultHttpContext().Response, DeviceSettings.Default).Did;
        var b = DeviceSettings.Set(new DefaultHttpContext().Response, DeviceSettings.Default).Did;
        Assert.NotEqual(a, b);
    }
```

Then update the four assertions that pin the exact serialized string, since it grows a `did=` key. `Serialize()` called directly (not through `Set`) leaves the id empty, so the expected values are deterministic:

- `Serialize_emits_keyed_pairs` (both `Assert.Equal` lines): append `&did=` to each expected value.
- `Serialize_includes_fav`: expected becomes `"retina=1&gray=0&lang=de&fav=lib_abc&did="`.
- `Fav_is_sanitized_on_the_way_into_the_cookie` (the theory): expected format string becomes `$"retina=1&gray=0&lang=&fav={expected}&did="`.

Leave every other test alone. The `Assert.Contains` cookie-prefix assertions and all the `Read`-side tests keep passing untouched, because an absent `did` key reads as `""`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests"`
Expected: FAIL to compile — `'DeviceSettings' has no property 'Did'`. A compile error is a legitimate first red.

- [ ] **Step 3: Implement**

In `src/Inkshelf/Auth/DeviceSettings.cs`, add `using System.Security.Cryptography;` and, next to the existing `Fav` property:

```csharp
    // An opaque per-device handle, minted by Set (below) and used to key this
    // device's downloaded-file marks. An init property for the same reason as Fav:
    // the existing three-argument construction sites keep compiling.
    //
    // NOT a secret and NOT derived from anything the browser exposes — we mint it,
    // so no fingerprinting is involved and no privacy countermeasure applies to it.
    public string Did { get; init; } = "";
```

Extend `Serialize()` to emit the key (every key is always written):

```csharp
    public string Serialize() =>
        $"retina={(Retina ? 1 : 0)}&gray={(Grayscale ? 1 : 0)}"
        + $"&lang={SanitizeLang(Lang)}&fav={SanitizeId(Fav)}&did={SanitizeId(Did)}";
```

In `Read`, resolve it alongside `Fav` in the keyed branch — `SanitizeId` is what makes a hostile value collapse to `""`:

```csharp
            Did = q.TryGetValue("did", out var did) ? SanitizeId(did.ToString()) : "",
```

Change `Set` to mint and return. Note the mint happens **before** serializing, and the returned value is what was actually written:

```csharp
    // Returns the settings as written, including any id minted here — the download
    // endpoints need it to record a mark for a device seen for the first time.
    // Minting lives in Set so that no call site can write this cookie without an
    // id; every write path (POST /settings, POST /favorite, Index's stale-favorite
    // clear) therefore establishes one.
    public static DeviceSettings Set(HttpResponse res, DeviceSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Did)) settings = settings with { Did = NewDid() };
        var forceSecure = res.HttpContext.RequestServices?.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
        // ... existing Append(...) call unchanged, then the existing legacy delete ...
        return settings;
    }

    // 16 hex chars from a crypto RNG: unique enough for a household, and inside
    // SanitizeId's allowlist so it survives its own round trip.
    private static string NewDid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests"`
Expected: PASS.

Then: `dotnet test`
Expected: PASS, **261** tests (256 + 5). If anything outside `DeviceSettingsTests` fails, it is asserting on the exact cookie string — fix the assertion, never the source, and report it.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs
git commit -m "feat: mint a per-device id in the settings cookie"
```

---

### Task 2: The mark store

**Files:**
- Create: `src/Inkshelf/DownloadMarks.cs`
- Create: `tests/Inkshelf.Tests/DownloadMarksTests.cs`
- Modify: `src/Inkshelf/Program.cs`

**Interfaces:**
- Consumes: nothing (takes the device id as a plain string).
- Produces: `Inkshelf.DownloadMarks` — a singleton with `static string RawKey(string itemId, string? ino)`, `static string EpubKey(string itemId, string? ino)`, `HashSet<string> Read(string did)`, `void Add(string did, string key)`, `void Prune(TimeSpan maxAge)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/DownloadMarksTests.cs`:

```csharp
using Inkshelf;

namespace Inkshelf.Tests;

public class DownloadMarksTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string Did = "abc123def4560000";

    [Fact]
    public void Add_then_Read_round_trips()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        Assert.Contains(DownloadMarks.RawKey("item1", null), m.Read(Did));
    }

    [Fact]
    public void Read_of_an_unknown_device_is_empty()
    {
        using var dir = new TempDir();
        Assert.Empty(new DownloadMarks(dir.Path).Read("neverseen00000000"));
    }

    [Fact]
    public void Raw_and_epub_keys_are_distinct()
    {
        // THE important one. Both actions sit in the same row; a shared key would
        // make downloading the raw ebook light up the EPUB action as fetched.
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        var set = m.Read(Did);
        Assert.Contains(DownloadMarks.RawKey("item1", null), set);
        Assert.DoesNotContain(DownloadMarks.EpubKey("item1", null), set);
    }

    [Fact]
    public void Primary_and_per_file_keys_are_distinct()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", "14237"));
        var set = m.Read(Did);
        Assert.Contains(DownloadMarks.RawKey("item1", "14237"), set);
        Assert.DoesNotContain(DownloadMarks.RawKey("item1", null), set);
    }

    [Fact]
    public void Devices_do_not_see_each_other()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        Assert.Empty(m.Read("0000111122223333"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void A_hostile_or_blank_device_id_reads_empty_and_writes_nothing(string did)
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(did, DownloadMarks.RawKey("item1", null));

        Assert.Empty(m.Read(did));
        // Nothing was created anywhere under the marks dir, and no fallback file
        // pooled the request into a shared bucket.
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Adding_the_same_key_twice_does_not_duplicate_it()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        Assert.Single(m.Read(Did));
        Assert.Single(File.ReadAllLines(Path.Combine(dir.Path, Did)));
    }

    [Fact]
    public void Prune_deletes_stale_devices_and_keeps_fresh_ones()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add("staleaaaaaaaaaaa", DownloadMarks.RawKey("old", null));
        m.Add("freshbbbbbbbbbbb", DownloadMarks.RawKey("new", null));
        File.SetLastWriteTimeUtc(Path.Combine(dir.Path, "staleaaaaaaaaaaa"),
            DateTime.UtcNow - TimeSpan.FromDays(31));

        m.Prune(TimeSpan.FromDays(30));

        Assert.Empty(m.Read("staleaaaaaaaaaaa"));
        Assert.Single(m.Read("freshbbbbbbbbbbb"));
    }

    [Fact]
    public void Read_refreshes_the_files_timestamp_so_an_active_device_is_not_pruned()
    {
        // "Untouched" must mean "this device hasn't used the app", not "hasn't
        // downloaded" — otherwise browsing for a month prunes your marks mid-use.
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        var path = Path.Combine(dir.Path, Did);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(10));

        m.Read(Did);

        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Read_does_not_rewrite_a_recently_touched_file()
    {
        // Rate-limited so rendering doesn't amplify into a write per request.
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        var path = Path.Combine(dir.Path, Did);
        var stamp = DateTime.UtcNow - TimeSpan.FromHours(2);
        File.SetLastWriteTimeUtc(path, stamp);

        m.Read(Did);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DownloadMarksTests"`
Expected: FAIL to compile — `DownloadMarks` does not exist.

- [ ] **Step 3: Implement the store**

Create `src/Inkshelf/DownloadMarks.cs`:

```csharp
namespace Inkshelf;

// Which files each device has already downloaded, so a row can say "you already
// pulled this one onto this reader". One append-only file per device id, keys one
// per line. Deliberately NOT in the EPUB cache's own directory listing: this
// lives in a `marks/` subdirectory, and every cache operation globs
// non-recursively for *.epub / *.tmp, so eviction can never delete marks.
//
// A singleton over a directory path, mirroring EpubCache.
public sealed class DownloadMarks
{
    // Reading marks refreshes the file's timestamp so pruning tracks "this device
    // still uses the app" rather than "still downloads". Rate-limited so rendering
    // doesn't turn into a write per request.
    private static readonly TimeSpan TouchAfter = TimeSpan.FromDays(1);

    private readonly string _dir;
    public DownloadMarks(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    // The `d:`/`e:` prefix is load-bearing: an item's raw ebook and its converted
    // EPUB are different files reachable from the same row, so one shared key
    // would make fetching either light up both.
    public static string RawKey(string itemId, string? ino) => Key("d", itemId, ino);
    public static string EpubKey(string itemId, string? ino) => Key("e", itemId, ino);
    private static string Key(string kind, string itemId, string? ino) =>
        string.IsNullOrEmpty(ino) ? $"{kind}:{itemId}" : $"{kind}:{itemId}:{ino}";

    public HashSet<string> Read(string did)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (PathFor(did) is not { } path || !File.Exists(path)) return set;
        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (line.Length > 0) set.Add(line);
            var last = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - last > TouchAfter) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException) { }
        return set;
    }

    public void Add(string did, string key)
    {
        if (PathFor(did) is not { } path) return;
        try
        {
            if (Read(did).Contains(key)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return; }
            File.AppendAllText(path, key + Environment.NewLine);
        }
        catch (IOException) { }
    }

    public void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var f in new DirectoryInfo(_dir).GetFiles())
        {
            if (f.LastWriteTimeUtc >= cutoff) continue;
            try { f.Delete(); } catch (IOException) { }
        }
    }

    // The device id comes from a client cookie and becomes a FILE NAME, so it is a
    // trust boundary. A blank or invalid id means "no marks" — never a fallback
    // name, which would pool every malformed device into one shared bucket.
    private string? PathFor(string did) =>
        Auth.DeviceSettings.IsValidDid(did) ? Path.Combine(_dir, did) : null;
}
```

Note `PathFor` calls a validator that must be public. In `DeviceSettings`, expose one that reuses the existing private sanitizer rather than duplicating the allowlist:

```csharp
    // Public so DownloadMarks can gate a cookie-supplied id before it becomes a
    // file name. Reuses the one allowlist rather than restating it.
    public static bool IsValidDid(string? did) => !string.IsNullOrEmpty(did) && SanitizeId(did) == did;
```

- [ ] **Step 4: Register it**

In `src/Inkshelf/Program.cs`, immediately after the existing `AddSingleton(new EpubCache(cachePath))` line:

```csharp
// Marks live in a SUBDIRECTORY of the cache dir on purpose: every cache operation
// globs non-recursively for *.epub / *.tmp, so eviction can't reach them.
builder.Services.AddSingleton(new DownloadMarks(Path.Combine(cachePath, "marks")));
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~DownloadMarksTests"`
Expected: PASS.

Then: `dotnet test`
Expected: PASS, **274** tests (261 + 13; the traversal theory counts as four).

- [ ] **Step 6: Add the eviction-safety test**

This one belongs with the cache, not the store. Add to `tests/Inkshelf.Tests/EpubCacheTests.cs`:

```csharp
    [Fact]
    public void EnforceCap_does_not_touch_a_marks_subdirectory()
    {
        // Marks live under the cache dir. Every cache glob is non-recursive and
        // extension-scoped, which is the only reason that's safe — this test fails
        // if someone "simplifies" one of them to recurse.
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        var marks = Path.Combine(dir, "marks");
        Directory.CreateDirectory(marks);
        File.WriteAllText(Path.Combine(marks, "abc123def4560000"), "d:item1\n");
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(dir, $"item{i}-1-1-10x10.epub"), new byte[100]);

        cache.EnforceCap(150);   // forces eviction of at least one epub

        Assert.True(File.Exists(Path.Combine(marks, "abc123def4560000")));
        Assert.Empty(cache.ListVariants().Where(v => v.ItemId == "marks"));
    }
```

Run: `dotnet test`
Expected: PASS, 275 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add a per-device downloaded-file mark store"
```

---

### Task 3: Record marks from the download endpoints

**Files:**
- Modify: `src/Inkshelf/Endpoints/DownloadEndpoints.cs`
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs`
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `DownloadMarks` and `DeviceSettings.Set`'s return value from Tasks 1–2.
- Produces: no new API.

- [ ] **Step 1: Write the failing tests**

**Do not use `EndpointTests.CreateFactory` for these.** It points `ABS_URL` at `http://localhost:1` with no stub, so `/download/{id}` throws on the item-detail call, is caught, and returns `NotFound` — nothing would ever be marked. It also leaves `CachePath` unset, which defaults under the content root and would write marks into the repo.

Create `tests/Inkshelf.Tests/DownloadMarkEndpointTests.cs` with its own harness, modeled on `ConvertedRenderTests` (which already does both a stub and a temp cache):

```csharp
using System.Net;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

// The download endpoints must record a per-device mark. Needs a stubbed ABS (the
// endpoint 404s without one, so nothing would be marked) and a temp CachePath (so
// marks don't land in the repo).
public class DownloadMarkEndpointTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dlmark-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string ItemId = "item1";
    private const string Did = "abc123def4560000";

    // Expanded item detail with one primary epub file.
    private const string DetailJson = """
        {"media":{"metadata":{"title":"A Book","authorName":"An Author"},
         "ebookFile":{"ebookFormat":"epub","metadata":{"filename":"a.epub","size":10,"mtimeMs":20} } } }
        """;

    private static StubHandler MakeStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/items/{ItemId}") return StubHandler.Json(DetailJson);
        if (path == $"/api/items/{ItemId}/ebook")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("epub-bytes"u8.ToArray()) };
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static WebApplicationFactory<Program> CreateFactory(string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = MakeStub()));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    private static HttpRequestMessage Download(WebApplicationFactory<Program> factory, string? did)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/download/{ItemId}");
        var cookie = $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}";
        if (did is not null) cookie += $"; inkshelf_settings=retina=1&gray=0&lang=&fav=&did={did}";
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    [Fact]
    public async Task A_download_records_a_raw_mark_and_not_an_epub_mark()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.SendAsync(Download(factory, Did));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var marks = factory.Services.GetRequiredService<DownloadMarks>().Read(Did);
        Assert.Contains(DownloadMarks.RawKey(ItemId, null), marks);
        Assert.DoesNotContain(DownloadMarks.EpubKey(ItemId, null), marks);
    }

    [Fact]
    public async Task A_download_from_a_device_with_no_id_mints_one_and_marks_it()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.SendAsync(Download(factory, did: null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var setCookie = string.Join(" ", res.Headers.TryGetValues("Set-Cookie", out var v) ? v : Array.Empty<string>());
        var minted = System.Text.RegularExpressions.Regex.Match(setCookie, "did%3D([0-9a-f]{16})").Groups[1].Value;
        Assert.NotEqual("", minted);

        Assert.Contains(DownloadMarks.RawKey(ItemId, null),
            factory.Services.GetRequiredService<DownloadMarks>().Read(minted));
    }

    [Fact]
    public async Task A_failed_download_records_nothing()
    {
        // Unknown item → the endpoint 404s before serving, so there is no file to
        // have downloaded and nothing should be marked.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, "/download/nope");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang=&fav=&did={Did}");
        await client.SendAsync(req);

        Assert.Empty(factory.Services.GetRequiredService<DownloadMarks>().Read(Did));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DownloadMarkEndpointTests"`
Expected: FAIL — the marks come back empty, because nothing writes one yet. (`A_failed_download_records_nothing` will pass from the start; that's fine, it's a guard against marking on the 404 path.)

- [ ] **Step 3: Mark in the raw download endpoint**

`src/Inkshelf/Endpoints/DownloadEndpoints.cs` — add `HttpContext ctx` and `DownloadMarks marks` to the lambda's parameters, and record before streaming. Put the call immediately after the `file`/primary branch has established which file is being served, so the key matches what is actually sent:

```csharp
            // Mark BEFORE streaming: we can't tell a completed transfer from an
            // aborted one anyway (see the spec), and the marker is advisory.
            static string EnsureDid(HttpContext ctx)
            {
                var s = Auth.DeviceSettings.Read(ctx.Request);
                return string.IsNullOrEmpty(s.Did) ? Auth.DeviceSettings.Set(ctx.Response, s).Did : s.Did;
            }
```

For the per-file branch use `DownloadMarks.RawKey(id, file)`; for the primary branch use `DownloadMarks.RawKey(id, null)`. Record only on the paths that actually return a file — not on the `NotFound` paths.

- [ ] **Step 4: Mark in the convert endpoint**

`src/Inkshelf/Endpoints/ConvertEndpoints.cs` already receives `HttpContext httpContext`. Inject `DownloadMarks marks` and record **only where the cached EPUB is actually served** — the branch that returns `Results.File(...)`. Do not mark on `?warm=1`, `?status=1`, `?fresh=1`, the 202, or the redirect-when-not-ready: none of those hand the user a file. Use `DownloadMarks.EpubKey(id, file)`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: PASS, **278** tests (275 + 3).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: record a mark when a file is downloaded"
```

---

### Task 4: Render the marks, and retire the `EPUB ✓`

The largest task: three partials, three page models, and six existing assertions that use the checkmark as a discriminator.

**Files:**
- Modify: `src/Inkshelf/Pages/Support/ConvertActionModel.cs`, `ItemRowModel.cs`
- Modify: `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`, `_ItemRow.cshtml`
- Modify: `src/Inkshelf/Pages/Item.cshtml`, `Item.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`, `Converted.cshtml.cs`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`, `ItemRenderTests.cs`, `ConvertedRenderTests.cs`

**Interfaces:**
- Consumes: `DownloadMarks` from Task 2.
- Produces: `ConvertActionModel.Downloaded` (`bool`, defaulted so existing constructions compile), `ItemRowModel.RawDownloaded` (`bool`, defaulted), `ItemModel.FileRow.Downloaded` (`bool`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`, reusing its existing `TempDir`, `MakeStub`, `CreateFactory` and `LibraryRequest` helpers.

**Note on glyph encoding — this trips people up.** A literal `&#8595;` written in a `.cshtml` passes through as `&#8595;` (like the existing `&#10003;`), whereas a glyph returned from C# as a string gets HTML-encoded to `&#x2193;` (which is what `SortLinks.Arrow` produces in the sort bar). The arrow here is literal markup, so assert on `&#8595;`.

```csharp
    [Fact]
    public async Task A_downloaded_action_renders_the_arrow_and_an_unmarked_one_does_not()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        const string did = "abc123def4560000";
        var settings = $"retina=0&gray=0&lang=&fav=&did={did}";

        // Nothing marked yet.
        var plain = await (await client.SendAsync(LibraryRequest(factory, settings))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("&#8595;", plain);

        // Mark the RAW ebook for this device, then re-render.
        factory.Services.GetRequiredService<DownloadMarks>()
            .Add(did, DownloadMarks.RawKey(ItemId, null));
        var marked = await (await client.SendAsync(LibraryRequest(factory, settings))).Content.ReadAsStringAsync();
        Assert.Contains("&#8595;", marked);
    }

    [Fact]
    public async Task A_raw_mark_does_not_mark_the_epub_action()
    {
        // The row offers two different files. Marking one must not light up the
        // other — the whole reason keys carry a d:/e: discriminator.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        const string did = "abc123def4560000";
        // Cached EPUB so the convert action is in its Cached state.
        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H), "epub");
        factory.Services.GetRequiredService<DownloadMarks>()
            .Add(did, DownloadMarks.RawKey(ItemId, null));

        var html = await (await client.SendAsync(LibraryRequest(factory,
            $"retina=0&gray=0&lang=&fav=&did={did}"))).Content.ReadAsStringAsync();

        // Exactly one arrow: on Download, not on EPUB.
        Assert.Equal(1, Regex.Matches(html, "&#8595;").Count);
        Assert.DoesNotContain("EPUB &#8595;", html);
    }

    [Fact]
    public async Task The_cached_epub_action_no_longer_renders_a_checkmark()
    {
        // The label already says EPUB rather than Convert, so the checkmark was
        // decoration — and dropping it leaves the arrow as the only glyph in that
        // column. Asserting the exact old string rather than a bare "&#10003;",
        // because the read-state button legitimately renders one for "✓ Read".
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, W, H), "epub");

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("EPUB &#10003;", html);
        Assert.Contains(">EPUB", html);                                   // label survives
        Assert.DoesNotContain("data-warm", PrimaryConvertAnchor(html));   // and it IS the cached state
    }
```

Then deal with the six existing `EPUB &#10003;` assertions. **Most are redundant, not in need of replacement** — the tests already discriminate on `data-warm` right beside them via `PrimaryConvertAnchor`:

| File:line | Action |
|---|---|
| `ListingRenderTests:162` | **delete the line** — the `DoesNotContain("data-warm", PrimaryConvertAnchor(html))` on the next line already proves the cached state |
| `ListingRenderTests:190` | **delete** — same, `DoesNotContain("data-warm", …)` follows it |
| `ListingRenderTests:198` | **replace** with `Assert.Contains(">Convert</a>", PrimaryConvertAnchor(colourHtml))` — except that assertion already exists two lines down, so **delete** |
| `ListingRenderTests:244` | **replace** with `Assert.Contains(">EPUB", html)` — check whether a `data-warm` assertion already accompanies it; if so, delete instead |
| `ItemRenderTests:98` | **replace** with `Assert.Contains(">EPUB", html)` |
| `ConvertedRenderTests:274` | **replace** with `Assert.Contains(">EPUB", html)` |

Read each site before editing — the point is that the cached-vs-uncached discrimination must survive, not that every line gets mechanically rewritten. If deleting a line would leave a test with no cached-state assertion at all, replace it with `>EPUB` instead.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ListingRenderTests"`
Expected: FAIL to compile first (`DownloadMarks` isn't referenced by that file yet — add the `using Inkshelf;`), then FAIL on assertions: the arrow tests find no `&#8595;`, and `The_cached_epub_action_no_longer_renders_a_checkmark` fails on `DoesNotContain("EPUB &#10003;")` because the ✓ is still rendered.

- [ ] **Step 3: Add the model fields**

`ConvertActionModel` gains a trailing defaulted parameter so its five existing construction sites keep compiling:

```csharp
public record ConvertActionModel(string Id, string? FileIno, ConvertRowState State, string ReturnUrl,
    bool Downloaded = false);
```

`ItemRowModel` likewise:

```csharp
    bool Read = false,
    bool RawDownloaded = false);
```

`ItemModel.FileRow` gains `bool Downloaded = false` as a trailing parameter.

- [ ] **Step 4: Retire the checkmark and render the arrow**

In `_ConvertAction.cshtml`, the `Cached` case drops `&#10003;` and gains the arrow. Write the arrow as **literal markup inside a `<text>` block**, not as a C# string — a returned string gets HTML-encoded to `&#x2193;`, while literal markup stays `&#8595;`, which is what the tests assert and what matches the existing `&#10003;` precedent:

```razor
        case ConvertRowState.Cached:
            <a href="@baseHref" title="@L["Already converted — downloads right away"]">EPUB@if (Model.Downloaded) { <text> &#8595;</text> }</a>
            break;
```

**Only the `Cached` state gets an arrow.** You can only have downloaded an EPUB that existed, and after a `?fresh=1` regen the state is Converting/Convert — an arrow on a "Converting…" label would be nonsense. The mark persisting through a regen is a documented, accepted limitation.

Keep the `title` attribute; it still explains that a cached EPUB downloads immediately.

In `_ItemRow.cshtml`, the raw link becomes:

```razor
            <a href="/download/@item.Id">@L["Download"]@if (Model.RawDownloaded) { <text> &#8595;</text> }</a>
```

In `Item.cshtml`, the file row's raw link gains the same treatment using `f.Downloaded`.

- [ ] **Step 5: Load the marks in the three page models**

Each page reads the device id from the settings cookie, loads the set once, and passes the per-action booleans. Inject `DownloadMarks` into `LibraryModel`, `ConvertedModel` and `ItemModel` constructors.

```csharp
        var did = DeviceSettings.Read(Request).Did;      // "" for a device that has never downloaded
        var marks = did.Length == 0 ? new HashSet<string>() : _marks.Read(did);
```

Then for each row, `RawDownloaded = marks.Contains(DownloadMarks.RawKey(item.Id, null))` and the convert action's `Downloaded = marks.Contains(DownloadMarks.EpubKey(item.Id, null))`. On the item detail page use each file's ino for both.

Do **not** call `_marks.Read` per row — once per render, as above.

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: PASS, **281** tests — 278 plus the three added to `ListingRenderTests`. The six edited `EPUB &#10003;` assertions change lines, not test counts.

Then: `dotnet format Inkshelf.sln --verify-no-changes`
Expected: clean.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: show which files this device already downloaded"
```

---

### Task 5: Pruning, docs, and the device pass

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs`
- Modify: `tools/uicheck/Program.cs`
- Modify: `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`

- [ ] **Step 1: Sweep stale devices at startup**

`ConvertWorker.ExecuteAsync` already calls `_cache.SweepTemp()` before draining. Inject `DownloadMarks` and prune alongside it:

```csharp
        _cache.SweepTemp();          // clear orphan .tmp from a prior crash/shutdown
        _marks.Prune(TimeSpan.FromDays(30));   // forget devices that stopped visiting
```

`DownloadMarks.Add` already prunes opportunistically if you implemented it that way in Task 2; if not, leave writes alone — startup is sufficient for a container that restarts on deploy, and the roadmap entry should say so.

- [ ] **Step 2: Extend the headless pass**

In `tools/uicheck/Program.cs`, extend the existing `/converted` revisit at the end of the authed block — it already waits for `nav.sortbar`, which means a conversion has landed. Assert the **un-downloaded** state there, which is deterministic:

```csharp
        // The retired checkmark must be gone and no download arrow present yet:
        // nothing has been downloaded in this run.
        var convertedHtml = await page.ContentAsync();
        if (convertedHtml.Contains("EPUB &#10003;", StringComparison.Ordinal))
            failures.Add("converted-sorted-de: the retired EPUB checkmark is still rendered");
```

**Do not try to drive an actual file download through Playwright here.** A download navigation in headless Chromium either triggers download handling or replaces the page, both of which would wreck the rest of the authed flow — and the marked state is already covered precisely by the render tests in Task 4. Assert the negative here and rely on those for the positive.

- [ ] **Step 3: Run the pass and LOOK at the screenshot**

Run: `tools/uicheck/run.sh`
Expected: PASS. Then open `tools/uicheck/shots/library-de.png` and `converted-sorted-de.png`.

**Check specifically:** the actions column with `Herunterladen ↓` and `EPUB ↓` — does it fit without wrapping at the 758px viewport? Retiring the `✓` shortens that column, so this should be neutral-or-better than today, but confirm rather than assume. If it wraps, **report it and stop** — do not adjust CSS. That is the owner's call.

- [ ] **Step 4: Update the docs**

`docs/ROADMAP.md` — delete the **Mark files as already downloaded (per device)** bullet from `## Browsing & reading` and add to the top of `## Done`:

```markdown
- **Downloaded-file marks** — each download action shows whether *this device*
  already fetched *that file* (`↓`), so working through a batch doesn't mean
  re-downloading or skipping one. Keyed on a device id minted into the settings
  cookie, with marks in a server-side file per device; deliberately not keyed on
  the render target (a variant key, not an identity) or the ABS user (answers the
  wrong question). The old `EPUB ✓` went with it: the label already says `EPUB`
  rather than `Convert`, so the checkmark was decoration, and dropping it leaves
  `↓` as the only glyph in that column. Marks for devices that stop visiting are
  pruned after 30 days.
```

`docs/ARCHITECTURE.md` — **at most two lines, as invariants, not description.** Add to the "Per-device state" group:

```markdown
- **The device id is a trust boundary.** It arrives in a cookie and becomes a
  filename, so it goes through `SanitizeId`; blank or invalid means "no marks",
  never a fallback name that would pool devices into one bucket.
- **Download marks live in a `marks/` subdirectory of the EPUB cache.** That is
  safe because every cache glob is extension-scoped (`*.epub`, `*.tmp`) and a
  device id can't contain a dot — don't widen one of those patterns.
```

Do not describe the key scheme, the mint paths, or the arrow. Those are code comments and spec material.

- [ ] **Step 5: Final run and commit**

Run: `dotnet test` and `dotnet format Inkshelf.sln --verify-no-changes`
Expected: both clean.

```bash
git add -A
git commit -m "docs: record the downloaded-file marks"
```

---

## Done criteria

- `dotnet test` reports **281** passing; `dotnet format --verify-no-changes` clean; `tools/uicheck/run.sh` PASS.
- Downloading an item's raw ebook marks the Download action and **not** the EPUB action, and vice versa.
- A second device sees none of the first device's marks.
- A traversal-shaped `did` yields no marks and creates no file.
- `EnforceCap` leaves `marks/` intact.
- No rendered convert action contains `&#10003;`.
- A marks file untouched for 30 days is pruned; browsing (not just downloading) counts as touching it.
