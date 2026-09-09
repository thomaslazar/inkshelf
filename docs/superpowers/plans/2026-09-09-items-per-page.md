# Items Per Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-device "Items per page" setting, default 10, that drives both browsable lists, plus a pager on the converted page.

**Architecture:** The setting is one more field in the existing encrypted-cookie wire format, read the same way as `Scale`. The library listing already pages server-side through the ABS API, so it only swaps a constant for the setting. The converted page pages in memory, which requires reordering its work so the slice happens before rows are built. `_Pager.cshtml` is retyped from `LibraryModel` to a two-member interface both page models implement.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit, Playwright (C#) for the browser pass. No client JavaScript in this feature.

## Context for a fresh session

Inkshelf is a thin, server-rendered Razor Pages client for the Audiobookshelf (ABS) API, run as a sidecar container. It targets very old e-reader browsers, so pages are plain HTML with `<form>` and `<a>` only and near-zero JavaScript. It is public open source with external users, and the maintainer's own deployment is shared with family.

**Read `docs/superpowers/specs/2026-09-09-items-per-page-design.md` before starting.** In particular it records why the converted page's work has to be reordered rather than just sliced at render time, and one trap about the warning marker that will cause a subtle bug if ignored.

## Global Constraints

- **No em dashes (U+2014) and no en dashes (U+2013) anywhere**: not in code, comments, docs, the German strings, or commit messages. Use a plain hyphen, a comma, or two sentences. Absolute, with no exception for German where an en dash would be conventional. Existing files legitimately contain U+00D7, U+2192, U+00F7, U+2713 and the ellipsis U+2026; only U+2013 and U+2014 are banned. Verify before each commit with `git diff --cached | grep -nP '[\x{2013}\x{2014}]'` returning nothing.
- **The English label in a Razor `L[...]` lookup IS the key in `src/Inkshelf/locales/de.json`.** Any drift means German silently falls back to English. There is no `locales/en.json`; English is the key itself.
- **UI copy names what the user sees**, not the mechanism.
- **The accepted range is 5 to 50 inclusive; the default is 10.** Out of range means the default, NOT a clamp. This matches `SanitizeScale`'s documented rule: "20 is not a request for 50."
- **The warning marker must NOT be added to `DeviceSettings.Keys`.** That array is "the keys Serialize writes, and nothing else" and it decides whether a query counts as a settings payload at all. `range` and `scalerange` are deliberately absent from it.
- **No new dependencies. No client JavaScript.**
- Conventional Commits: `type: subject`, imperative, lowercase, no period, max ~72 chars. No `Co-Authored-By` and no "Generated with Claude Code" lines.
- Per-task commits on branch `feat/items-per-page` are pre-authorized by the user for this plan.
- `dotnet test` from the repo root must be green and `dotnet format Inkshelf.sln --verify-no-changes` clean before each commit.
- Do NOT start a dev server during implementation; it holds a file lock on `src/Inkshelf/bin` and breaks `dotnet test`. `tools/uicheck/run.sh` starts and stops its own and is fine.
- Do NOT touch `CHANGELOG.md`; it belongs to the release process.
- Do NOT add a per-feature entry to `docs/ARCHITECTURE.md`; `CLAUDE.md` is explicit that this is a smell.
- The baseline is **517 passing tests**. `dotnet test` takes about 40 seconds including the build.

## File Structure

- `src/Inkshelf/Auth/DeviceSettings.cs`: the new `PerPage` property, `MinPerPage`/`MaxPerPage` constants, `SanitizePerPage`, cookie key `ipp` in `Serialize`, `Keys` and `Parse`.
- `src/Inkshelf/Endpoints/SettingsEndpoints.cs`: reads the `perpage` form field and appends the `pprange=1` warning marker.
- `src/Inkshelf/Pages/Settings.cshtml`: the number input.
- `src/Inkshelf/Pages/Settings.cshtml.cs`: the `PerPageWarning` flag.
- `src/Inkshelf/locales/de.json`: two German strings.
- `src/Inkshelf/Pages/Support/IPagedListing.cs`: new, the two-member interface.
- `src/Inkshelf/Pages/Shared/_Pager.cshtml`: retyped to the interface.
- `src/Inkshelf/Pages/Library.cshtml.cs`: page size from the setting; implements the interface.
- `src/Inkshelf/Pages/Converted.cshtml.cs`: reordered work, slice, clamp; implements the interface.
- `src/Inkshelf/Pages/Converted.cshtml`: renders the pager.
- `src/Inkshelf/DownloadTickets.cs`: a one-line `LiveCount` so a test can pin that off-page rows mint nothing.
- `tools/uicheck/Program.cs`: browser coverage of the converted pager.

---

### Task 1: The setting

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Inkshelf/Pages/Settings.cshtml`
- Modify: `src/Inkshelf/Pages/Settings.cshtml.cs`
- Modify: `src/Inkshelf/locales/de.json`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `DeviceSettings.PerPage` (`int`), `DeviceSettings.MinPerPage` (`const int = 5`), `DeviceSettings.MaxPerPage` (`const int = 50`), `DeviceSettings.SanitizePerPage(int)` returning `int`. Tasks 2 and 3 read `DeviceSettings.Read(Request).PerPage`.

Mirror the existing `Scale` setting at every call site. Read `Scale` first and follow it: property declaration, `Serialize`, `Keys`, `Parse`, the endpoint's form read, the endpoint's warning flag, the page model's warning property, and the Razor input.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void PerPage_defaults_to_ten()
    {
        Assert.Equal(10, DeviceSettings.Default.PerPage);
    }

    [Fact]
    public void PerPage_round_trips_through_the_wire_format()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("retina=1&gray=0&lang=&fav=&ipp=25"));
        Assert.Equal(25, DeviceSettings.FromQuery(q)!.PerPage);
    }

    [Fact]
    public void PerPage_absent_from_an_older_cookie_reads_as_the_default()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("retina=1&gray=0&lang=&fav="));
        Assert.Equal(10, DeviceSettings.FromQuery(q)!.PerPage);
    }

    [Fact]
    public void PerPage_alone_is_enough_to_recognise_a_settings_query()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("ipp=15"));
        Assert.Equal(15, DeviceSettings.FromQuery(q)!.PerPage);
    }

    // Out of range on EITHER side falls back to the default rather than clamping
    // to the bound, the same rule SanitizeScale documents: a typo'd 500 is a
    // mistake, not a request for 50.
    [Theory]
    [InlineData(4, 10)]
    [InlineData(51, 10)]
    [InlineData(0, 10)]
    [InlineData(-3, 10)]
    [InlineData(500, 10)]
    [InlineData(5, 5)]
    [InlineData(50, 50)]
    [InlineData(10, 10)]
    public void SanitizePerPage_keeps_the_range_and_defaults_outside_it(int given, int expected)
    {
        Assert.Equal(expected, DeviceSettings.SanitizePerPage(given));
    }

    // A garbage value in the cookie must not reach the page size: zero or
    // negative would mean a division by zero in Pager.TotalPages, which guards
    // it, but the sanitizer must not lean on that guard.
    [Fact]
    public void PerPage_garbage_in_the_cookie_reads_as_the_default()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("retina=1&gray=0&lang=&fav=&ipp=abc"));
        Assert.Equal(10, DeviceSettings.FromQuery(q)!.PerPage);
    }
```

Add to `tests/Inkshelf.Tests/EndpointTests.cs`, inside the existing class:

```csharp
    [Fact]
    public async Task Saving_items_per_page_records_it_and_out_of_range_warns()
    {
        using var keysDir = new TempDir();
        using var factory = CreateFactory(keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var ok = await client.PostAsync("/settings",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["perpage"] = "25" }));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("ipp=25", ok.Headers.Location!.OriginalString);
        Assert.DoesNotContain("pprange=1", ok.Headers.Location!.OriginalString);

        // Out of range: stored as the default AND flagged, because silently
        // reverting looks like the field ignoring you.
        var bad = await client.PostAsync("/settings",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["perpage"] = "500" }));
        Assert.Contains("ipp=10", bad.Headers.Location!.OriginalString);
        Assert.Contains("pprange=1", bad.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Settings_page_renders_the_items_per_page_input()
    {
        using var keysDir = new TempDir();
        using var factory = CreateFactory(keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();

        Assert.Contains("name=\"perpage\"", html);
    }

    // The marker is NOT a settings key. If it were added to DeviceSettings.Keys,
    // a redirect carrying only the warning would parse as a settings payload and
    // could overwrite real settings with defaults.
    [Fact]
    public void The_per_page_warning_marker_is_not_a_settings_key()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("pprange=1"));
        Assert.Null(DeviceSettings.FromQuery(q));
    }
```

If `EndpointTests.cs` lacks a `CreateFactory(string keysPath)` or `TempDir` with these exact shapes, use whatever the neighbouring settings tests in that file already use rather than inventing one, and say so in your report. There are existing settings POST tests in that file to copy from; the two new ones must follow them.

`QueryCollection` and `QueryHelpers` need `using Microsoft.AspNetCore.Http;` and `using Microsoft.AspNetCore.WebUtilities;`. `DeviceSettingsTests.cs` already has them; add them to `EndpointTests.cs` if the marker test needs them.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PerPage|FullyQualifiedName~items_per_page|FullyQualifiedName~per_page_warning_marker"`

Expected: compile errors, since `PerPage` and `SanitizePerPage` do not exist yet. That IS the red state for this task; do not "fix" it by stubbing them before writing the implementation in Step 3.

- [ ] **Step 3: Add the property and the sanitizer**

In `src/Inkshelf/Auth/DeviceSettings.cs`, next to `MinScale` (around line 41), add the bounds:

```csharp
    // Items per page for the browsable lists. A free number with bounds rather
    // than a menu, matching the page scale's control.
    public const int MinPerPage = 5;
    public const int MaxPerPage = 50;
```

Add the property immediately after the `ReturnAfterDownload` property. It must be `{ get; init; }` with an initializer, NOT a constructor parameter: `Default` is built by a three-argument positional constructor and every existing construction site must keep compiling.

```csharp
    // How many rows a browsable list shows: the library listing and the
    // converted page. An init property for the same reason as the flags above.
    //
    // 10 is the size the library listing was fixed at before this was
    // configurable, so leaving it alone changes nothing for anyone.
    public int PerPage { get; init; } = 10;
```

Add the sanitizer next to `SanitizeScale` (around line 175):

```csharp
    // Out of range means the documented default, not a clamp, for the same
    // reason SanitizeScale gives: 500 is not a request for 50. Zero and negative
    // are covered by the lower bound, which matters because a page size of 0
    // would divide by zero in Pager.TotalPages.
    public static int SanitizePerPage(int n) => n >= MinPerPage && n <= MaxPerPage ? n : Default.PerPage;
```

- [ ] **Step 4: Add it to the wire format**

In `Serialize`, append `&ipp={PerPage}` to the segment that already carries `scale` and `up`:

```csharp
        + $"&spread={Spread.ToString().ToLowerInvariant()}&scale={Scale}&up={(Upscale ? 1 : 0)}&ret={(ReturnAfterDownload ? 1 : 0)}&ipp={PerPage}"
```

Add `"ipp"` to `Keys`:

```csharp
    private static readonly string[] Keys =
        ["retina", "gray", "lang", "fav", "did", "spread", "scale", "up", "ret", "ipp", "ovr", "ovrw", "ovrh", "ovrd"];
```

Do NOT add `pprange` to `Keys`.

In `Parse`, next to the `Scale` line, add:

```csharp
            PerPage = SanitizePerPage(Num(q, "ipp", Default.PerPage)),
```

`Parse` needs a helper that reads an int and falls back on a missing or unparseable value. If the file already has one for `scale`, reuse it and ignore the snippet below. If it does not, add this beside `Flag`, following `Flag`'s `v[0]` rule verbatim (a duplicated key joins with a comma via `StringValues.ToString()`, so `"10,10"` must land on the fallback rather than throwing or parsing oddly):

```csharp
    // v[0], not v.ToString(), for the same reason as Flag: a duplicated key
    // ("ipp=10&ipp=10") joins to "10,10", which must fall back rather than parse.
    private static int Num(IQueryCollection q, string key, int fallback) =>
        q.TryGetValue(key, out var v) && v.Count > 0 && int.TryParse(v[0], out var n) ? n : fallback;
```

Read how `Parse` currently reads `scale` before writing this, and match it. If `scale` already goes through such a helper, `ipp` must use the same one.

- [ ] **Step 5: Read the form field and flag an out-of-range value**

In `src/Inkshelf/Endpoints/SettingsEndpoints.cs`, in the `stored with { ... }` block, next to the `Scale` line (around line 32):

```csharp
                PerPage = int.TryParse(form["perpage"].ToString(), out var pp)
                    ? DeviceSettings.SanitizePerPage(pp) : DeviceSettings.Default.PerPage,
```

Below, next to the `scaleRejected` computation (around line 58):

```csharp
            // Same silent-revert problem as the page scale: an out-of-range
            // number becomes the default, which looks like the field ignoring you.
            var rawPerPage = form["perpage"].ToString();
            var perPageRejected = !string.IsNullOrWhiteSpace(rawPerPage)
                && (!int.TryParse(rawPerPage, out var typedPp) || DeviceSettings.SanitizePerPage(typedPp) != typedPp);
```

Then extend the `flags` line:

```csharp
            var flags = (unusable ? "&range=1" : "") + (scaleRejected ? "&scalerange=1" : "")
                + (perPageRejected ? "&pprange=1" : "");
```

- [ ] **Step 6: Surface the warning on the page model**

In `src/Inkshelf/Pages/Settings.cshtml.cs`, next to `ScaleWarning` (declared around line 31, assigned around line 60):

```csharp
    public bool PerPageWarning { get; private set; }
```

and in the same method that sets `ScaleWarning`:

```csharp
        PerPageWarning = Request.Query.ContainsKey("pprange");
```

- [ ] **Step 7: Add the input**

In `src/Inkshelf/Pages/Settings.cshtml`, immediately after the page scale block and its warning (the `ScaleWarning` block ends around line 81), add:

```razor
    <p>
        <label class="setting-num">@L["Items per page"]
            <input type="number" name="perpage" min="@Inkshelf.Auth.DeviceSettings.MinPerPage" max="@Inkshelf.Auth.DeviceSettings.MaxPerPage" step="1"
                   value="@Model.Settings.PerPage" />
        </label>
    </p>
```

and immediately after it, mirroring the `ScaleWarning` block's exact shape (copy that block and adapt it rather than guessing at the markup):

```razor
@if (Model.PerPageWarning)
{
    <p>
        <span class="settings-note">@L["Not used: items per page must be between 5 and 50."]</span>
    </p>
}
```

- [ ] **Step 8: Add the German strings**

In `src/Inkshelf/locales/de.json`, add both entries. The English key must match the Razor `L[...]` string character for character:

```json
  "Items per page": "Einträge pro Seite",
  "Not used: items per page must be between 5 and 50.": "Nicht verwendet: Einträge pro Seite muss zwischen 5 und 50 liegen.",
```

Place them near the existing page-scale entries. Mind the JSON commas. No en dash in the German text.

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all green, 517 baseline plus the new tests.

Several existing tests assert the literal `Serialize()` output string. Adding `&ipp=10` changes it, so those expectations need updating. Hand-trace the new expected string once and verify the position of `ipp` rather than doing a blind find and replace, then update them.

Also run: `dotnet format Inkshelf.sln --verify-no-changes`
Expected: no output, exit 0.

- [ ] **Step 10: Commit**

```bash
git add -A
git diff --cached | grep -nP '[\x{2013}\x{2014}]' || true
git commit -m "feat: add an items-per-page setting"
```

The grep must print nothing. If it prints a line, remove the dash before committing.

---

### Task 2: Share the pager and use the setting on the library listing

**Files:**
- Create: `src/Inkshelf/Pages/Support/IPagedListing.cs`
- Modify: `src/Inkshelf/Pages/Shared/_Pager.cshtml`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: `DeviceSettings.Read(Request).PerPage` from Task 1.
- Produces: `Inkshelf.Pages.IPagedListing` with `Pager Pager { get; }` and `string PageHref(int page)`. Task 3 implements it on `ConvertedModel`.

`LibraryModel.PageSize` is a `const int = 10` used in two places: the `Pager` field initializer (around line 57) and the `GetItemsAsync` call (around line 92), with a fallback use at line 99. The constant must go, replaced by the setting read once in `OnGetAsync`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`, inside the existing class. This captures the ABS request URI so the assertion is about what was actually asked of ABS, not about what rendered:

```csharp
    [Fact]
    public async Task The_listing_asks_abs_for_the_configured_page_size()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var seen = new List<string>();
        var stub = new StubHandler(req =>
        {
            seen.Add(req.RequestUri!.ToString());
            return MakeStub().Respond(req);
        });
        using var factory = CreateFactory(stub, cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var settings = (DeviceSettings.Default with { PerPage = 25 }).Serialize();
        await client.SendAsync(LibraryRequest(factory, settings));

        var items = seen.FirstOrDefault(u => u.Contains("/items", StringComparison.Ordinal));
        Assert.NotNull(items);
        Assert.Contains("limit=25", items);
    }
```

`StubHandler` may not expose a `Respond(req)` entry point for delegating to another stub. Read `tests/Inkshelf.Tests/StubHandler.cs` first. If it does not, build the recording stub by copying `MakeStub`'s response lambda inline and recording the URI at the top of it, rather than adding a method to `StubHandler`. Either shape is fine; do not change `StubHandler`'s public surface for this.

Confirm the real query-parameter name ABS is given for the page size by reading `AbsApiClient.GetItemsAsync`. If it is not `limit`, assert on the actual name and say so in your report. Do not weaken the assertion to something that passes vacuously, such as asserting only that a request was made.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~asks_abs_for_the_configured_page_size"`
Expected: FAIL, because the listing always asks for 10.

- [ ] **Step 3: Add the interface**

Create `src/Inkshelf/Pages/Support/IPagedListing.cs`:

```csharp
namespace Inkshelf.Pages;

// What _Pager.cshtml needs, and nothing more. Implemented by the page models
// themselves rather than by a wrapper model, so neither call site has to
// construct anything and each page keeps ownership of its own href shape (the
// library listing carries filter/author/series, the converted page carries its
// local sort).
public interface IPagedListing
{
    Pager Pager { get; }

    // The URL for a 1-based page number, preserving whatever else the current
    // view is showing.
    string PageHref(int page);
}
```

- [ ] **Step 4: Retype the partial**

Rewrite `src/Inkshelf/Pages/Shared/_Pager.cshtml` to:

```razor
@model Inkshelf.Pages.IPagedListing
<nav class="pager">
    @* Both buttons always render - a disabled <button> when there's nowhere to
       go. Showing only the available one made the remaining button change
       position between page 1 and 2, which reads as the layout jumping. *@
    @if (Model.Pager.HasPrev)
    {
        <a class="btn" href="@Model.PageHref(Model.Pager.DisplayPage - 1)">&larr; @L["Prev"]</a>
    }
    else
    {
        <button type="button" class="btn" disabled>&larr; @L["Prev"]</button>
    }
    <span>@L["Page {0} of {1}", Model.Pager.DisplayPage, Math.Max(1, Model.Pager.TotalPages)]</span>
    @if (Model.Pager.HasNext)
    {
        <a class="btn" href="@Model.PageHref(Model.Pager.DisplayPage + 1)">@L["Next"] &rarr;</a>
    }
    else
    {
        <button type="button" class="btn" disabled>@L["Next"] &rarr;</button>
    }
</nav>
```

Keep the existing comment verbatim. Keep `Math.Max(1, ...)`.

- [ ] **Step 5: Implement the interface on the library model and use the setting**

In `src/Inkshelf/Pages/Library.cshtml.cs`:

Change the class declaration:

```csharp
public class LibraryModel : PageModel, IPagedListing
```

Delete `public const int PageSize = 10;` and add a default-seeded field the `Pager` initializer can use, since the setting is not known until `OnGetAsync` runs:

```csharp
    private int _perPage = DeviceSettings.Default.PerPage;
```

Change the `Pager` initializer from `new(0, PageSize, 0)` to:

```csharp
    public Pager Pager { get; private set; } = new(0, DeviceSettings.Default.PerPage, 0);
```

In `OnGetAsync`, the existing line `var ds = DeviceSettings.EnsureDid(HttpContext);` already gives the settings. Immediately after it add:

```csharp
        _perPage = ds.PerPage;
```

Replace the `GetItemsAsync` call's page size and the `Pager` construction:

```csharp
        var result = await _api.GetItemsAsync(Id, zeroPage, _perPage, filter,
            EffectiveSort, EffectiveDesc, ct);
```

```csharp
        Pager = new Pager(result.Page, result.Limit <= 0 ? _perPage : result.Limit, result.Total);
```

Add `PageHref`. It must pass the RAW `Sort` and `Desc`, exactly what the partial passed before, not `EffectiveSort`/`EffectiveDesc`. On the default view `Sort` is null, so page 2 also gets the default, which is the existing behaviour. Changing this to the effective values would alter every pager href and is out of scope:

```csharp
    // The raw Sort/Desc, not the effective ones: this is what _Pager passed
    // before it was retyped, and the default view relies on an absent sort
    // staying absent across pages.
    public string PageHref(int page) => Links.ListingHref(Sort, Desc, page);
```

If `PageSize` is referenced anywhere else in the repo, update those references. Run `grep -rn "PageSize" src tests tools` to find them. `SearchLimit` stays exactly as it is.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all green.

Run: `dotnet format Inkshelf.sln --verify-no-changes`
Expected: no output, exit 0.

- [ ] **Step 7: Commit**

```bash
git add -A
git diff --cached | grep -nP '[\x{2013}\x{2014}]' || true
git commit -m "feat: page the library listing at the configured size"
```

---

### Task 3: Page the converted page

**Files:**
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Converted.cshtml`
- Modify: `src/Inkshelf/DownloadTickets.cs`
- Test: `tests/Inkshelf.Tests/ConvertedRenderTests.cs`
- Test: `tools/uicheck/Program.cs`

**Interfaces:**
- Consumes: `DeviceSettings.PerPage` from Task 1; `IPagedListing` from Task 2.
- Produces: nothing later tasks depend on.

This is the substantive task. Read the spec's "Converted page" section before starting: the slice has to happen BEFORE rows are built, or the ticket saving does not happen and the test in Step 1 will catch it.

`ConvertedModel.OnGetAsync` today: enumerates the cache, batch-fetches metadata for every converted id, builds an `ItemRowModel` per item (minting up to two download tickets each), sorts the built rows, reverses if descending, renders. The reordering keeps every existing behaviour except that rows are built only for the visible page.

- [ ] **Step 1: Write the failing tests**

`ConvertedRenderTests.cs` has a `Request(factory, url)` helper that sets no settings cookie, and a `GetConvertedAsync(query, seed)` helper. Both need an optional settings argument. Change them to:

```csharp
    private static HttpRequestMessage Request(WebApplicationFactory<Program> factory, string url, string? settings = null)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var cookie = $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}; scr={W}x{H}x1";
        if (settings is not null) cookie += $"; inkshelf_settings={settings}";
        req.Headers.Add("Cookie", cookie);
        return req;
    }
```

The settings string must be built from `DeviceSettings.Default with { ... }` so the render target still matches what `SeedConverted` wrote. `SeedConverted` uses `DeviceSettings.Default.Spread` and `DeviceSettings.Default.Scale`, so any cookie that changes retina, grayscale, spread or scale would make `ListVariants` match nothing and the page would render empty for an unrelated reason.

The existing `MultiStub` fixture has only three items, and `MinPerPage` is 5, so
it cannot express a multi-page slice at all: a three-item fixture cannot tell a
correct slice apart from no slice. Add a seven-item fixture, which splits 5 + 2
at the minimum page size.

Add this fixture beside `MultiBatchJson`, following its literal style:

```csharp
    // Seven items, so the minimum page size of 5 splits them 5 + 2. Titles are
    // zero-padded so an ordinal title sort and a numeric reading agree, which
    // keeps the expected page contents obvious.
    private const string PagedBatchJson = $$"""
        {"libraryItems":[
          {"id":"p1","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 01","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p1.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p2","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 02","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p2.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p3","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 03","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p3.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p4","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 04","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p4.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p5","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 05","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p5.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p6","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 06","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p6.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } },
          {"id":"p7","libraryId":"{{LibId}}","media":{"metadata":{"title":"Paged 07","authors":[{"id":"pa","name":"Pager Author"}]},"ebookFile":{"ebookFormat":"cbz","metadata":{"filename":"p7.cbz","size":{{Size}},"mtimeMs":{{Mtime}} } } } }
        ]}
        """;

    private static StubHandler PagedStub() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(PagedBatchJson);
        if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
        if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    // Which "Paged NN" titles appear, in the order they appear.
    private static List<string> PagedOrder(string html) =>
        Enumerable.Range(1, 7).Select(i => $"Paged {i:00}")
            .Where(t => html.Contains(t, StringComparison.Ordinal))
            .OrderBy(t => html.IndexOf(t, StringComparison.Ordinal))
            .ToList();

    // Seeds all seven with distinct conversion times, oldest first, so the
    // default newest-first sort is a strict reversal and page boundaries are
    // unambiguous.
    private static async Task<string> GetPagedAsync(string query, int perPage,
        Action<WebApplicationFactory<Program>, EpubCache>? extra = null)
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(PagedStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<EpubCache>();
        for (var i = 1; i <= 7; i++)
            SeedConverted(cache, $"p{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
        extra?.Invoke(factory, cache);
        var settings = (DeviceSettings.Default with { PerPage = perPage }).Serialize();
        return await (await client.SendAsync(Request(factory, "/converted" + query, settings))).Content.ReadAsStringAsync();
    }
```

`GetPagedAsync` cannot also return the factory for the ticket-count test, so that
one builds its own factory inline below rather than reusing the helper.

Now the tests, inside the existing class:

```csharp
    [Fact]
    public async Task Slices_to_the_configured_page_size()
    {
        // Sorted by title so the expected contents of each page are obvious and
        // independent of conversion times.
        var page1 = await GetPagedAsync("?sort=title", perPage: 5);

        Assert.Equal(
            new[] { "Paged 01", "Paged 02", "Paged 03", "Paged 04", "Paged 05" },
            PagedOrder(page1));
        Assert.Contains("Page 1 of 2", page1);
    }

    [Fact]
    public async Task The_second_page_shows_the_remainder()
    {
        var page2 = await GetPagedAsync("?sort=title&page=2", perPage: 5);

        Assert.Equal(new[] { "Paged 06", "Paged 07" }, PagedOrder(page2));
        Assert.Contains("Page 2 of 2", page2);
    }

    [Fact]
    public async Task A_page_beyond_the_end_clamps_to_the_last_page()
    {
        // Clamps rather than rendering an empty list: this list is sliced
        // locally, so an out-of-range page is ours to correct.
        var far = await GetPagedAsync("?sort=title&page=99", perPage: 5);

        Assert.Equal(new[] { "Paged 06", "Paged 07" }, PagedOrder(far));
        Assert.Contains("Page 2 of 2", far);
    }

    [Fact]
    public async Task A_page_below_one_reads_as_the_first_page()
    {
        var low = await GetPagedAsync("?sort=title&page=0", perPage: 5);

        Assert.Equal(
            new[] { "Paged 01", "Paged 02", "Paged 03", "Paged 04", "Paged 05" },
            PagedOrder(low));
        Assert.Contains("Page 1 of 2", low);
    }

    [Fact]
    public async Task A_larger_page_size_fits_everything_on_one_page()
    {
        var all = await GetPagedAsync("?sort=title", perPage: 10);

        Assert.Equal(7, PagedOrder(all).Count);
        Assert.Contains("Page 1 of 1", all);
    }

    [Fact]
    public async Task The_pager_hrefs_keep_the_active_sort_and_direction()
    {
        var html = await GetPagedAsync("?sort=title&desc=1", perPage: 5);

        // The pager is present and its links carry the view's own sort, so
        // paging does not silently reset the list to the default order.
        Assert.Contains("class=\"pager\"", html);
        Assert.Contains("sort=title", html);
        Assert.Contains("desc=1", html);
    }

    // Pins the ORDERING of the work, which is the whole point of this task. If
    // rows are built before the slice, all seven rows mint their tickets and the
    // two counts come out equal.
    [Fact]
    public async Task Mints_tickets_only_for_the_rows_it_renders()
    {
        static async Task<int> LiveAfterAsync(int perPage)
        {
            using var cacheDir = new TempDir();
            using var keysDir = new TempDir();
            using var factory = CreateFactory(PagedStub(), cacheDir.Path, keysDir.Path);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var cache = factory.Services.GetRequiredService<EpubCache>();
            for (var i = 1; i <= 7; i++)
                SeedConverted(cache, $"p{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));
            var settings = (DeviceSettings.Default with { PerPage = perPage }).Serialize();
            await client.SendAsync(Request(factory, "/converted?sort=title", settings));
            return factory.Services.GetRequiredService<DownloadTickets>().LiveCount;
        }

        var five = await LiveAfterAsync(5);
        var ten = await LiveAfterAsync(10);

        Assert.True(five > 0, "Expected the rendered rows to mint tickets.");
        // Five visible rows must cost strictly fewer tickets than seven.
        Assert.True(five < ten, $"Expected a 5-row page to mint fewer tickets than a 7-row page, got {five} and {ten}.");
    }

    // Pins the deliberate exception: convert state is resolved for EVERY item,
    // not just the page's, so a conversion running on page 2 still refreshes
    // page 1. An item on this page resolves to Converting only when its source
    // changed since the conversion, because ConvertQueue.Status answers Done
    // whenever the cache file exists. So p7 is seeded at the CURRENT size and
    // the queue entry is made against the path for a CHANGED size, which is what
    // the resolver will look up.
    [Fact]
    public async Task A_conversion_on_a_later_page_still_refreshes_this_page()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(PagedStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<EpubCache>();
        for (var i = 1; i <= 7; i++)
            SeedConverted(cache, $"p{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc));

        // p7 is last by title, so it is on page 2 while we render page 1.
        var target = DeviceSettings.Default.ToRenderTarget($"{W}x{H}x1");
        var pending = cache.PathFor("p7", Size, Mtime, target.MaxW, target.MaxH,
            target.Grayscale, target.Spread, target.Scale, target.Dpr, target.Upscale);
        File.Delete(pending);
        factory.Services.GetRequiredService<ConvertQueue>().Enqueue(new ConvertJob(
            "p7", "tok", pending, new EbookMeta("T", "A", null, null, "p7"), target));

        var settings = (DeviceSettings.Default with { PerPage = 5 }).Serialize();
        var page1 = await (await client.SendAsync(Request(factory, "/converted?sort=title", settings))).Content.ReadAsStringAsync();

        // p7 is not on this page...
        Assert.DoesNotContain("Paged 07", page1);
        // ...but its conversion still arms the refresh.
        Assert.Contains("http-equiv=\"refresh\"", page1);
    }
```

The last test is the fiddliest thing in this plan and its setup is load-bearing, so verify each assumption as you go rather than adjusting the assertions:

- `ConvertQueue.Status(path)` returns `Done` when `File.Exists(path)`, which is why the seeded file for p7 is deleted before enqueuing. Without the delete, the state resolves to `Cached` and the test proves nothing.
- Deleting the file may drop p7 from the listing entirely if `ListVariants` re-reads the directory after the delete. If that happens, p7 will not be on page 2 either, and the test no longer pins what it claims. In that case seed p7 with a size that DIFFERS from what the stub reports (give the stub a distinct size for p7 and seed at that other size) so the variant still exists while the resolved path does not, and say in your report which shape you used.
- `ConvertJob`'s constructor is `(string ItemId, string AccessToken, string CachePath, EbookMeta Meta, RenderTarget Target, string? FileIno = null, long ArchiveBytes = 0)`. `ListingRenderTests.cs:246` has a working call to copy.
- The meta refresh renders as `<noscript><meta http-equiv="refresh" content="30" /></noscript>` in `_Layout.cshtml:8-11`. Assert on the attribute, not on the surrounding `noscript`.
- `DeviceSettings.ToRenderTarget` takes the raw `scr` cookie string; the tests' `Request` helper sends `scr={W}x{H}x1`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ConvertedRenderTests"`
Expected: the new tests FAIL. `LiveCount` does not exist yet, so this is a compile error until Step 3.

- [ ] **Step 3: Expose the live ticket count**

In `src/Inkshelf/DownloadTickets.cs`, next to the `_live` field:

```csharp
    // Live tickets, for a test that pins the converted page building rows only
    // for the page it renders. Cheap on ConcurrentDictionary and useful if a
    // ticket leak is ever suspected.
    public int LiveCount => _live.Count;
```

- [ ] **Step 4: Reorder the work and slice**

In `src/Inkshelf/Pages/Converted.cshtml.cs`:

Change the class declaration:

```csharp
public class ConvertedModel : PageModel, IPagedListing
```

Add the pager state and the href, near the existing `Rows` property:

```csharp
    public Pager Pager { get; private set; } = new(0, DeviceSettings.Default.PerPage, 0);

    // The APPLIED sort and direction, not the raw query values: the pager must
    // describe what is on screen, the same rule SortHref's comment gives.
    public string PageHref(int page) =>
        $"/converted?sort={ActiveSort}" + (AppliedDesc ? "&desc=1" : "") + (page > 1 ? $"&page={page}" : "");
```

Bind the page number. Add it as a parameter of `OnGetAsync`, matching how `LibraryModel.OnGetAsync` does it:

```csharp
    public async Task<IActionResult> OnGetAsync([FromQuery] int page = 1, CancellationToken ct = default)
```

Then restructure the body. The existing method builds `built` as a `List<(ItemRowModel Row, AbsBatchMetadata? Meta)>` and sorts that. Change it to sort a list that carries only what the sort needs plus the source data, then slice, then build rows.

Replace the row-building loop and everything after it with this shape. `ConvertRowStateResolver.Resolve` still runs for every item, deliberately, because `AnyConverting` drives the page's 30 second meta-refresh and scoping it to the visible page would change when the page auto-refreshes:

```csharp
        // State for EVERY item, not just the page's: AnyConverting drives a 30s
        // MetaRefresh, and scoping it to the visible page would stop the page
        // refreshing while something converts on another page. Resolve is local
        // file and queue checks with no network call.
        var candidates = new List<(AbsBatchItem It, AbsBatchMedia M, ConvertRowState State, string? CachePath, AbsBatchMetadata? Meta)>();
        foreach (var it in items)
        {
            if (it.Media is null) continue;
            var m = it.Media;
            var probe = new AbsItem(it.Id, new AbsMedia(
                new AbsMetadata(m.Metadata?.Title, null, null), m.CoverPath, null, m.EbookFile));
            var (state, cachePath) = ConvertRowStateResolver.Resolve(probe, m, target, _cache, _queue);
            if (state == ConvertRowState.Converting) AnyConverting = true;
            candidates.Add((it, m, state, cachePath, m.Metadata));
        }

        IEnumerable<(AbsBatchItem It, AbsBatchMedia M, ConvertRowState State, string? CachePath, AbsBatchMetadata? Meta)> ordered = ActiveSort switch
        {
            "series" => candidates
                .OrderBy(b => HasSeries(b.Meta) ? 0 : 1)
                .ThenBy(b => SeriesKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => SeqKey(b.Meta))
                .ThenBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
            "title" => candidates.OrderBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
            "author" => candidates
                .OrderBy(b => AuthorKey(b.Meta), StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
            // ConvertedAtUtc, not the source mtime in the filename.
            _ => candidates
                .OrderBy(b => convertedAt.TryGetValue(b.It.Id, out var at) ? at : DateTime.MinValue)
                .ThenBy(b => TitleKey(b.Meta), StringComparer.OrdinalIgnoreCase),
        };
        var sorted = ordered.ToList();
        if (AppliedDesc) sorted.Reverse();

        // Clamp rather than render an empty list: this list is sliced locally, so
        // a page past the end is ours to correct, unlike the library listing
        // where ABS answers an empty result.
        var perPage = settings.PerPage;
        var totalPages = Math.Max(1, (sorted.Count + perPage - 1) / perPage);
        var zeroPage = Math.Clamp(page - 1, 0, totalPages - 1);
        Pager = new Pager(zeroPage, perPage, sorted.Count);

        // Rows, and therefore TICKETS, only for what is rendered.
        var access = _tokens.Read()?.Access;
        foreach (var b in sorted.Skip(zeroPage * perPage).Take(perPage))
        {
            var item = new AbsItem(b.It.Id, new AbsMedia(
                new AbsMetadata(b.M.Metadata?.Title, null, null), b.M.CoverPath, null, b.M.EbookFile));
            var links = new LibraryLinks(b.It.LibraryId ?? "", null, null, null, null, false);
            var rawDownloaded = markSet.Contains(DownloadMarks.RawKey(b.It.Id, null));
            var epubDownloaded = markSet.Contains(DownloadMarks.EpubKey(b.It.Id, null));
            // Both hrefs are re-requested by a cookie-less download manager, so each
            // gets a ticket standing for exactly the file that row offers.
            var epubTicket = b.CachePath is { } path
                ? _tickets.MintEpub(b.It.Id, null, settings.Did,
                    EpubName.For(b.M.Metadata?.Authors?.FirstOrDefault()?.Name, b.M.Metadata?.Title), path)
                : null;
            var filename = b.M.EbookFile?.Metadata?.Filename;
            var rawTicket = filename is not null && access is { } acc
                ? _tickets.MintRaw(b.It.Id, null, settings.Did, filename, acc)
                : null;
            Rows.Add(new ItemRowModel(item, links, b.M.Metadata?.Authors, b.M.Metadata?.Series,
                b.State, "/converted", finished.Contains(b.It.Id), rawDownloaded, epubDownloaded,
                rawTicket, epubTicket));
        }
        return Page();
```

Notes you must honour while doing this:

- `TitleKey`, `AuthorKey`, `SeriesKey`, `SeqKey` and `HasSeries` are existing private helpers. `AuthorKey`, `SeriesKey`, `SeqKey` and `HasSeries` already take `AbsBatchMetadata?` and need no change. `TitleKey` currently takes the old tuple and reads through the built row, which no longer exists at sort time. Replace it with a version that takes the metadata directly, so it matches the other four and the tuple shape stops leaking into the helpers:

```csharp
    private static string TitleKey(AbsBatchMetadata? m) => m?.Title ?? "";
```

  Then the `ordered` switch calls `TitleKey(b.Meta)` rather than `TitleKey(b)`. Update all four call sites in the switch. If `AbsBatchMetadata` does not expose `Title` directly, read it the same way the old `TitleKey` did and keep the parameter as `AbsBatchMetadata?`; do not reintroduce a tuple parameter.
- The early return `if (convertedAt.Count == 0) return Page();` stays where it is.
- `Rows` is already initialised to an empty list, so appending is fine. If you prefer, build a local list and assign it.
- **`access` is already declared in the existing code, ABOVE the loop you are replacing** (`var access = _tokens.Read()?.Access;`, just after `FetchFinishedAsync`). The snippet above declares it again after the sort, so you must **delete the earlier declaration** or you will get a duplicate-variable compile error. Keep exactly one, read once rather than per row.
- Moving that read later is safe and must stay safe: the rule it follows is that the bearer is read AFTER every ABS call of the request, and the last ABS call is `FetchFinishedAsync`, which still runs before the sort. Do not move it earlier, and do not move any ABS call after it.
- `finished` and `markSet` are also declared in the surviving code above; do not redeclare them.
- The original tail built a local `rows` list and assigned `Rows = rows;`. The snippet appends to `Rows` instead, which is already initialised to an empty list with a private setter. Delete the old `rows` local and its assignment.

- [ ] **Step 5: Render the pager**

In `src/Inkshelf/Pages/Converted.cshtml`, add the pager. `Converted.cshtml` renders rows in a `foreach (var row in Model.Rows)` around line 35. Place the pager the way `Library.cshtml` does, which renders it both above and below the rows (lines 93 and 104). Match that:

```razor
    <partial name="_Pager" model="Model" />
```

Add it immediately before the rows loop and immediately after it, inside the same block that currently guards on `Model.Rows.Count > 0`. Read `Library.cshtml:88-106` and mirror its placement and conditions rather than guessing.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: all green.

Run: `dotnet format Inkshelf.sln --verify-no-changes`
Expected: no output, exit 0.

If a pre-existing `ConvertedRenderTests` sort test now fails because only one page of rows renders, that is the fixture exceeding the page size. Raise the test's `PerPage` so all its items stay on one page rather than changing what the test asserts about order.

- [ ] **Step 7: Add browser coverage**

In `tools/uicheck/Program.cs`, inside the `UICHECK_AUTHED` block, the existing converted-page checks capture `converted` and `converted-sorted` screenshots. Extend that area with a check that the pager renders and navigates. Set a small page size through the settings cookie so the seeded ABS's converted items span more than one page.

The authed pass builds its context with a settings cookie from `const string De = "retina=1&gray=0&lang=de&fav=";` (around line 61). A pager check needs `&ipp=5` on that cookie, in its own `BrowserContext` with its own login, because cookies do not cross contexts. Model the block on the existing `dlreturn` check in the same file, which does exactly this.

Assert concretely:

- the pager is present on `/converted`,
- clicking Next changes the URL and the rows,
- the sort survives paging, i.e. after sorting by title and paging, the URL still carries `sort=title`.

If the seeded ABS does not yield more than five converted items at that point in the run, either convert more in the check or state in your report that the multi-page case is not reachable there and assert what is. Do not assert something that would pass with no pager at all.

- [ ] **Step 8: Run the browser pass**

Run: `tools/uicheck/run.sh`
Expected: `PASS` at both viewports.

Read the screenshots it writes for the converted page in `tools/uicheck/shots/` and confirm the pager renders where you expect. Do not rely on the exit code alone.

- [ ] **Step 9: Commit**

```bash
git add -A
git diff --cached | grep -nP '[\x{2013}\x{2014}]' || true
git commit -m "feat: page the converted list"
```

---

### Task 4: Documentation

**Files:**
- Modify: `docs/ROADMAP.md`
- Modify: `docs/FAQ.md`

**Interfaces:**
- Consumes: the finished feature.
- Produces: nothing code-facing.

- [ ] **Step 1: Record the shipped work**

Add an entry to `docs/ROADMAP.md`'s `## Done` section (the section starts around line 92), matching the format the existing entries use, `- **Title** (#N) - description`, and their line wrapping. Note that it closes #67. Mention both halves: the setting and the converted page's pager.

- [ ] **Step 2: Qualify the screenful investigation**

`docs/ROADMAP.md` around line 82 carries "**Screenful pagination (investigation).**" It is still open, but its framing assumed a fixed 10. Add a sentence recording that a manual "Items per page" setting now exists, so the investigation is about deriving the number automatically rather than about making it configurable at all, and that if it proves out it becomes another value the setting can take.

Do NOT delete the investigation. It is not closed by this work.

- [ ] **Step 3: Add a FAQ entry**

In `docs/FAQ.md`, add an entry in the existing register (short, declarative, second person) for the symptom a user would search by: the list shows too many or too few books per screen. Say where the setting is, that it applies to the library lists and the converted page, and that search results are not affected.

- [ ] **Step 4: Verify no banned dashes across the branch**

Run: `git diff main | grep -nP '^\+.*[\x{2013}\x{2014}]'`
Expected: no matching lines printed.

- [ ] **Step 5: Commit**

```bash
git add docs/
git commit -m "docs: document the items-per-page setting"
```

---

## After the plan

`tools/uicheck/run.sh` covers this feature in a real browser at both viewports. Nothing here depends on the reader engine behaving unusually, so unlike the reader workarounds a device pass is useful for judging whether the default of 10 feels right on the reader, but is not required to trust the change.

Then use `superpowers:finishing-a-development-branch`.
