# Update check implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show `Inkshelf v1.0.0 (v1.0.1 available)` on the libraries page when a newer GitHub release exists.

**Architecture:** One singleton, `UpdateCheck`, holds the newer version or null. The libraries page pokes it and reads the cached value; a stale cache starts a background fetch that the render never awaits. Spec: `docs/superpowers/specs/2026-09-28-update-check-design.md`.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, `System.Text.Json`, `IHttpClientFactory`, xUnit.

## Global Constraints

- No em dashes (U+2014) and no en dashes (U+2013) anywhere, including code, comments, commit messages and docs. Plain hyphen only.
- Config key: `UPDATE_CHECK`, default on, the string `false` (case-insensitive) disables. Same parse shape as `DIAG_ENABLED` in `Program.cs`.
- Endpoint: `https://api.github.com/repos/thomaslazar/inkshelf/releases/latest`, field `tag_name`.
- Intervals: 24 hours after success, 1 hour after failure.
- Rendered copy: `Inkshelf v1.0.0 (v1.0.1 available) - User: alice`. Locale key is `"({0} available)"`; German `"({0} verfügbar)"`.
- The check must never be awaited on the request path.
- No new NuGet package.

---

### Task 1: Version comparison

The only real logic: normalising two version strings and deciding whether the remote one is newer. Written and tested before anything touches HTTP or DI.

**Files:**
- Create: `src/Inkshelf/UpdateCheck.cs`
- Test: `tests/Inkshelf.Tests/UpdateCheckTests.cs`

**Interfaces:**
- Consumes: `Inkshelf.AppVersion.Current` (existing, `string`).
- Produces: `internal static string? UpdateCheck.NewerThan(string local, string remote)` - the remote version without its `v` prefix when it parses and is strictly newer, else null.

- [ ] **Step 1: Write the failing test**

Create `tests/Inkshelf.Tests/UpdateCheckTests.cs`:

```csharp
namespace Inkshelf.Tests;

public class UpdateCheckTests
{
    [Theory]
    // A newer release is the only case that renders anything.
    [InlineData("1.0.0", "v1.0.1", "1.0.1")]
    [InlineData("1.0.0", "1.0.1", "1.0.1")]
    // A PR image is stamped "<version>+pr-34.a1b2c3d"; it compares as its base version.
    [InlineData("1.0.0+pr-34.a1b2c3d", "v1.0.1", "1.0.1")]
    // Up to date, and ahead of the latest release (a local build), both stay silent.
    [InlineData("1.0.0", "v1.0.0", null)]
    [InlineData("1.1.0", "v1.0.1", null)]
    // Anything that will not parse shows nothing rather than guessing.
    [InlineData("1.0.0", "nightly", null)]
    [InlineData("1.0.0", "", null)]
    [InlineData("1.0.0", "v1.1.0-rc1", null)]
    [InlineData("not-a-version", "v1.0.1", null)]
    public void NewerThan_reports_only_a_parseable_newer_release(string local, string remote, string? expected)
        => Assert.Equal(expected, UpdateCheck.NewerThan(local, remote));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckTests`
Expected: build failure, `error CS0103: The name 'UpdateCheck' does not exist in the current context`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Inkshelf/UpdateCheck.cs`:

```csharp
namespace Inkshelf;

public sealed class UpdateCheck
{
    // Null unless the remote tag parses and is strictly newer than ours. Both
    // sides drop a leading "v" and anything from the first "+": the Docker build
    // stamps non-release images "<version>+pr-34.a1b2c3d", and such a build of
    // 1.0.0 should still be told about 1.0.1.
    internal static string? NewerThan(string local, string remote)
    {
        static Version? Parse(string s)
        {
            s = s.TrimStart('v', 'V');
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            return Version.TryParse(s, out var v) ? v : null;
        }

        var mine = Parse(local);
        var theirs = Parse(remote);
        return mine is not null && theirs is not null && theirs > mine ? theirs.ToString() : null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckTests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/UpdateCheck.cs tests/Inkshelf.Tests/UpdateCheckTests.cs
git commit -m "feat: compare the running version against a release tag"
```

---

### Task 2: The singleton, the fetch and the config flag

Wraps Task 1's comparison in the cached, non-blocking check and registers it.

**Files:**
- Modify: `src/Inkshelf/UpdateCheck.cs` (add the instance members around Task 1's static method)
- Modify: `src/Inkshelf/AbsOptions.cs` (new `UpdateCheck` bool, and the config-key list in the file's header comment)
- Modify: `src/Inkshelf/Program.cs` (option binding near `DiagEnabled`; client + singleton registration near the other `AddHttpClient` calls)

**Interfaces:**
- Consumes: `UpdateCheck.NewerThan` (Task 1), `AbsOptions` (existing singleton), `AppVersion.Current`.
- Produces: `UpdateCheck` singleton with `public string? Newer { get; internal set; }` and `public void Poke()`. Named HTTP client `"github"`.

- [ ] **Step 1: Add the option**

In `src/Inkshelf/AbsOptions.cs`, add to the class (below `DiagEnabled`):

```csharp
    // Whether to check GitHub daily for a newer release and say so on the
    // libraries page. Default true; false means no outbound request is ever made.
    public bool UpdateCheck { get; set; } = true;
```

And add `UPDATE_CHECK` to the alphabetical config-key list in the file's header comment, between `TRUSTED_PROXY` and the closing period:

```
// ABS_URL (required), ABS_PUBLIC_URL, CachePath, DataProtectionKeysPath, DIAG_ENABLED, FORCE_SECURE_COOKIES, LOCALES_PATH, LOCALES_OVERRIDE_PATH, OIDC_ENABLED, OIDC_PROVIDER_NAME, TRUSTED_PROXY, UPDATE_CHECK.
```

- [ ] **Step 2: Bind it in Program.cs**

In the `absOptions` initializer, directly under the `DiagEnabled` line, add (same "true unless the string is literally false" shape):

```csharp
    UpdateCheck = !string.Equals(builder.Configuration["UPDATE_CHECK"], "false", StringComparison.OrdinalIgnoreCase),
```

- [ ] **Step 3: Write the rest of the class**

Replace the whole of `src/Inkshelf/UpdateCheck.cs` with (`NewerThan` is unchanged from Task 1):

```csharp
using System.Text.Json;

namespace Inkshelf;

// Daily check for a newer GitHub release, shown next to the version on the
// libraries page. Poke() returns immediately and the render uses whatever was
// last fetched: a synchronous call would put github.com in the critical path of
// a page load on an e-reader, and one load a day would appear to hang.
public sealed class UpdateCheck
{
    private const string LatestUrl = "https://api.github.com/repos/thomaslazar/inkshelf/releases/latest";
    private static readonly TimeSpan AfterSuccess = TimeSpan.FromHours(24);
    // Shorter, but not short: a deployment with no outbound network must not
    // re-attempt on every render.
    private static readonly TimeSpan AfterFailure = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _clients;
    private readonly bool _enabled;
    private readonly ILogger<UpdateCheck> _log;
    private readonly Lock _gate = new();
    private DateTimeOffset _due = DateTimeOffset.MinValue;

    public UpdateCheck(IHttpClientFactory clients, AbsOptions options, ILogger<UpdateCheck> log)
    {
        _clients = clients;
        _enabled = options.UpdateCheck;
        _log = log;
    }

    // The newer release's version, or null. Null covers disabled, not yet
    // checked, up to date, fetch failed and unparseable alike - the page has one
    // thing to render, so no failure path needs its own UI.
    public string? Newer { get; internal set; }

    public void Poke()
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _due) return;
            // Claim the slot before starting, so concurrent renders fetch once.
            _due = DateTimeOffset.UtcNow + AfterSuccess;
        }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var json = await _clients.CreateClient("github").GetStringAsync(LatestUrl);
            using var doc = JsonDocument.Parse(json);
            Newer = NewerThan(AppVersion.Current, doc.RootElement.GetProperty("tag_name").GetString() ?? "");
        }
        catch (Exception ex)
        {
            // Nothing here is worth a user-visible error or a warning in the log:
            // no network, rate limiting and a shape change all mean "no hint".
            lock (_gate) _due = DateTimeOffset.UtcNow + AfterFailure;
            _log.LogDebug(ex, "Update check failed.");
        }
    }

    // Null unless the remote tag parses and is strictly newer than ours. Both
    // sides drop a leading "v" and anything from the first "+": the Docker build
    // stamps non-release images "<version>+pr-34.a1b2c3d", and such a build of
    // 1.0.0 should still be told about 1.0.1.
    internal static string? NewerThan(string local, string remote)
    {
        static Version? Parse(string s)
        {
            s = s.TrimStart('v', 'V');
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            return Version.TryParse(s, out var v) ? v : null;
        }

        var mine = Parse(local);
        var theirs = Parse(remote);
        return mine is not null && theirs is not null && theirs > mine ? theirs.ToString() : null;
    }
}
```

- [ ] **Step 4: Register it in Program.cs**

Immediately after the `AddHttpClient<AbsDownloadClient>(ConfigureAbs);` line, add:

```csharp
// Its own client, deliberately not one of the ABS ones: those carry
// AbsAuthHandler or an ABS BaseAddress, and neither belongs on a third-party
// call. The User-Agent is reused because GitHub rejects requests without one.
builder.Services.AddHttpClient("github", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd(absUserAgent);
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<UpdateCheck>();
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Inkshelf`
Expected: `Build succeeded`, 0 warnings. If `Lock` is not found, the file is missing `using System.Threading;` - add it (`ImplicitUsings` covers it in .NET 10, so this should not happen).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all green. Nothing calls `Poke()` yet, so no test makes an outbound request.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/UpdateCheck.cs src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs
git commit -m "feat: check github for a newer release once a day"
```

---

### Task 3: Render it on the libraries page

**Files:**
- Modify: `tests/Inkshelf.Tests/IndexRenderTests.cs` (test-host setting + two new tests)
- Modify: `tests/Inkshelf.Tests/EndpointTests.cs` (test-host setting only)
- Modify: `src/Inkshelf/Pages/Index.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Index.cshtml`
- Modify: `src/Inkshelf/locales/de.json`

**Interfaces:**
- Consumes: the `UpdateCheck` singleton from Task 2 (`Poke()`, `Newer`).
- Produces: `IndexModel.Newer` (`string?`), rendered as `(v1.0.1 available)`.

- [ ] **Step 1: Keep the test host off the network**

Both test files boot the app and request `/`. Once the page pokes the check, an enabled default would make the suite talk to github.com. In `tests/Inkshelf.Tests/IndexRenderTests.cs` and `tests/Inkshelf.Tests/EndpointTests.cs`, add this line beside every existing `b.UseSetting("ABS_URL", ...)`:

```csharp
            b.UseSetting("UPDATE_CHECK", "false");
```

Then confirm nothing else boots the app and fetches `/`:

Run: `grep -rn 'Get, "/"\|GetAsync("/")' tests/Inkshelf.Tests/`
Expected: hits only in `IndexRenderTests.cs` and `EndpointTests.cs`. If another file appears, add the same setting there.

- [ ] **Step 2: Write the failing tests**

Add to `IndexRenderTests`. `GetIndexHtml` builds its own factory, so the value has to be planted from inside it - add this overload next to the existing helpers:

```csharp
    // Plants a result rather than driving the fetch: the check itself is tested
    // in UpdateCheckTests, and this asserts only what the page does with it.
    private static async Task<string> GetIndexHtmlWithUpdate(string? newer, string lang)
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("UPDATE_CHECK", "false");
            b.UseSetting("CachePath", cacheDir.Path);
            b.UseSetting("DataProtectionKeysPath", keysDir.Path);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = MakeStub()));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
                services.AddSingleton<IAntiforgery, SilentAntiforgery>();
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        factory.Services.GetRequiredService<UpdateCheck>().Newer = newer;

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect("acc\nref\nalice"))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang={lang}&fav=");

        return await (await client.SendAsync(req)).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_newer_release_is_named_beside_the_version()
    {
        var html = await GetIndexHtmlWithUpdate("1.0.1", lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current} (v1.0.1 available)", html);
    }

    [Fact]
    public async Task The_update_hint_is_localised()
    {
        var html = await GetIndexHtmlWithUpdate("1.0.1", lang: "de");

        Assert.Contains("(v1.0.1 verfügbar)", html);
        Assert.DoesNotContain("available", html);
    }

    [Fact]
    public async Task No_known_update_leaves_the_version_line_bare()
    {
        // The null case is what every deployment shows most of the time, and it
        // must not leave an empty pair of parentheses behind.
        var html = await GetIndexHtmlWithUpdate(null, lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current} - User: alice", html);
        Assert.DoesNotContain("available", html);
        Assert.DoesNotContain($"Inkshelf v{AppVersion.Current} (", html);
    }
```

This needs one more using at the top of the file:

```csharp
using Inkshelf;
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~IndexRenderTests`
Expected: FAIL. `A_newer_release_is_named_beside_the_version` fails on the missing `(v1.0.1 available)`.

- [ ] **Step 4: Expose it on the page model**

In `src/Inkshelf/Pages/Index.cshtml.cs`, take the singleton and poke it. The constructor and field:

```csharp
    private readonly AbsApiClient _api;
    private readonly TokenStore _tokens;
    private readonly UpdateCheck _updates;
    public IndexModel(AbsApiClient api, TokenStore tokens, UpdateCheck updates) { _api = api; _tokens = tokens; _updates = updates; }
```

The property, below `Version`:

```csharp
    // A newer release, when the last check found one. Poking never blocks: this
    // render shows the previous result, not the one it may be starting.
    public string? Newer => _updates.Newer;
```

And as the first line of `OnGetAsync`, before the libraries call:

```csharp
        _updates.Poke();
```

- [ ] **Step 5: Render it**

In `src/Inkshelf/Pages/Index.cshtml`, replace the last line with:

```razor
<p class="app-version"><small>Inkshelf v@(Model.Version)@if (Model.Newer is not null) {<text> @L["({0} available)", "v" + Model.Newer]</text>}@if (Model.Username.Length > 0) {<text> - @L["User: {0}", Model.Username]</text>}</small></p>
```

- [ ] **Step 6: Translate it**

In `src/Inkshelf/locales/de.json`, add beside the other version-line key:

```json
  "({0} available)": "({0} verfügbar)",
```

English needs no entry: the catalog is keyed by the English source string.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: all green, including the four pre-existing `IndexRenderTests` cases.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Pages/Index.cshtml src/Inkshelf/Pages/Index.cshtml.cs src/Inkshelf/locales/de.json tests/Inkshelf.Tests/IndexRenderTests.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: name a newer release on the libraries page"
```

---

### Task 4: Docs and the browser pass

**Files:**
- Modify: `README.md` (configuration table)
- Modify: `docs/ARCHITECTURE.md` (code map line, one invariant)
- Modify: `docs/ROADMAP.md` (`## Done`)

Not `CHANGELOG.md`: it is written by the release skill only.

- [ ] **Step 1: README row**

In the configuration table, after the `DIAG_ENABLED` row:

```markdown
| `UPDATE_CHECK`            | `true`               | Check GitHub once a day for a newer Inkshelf release and name it beside the version on the libraries page. Set `false` to make no outbound request at all. |
```

- [ ] **Step 2: ARCHITECTURE**

One line in the code map, directly under the `AbsOptions.cs` line:

```
  UpdateCheck.cs        Cached daily GitHub release check (singleton, off the request path).
```

And one bullet under the **ABS access** invariants, which is where the other "which client gets which handler" rules live:

```markdown
- The GitHub update check uses its own named client and is never awaited during
  a render. Giving it an ABS typed client would send the session bearer to a
  third party; awaiting it would put github.com in the critical path of a page
  load on a device whose browser is already slow.
```

Nothing else: a feature that fits the existing structure does not get an entry.

- [ ] **Step 3: ROADMAP**

Add at the top of the `## Done` list:

```markdown
- **Update check** - a daily, opt-out (`UPDATE_CHECK`) check against the GitHub
  releases API; the libraries page shows `(v1.0.1 available)` beside the version
  when one is newer. Never on the request path, and silent on any failure.
```

If the item also appears elsewhere in `ROADMAP.md` as pending work, remove it from there.

- [ ] **Step 4: Verify**

Run: `dotnet test`
Expected: all green.

Run: `grep -rnP '[\x{2013}\x{2014}]' README.md docs/ARCHITECTURE.md docs/ROADMAP.md src/Inkshelf/UpdateCheck.cs src/Inkshelf/Pages/Index.cshtml src/Inkshelf/locales/de.json`
Expected: no output.

Run: `dotnet format --verify-no-changes`
Expected: no changes (CI runs it).

Run: `tools/uicheck/run.sh`
Expected: exit 0. Then look at `tools/uicheck/shots/` - the libraries page in both languages must show the bare version line, unchanged, since the seeded stack has no newer release to report.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "docs: document the update check"
```
