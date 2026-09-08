# Return To The List After A Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (this plan is handed off to a fresh session) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-device setting, default off, that returns the browser to the page a download started from.

**Architecture:** Progressive enhancement in two halves. Marker attributes go on the three anchors that actually serve a file, always rendered and inert on their own. A small ES5 script, rendered into `_Layout.cshtml` ONLY when the setting is on, records the current page when an armed link is tapped and, on the next page load, sends the browser back there if it landed somewhere else.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit, Playwright (C#) for the browser pass. Client side is hand-written ES5 with `sessionStorage`, no libraries.

## Context for a fresh session

Inkshelf is a thin, server-rendered Razor Pages client for the Audiobookshelf API, run as a sidecar container. It targets very old e-reader browsers (Chrome 30 era and older), so pages are plain HTML with `<form>` and `<a>` only and near-zero JavaScript. It is public open source with external users, and the maintainer's own deployment is shared with family.

**Read `docs/superpowers/specs/2026-09-08-return-after-download-design.md` before starting.** It records seven findings measured on real hardware, each of which eliminated a simpler fix. Without them this design looks arbitrary and you will be tempted to "simplify" it into something already proven not to work. In particular: `target="_blank"`, a named hidden iframe, `history.pushState` padding, and the `download` attribute were all tested on device and all failed.

Also read `docs/ARCHITECTURE.md` before touching structure. Note especially that it forbids device-class detection and UA sniffing. This feature complies by being an opt-in setting whose correction only fires when the misbehaviour actually happened.

## Global Constraints

- **No em dashes (U+2014) and no en dashes (U+2013) anywhere**: not in code, comments, docs, the German string, or commit messages. Use a plain hyphen, a comma, or two sentences. Absolute, with no exception for German where an en dash would be conventional. The existing files legitimately contain U+00D7, U+2192, U+00F7, U+2713 and the ellipsis U+2026; only U+2013 and U+2014 are banned. Verify before each commit with `git diff --cached | grep -nP '[\x{2013}\x{2014}]'` returning nothing.
- **Client JavaScript must be ES5.** No `fetch`, no `Promise`, no arrow functions, no `const`/`let`, no template literals. Everything wrapped in `try/catch` so an engine that cannot run it leaves the plain anchors working.
- **No new dependencies, no libraries, no build step for the script.** It is inline in the Razor layout, as the two existing scripts are.
- **The English label in a Razor `L[...]` lookup IS the key in `de.json`.** Any drift means German silently falls back to English. There is no `locales/en.json`; English is the key itself.
- **UI copy names what the user sees**, not the mechanism.
- Conventional Commits: `type: subject`, imperative, lowercase, no period, max ~72 chars. No `Co-Authored-By` and no "Generated with Claude Code" lines.
- Per-task commits on branch `feat/return-after-download` are pre-authorized by the user for this plan.
- `dotnet test` from the repo root must be green and `dotnet format --verify-no-changes` clean before each commit.
- Do NOT start a dev server during implementation; it holds a file lock on `src/Inkshelf/bin` and breaks `dotnet test`. `tools/uicheck/run.sh` starts and stops its own and is fine.
- Do NOT touch `CHANGELOG.md`; it belongs to the release process.
- The baseline is **507 passing tests**. `dotnet test` takes about 40 seconds including the build.

## File Structure

- `src/Inkshelf/Auth/DeviceSettings.cs`: the new flag, cookie key `ret`.
- `src/Inkshelf/Endpoints/SettingsEndpoints.cs`: reads the form field.
- `src/Inkshelf/Pages/Settings.cshtml`: the checkbox.
- `src/Inkshelf/locales/de.json`: the German label.
- `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, `src/Inkshelf/Pages/Item.cshtml`, `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`: the marker attribute.
- `src/Inkshelf/Pages/Shared/_Layout.cshtml`: the gated script.
- `tools/uicheck/Program.cs`: browser coverage of the correction.

---

### Task 1: The setting

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Inkshelf/Pages/Settings.cshtml`
- Modify: `src/Inkshelf/locales/de.json`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`, `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `DeviceSettings.ReturnAfterDownload` (`bool`, init property, default `false`), cookie key `ret`, form field `returndl`.

This mirrors the existing `Upscale` setting exactly. Read those four call sites first (`DeviceSettings.cs:51`, `:88`, `:112`, `:138`) and follow them; they are the pattern.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`, following the shape of the neighbouring tests in that file (it already uses `QueryHelpers.ParseQuery` and has a `RequestWithCookie` helper):

```csharp
    [Fact]
    public void ReturnAfterDownload_defaults_off()
    {
        Assert.False(DeviceSettings.Default.ReturnAfterDownload);
    }

    [Fact]
    public void ReturnAfterDownload_round_trips_through_the_wire_format()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery(
            (DeviceSettings.Default with { ReturnAfterDownload = true }).Serialize()));
        Assert.True(DeviceSettings.FromQuery(q)!.ReturnAfterDownload);
    }

    [Fact]
    public void ReturnAfterDownload_absent_from_an_older_cookie_reads_as_off()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("retina=1&gray=0&lang=&fav="));
        Assert.False(DeviceSettings.FromQuery(q)!.ReturnAfterDownload);
    }

    [Fact]
    public void ReturnAfterDownload_alone_is_enough_to_recognise_a_settings_query()
    {
        var q = new QueryCollection(QueryHelpers.ParseQuery("ret=1"));
        Assert.True(DeviceSettings.FromQuery(q)!.ReturnAfterDownload);
    }
```

Add to `tests/Inkshelf.Tests/EndpointTests.cs`. There is no shared settings-POST helper in that file; every settings test inlines the client, the antiforgery token and the form. Follow `Saving_with_the_override_off_keeps_the_numbers` (around line 400) for the shape:

```csharp
    [Fact]
    public async Task Saving_return_after_download_records_it_and_absence_clears_it()
    {
        // Unchecked checkboxes submit nothing, so absent means off, the same
        // convention retina, grayscale and upscale already rely on.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        var on = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["returndl"] = "on",
        }));
        Assert.Contains("ret=1", on.Headers.Location!.OriginalString);

        var off = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
        }));
        Assert.Contains("ret=0", off.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Settings_page_renders_the_return_after_download_checkbox()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"returndl\"", html);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ReturnAfterDownload|FullyQualifiedName~return_after_download"`
Expected: build failure, `DeviceSettings` has no member `ReturnAfterDownload`.

- [ ] **Step 3: Add the property**

In `src/Inkshelf/Auth/DeviceSettings.cs`, immediately after the `Upscale` property (line 51):

```csharp
    // Return to the page a download started from. An init property for the same
    // reason as the flags above.
    //
    // Off by default because it costs an extra page load. It exists for reader
    // engines whose browser is killed when their book reader takes the foreground
    // and restored from a stale snapshot, which loses the listing. Nothing in the
    // markup can prevent that; see the spec's spike findings before changing the
    // approach.
    public bool ReturnAfterDownload { get; init; }
```

- [ ] **Step 4: Add it to the wire format**

In `Serialize`, extend the line that already carries spread, scale and up:

```csharp
        + $"&spread={Spread.ToString().ToLowerInvariant()}&scale={Scale}&up={(Upscale ? 1 : 0)}&ret={(ReturnAfterDownload ? 1 : 0)}"
```

Add `"ret"` to the `Keys` array, after `"up"`:

```csharp
    private static readonly string[] Keys =
        ["retina", "gray", "lang", "fav", "did", "spread", "scale", "up", "ret", "ovr", "ovrw", "ovrh", "ovrd"];
```

And read it in `Parse`, next to `Upscale`:

```csharp
            ReturnAfterDownload = Flag(q, "ret", Default.ReturnAfterDownload),
```

`Flag` exists because an ABSENT key must land on the DOCUMENTED default rather than on `false`. Read its comment before using it.

- [ ] **Step 5: Read the form field**

In `src/Inkshelf/Endpoints/SettingsEndpoints.cs`, in the `stored with` block, next to `Upscale`:

```csharp
                ReturnAfterDownload = form.ContainsKey("returndl"),
```

- [ ] **Step 6: Add the checkbox**

In `src/Inkshelf/Pages/Settings.cshtml`, after the upscale paragraph (which ends at line 41):

```razor
    <p>
        <label>
            <input type="checkbox" name="returndl" value="on" @(Model.Settings.ReturnAfterDownload ? "checked" : "") />
            @L["Return to the list after a download"]
        </label>
    </p>
```

- [ ] **Step 7: Add the German string**

In `src/Inkshelf/locales/de.json`, beside the existing "Enlarge small pages" entry at line 42, matching the file's formatting:

```json
  "Return to the list after a download": "Nach dem Herunterladen zur Liste zurück",
```

Use real umlauts as the rest of the file does. "Herunterladen" is the word the UI already uses for the Download button, so prefer it over "Download" for consistency. Verify the English key matches `Settings.cshtml` character for character.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

Note: some existing tests assert literal `Serialize()` output strings and will need `&ret=0` inserted after `&up=0`. That happened when `up` was added. If any fail, hand-trace the concatenation order in `Serialize` and confirm the position and value are what it really produces before editing the expectation.

- [ ] **Step 9: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs src/Inkshelf/Endpoints/SettingsEndpoints.cs src/Inkshelf/Pages/Settings.cshtml src/Inkshelf/locales/de.json tests/Inkshelf.Tests/DeviceSettingsTests.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: add a return-to-the-list-after-download setting"
```

---

### Task 2: Mark the links that serve a file

> **Superseded during implementation.** This task's premise was wrong: it assumed
> the `data-warm` convert anchors never navigate. They do, as soon as the poller
> marks one `data-ready="1"`, at which point its click handler stops calling
> `preventDefault`. So the steps below that mark "the Cached EPUB anchor only",
> and the test asserting a `data-warm` anchor is NOT marked, were both replaced:
> every download anchor is marked, and the layout script skips writing a record
> while a `data-warm` anchor is not yet ready. See the spec's "Which links are
> armed" section for the corrected rule. The steps are left as written for the
> record; do not follow them as-is.

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml:55`
- Modify: `src/Inkshelf/Pages/Item.cshtml:99`
- Modify: `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml` (the `Cached` branch only)
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: nothing (the markers are ungated and independent of Task 1).
- Produces: the attribute `data-dlreturn` on exactly the anchors that navigate and serve a file.

The markers are always rendered, regardless of the setting. They are inert without the script. The spec explains why they are not gated: gating them too would mean threading the flag through `ItemRowModel`, `ConvertActionModel` and `Item.cshtml.cs`'s `FileRow` plus every construction site, to remove a few bytes per row that no user can observe.

**Which anchors, and why not Regenerate.** The convert URL answers three ways:

- `Cached` renders a plain anchor that serves the file. Arm it.
- The `data-warm` states (Convert, Converting, Convert-retry) are intercepted by the background-convert script in `_Layout.cshtml` only while not yet ready: it calls `preventDefault`, so a click before the conversion completes does not navigate. Once the poller marks the anchor `data-ready="1"` the same anchor is a live download link. Arm these too, but the layout script must skip writing the record while not yet ready - `preventDefault` does not stop a second listener on the same element, so an unconditional record would sit unspent and bounce the user's next deliberate navigation.
- Regenerate navigates but only redirects back to the same listing, so arming it would be harmless and pointless. Do NOT arm it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`. That file has a `CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path)` plus `LibraryRequest(factory)` harness and a `PrimaryConvertAnchor(html)` helper that isolates the row's convert anchor (so a whole-page assertion cannot false-fail on the layout script, which mentions the attribute names by name):

```csharp
    [Fact]
    public async Task The_download_anchor_is_marked_for_the_return_after_download_script()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        var anchor = Regex.Match(html, "<a [^>]*href=\"/download/[^\"]*\"[^>]*>");
        Assert.True(anchor.Success, "Expected a download anchor in the rendered listing.");
        Assert.Contains("data-dlreturn", anchor.Value);
    }

    [Fact]
    public async Task A_convert_anchor_that_never_navigates_is_not_marked()
    {
        // The poller calls preventDefault on data-warm anchors, so a record stored
        // there would never be spent and would bounce the reader on their next
        // deliberate navigation.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        var convert = PrimaryConvertAnchor(html);
        Assert.Contains("data-warm", convert);
        Assert.DoesNotContain("data-dlreturn", convert);
    }
```

The seeded fixture's row renders the convert action in a non-Cached state, which is why the second test can assert on `PrimaryConvertAnchor`. If the fixture's state turns out to be `Cached`, that test's premise is wrong: print the anchor once, then either point the test at a genuinely non-Cached row or state in your report that the fixture cannot express this case and that the assertion was moved to a unit-level check of the partial instead. Do NOT weaken it to something that passes vacuously.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~marked_for_the_return_after_download|FullyQualifiedName~never_navigates_is_not_marked"`
Expected: `The_download_anchor_is_marked_for_the_return_after_download_script` FAILS on the missing attribute. `A_convert_anchor_that_never_navigates_is_not_marked` PASSES already, and is regression cover for the mistake this task must not make.

- [ ] **Step 3: Mark the listing's Download anchor**

In `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, line 55 currently reads:

```razor
                <a class="btn" href="@Model.DownloadHref">@L["Download"]@if (Model.RawDownloaded) { <text> &#8595;</text> }</a>
```

Change it to:

```razor
                @* data-dlreturn: this anchor navigates and serves a file, so the
                   return-after-download script arms on it. Inert unless that
                   setting is on, since the script is only rendered then. *@
                <a class="btn" href="@Model.DownloadHref" data-dlreturn>@L["Download"]@if (Model.RawDownloaded) { <text> &#8595;</text> }</a>
```

- [ ] **Step 4: Mark the item page's per-file Download anchors**

In `src/Inkshelf/Pages/Item.cshtml`, line 99 currently reads:

```razor
                <a class="btn" href="@f.DownloadHref">@L["Download"]@if (f.Downloaded) { <text> &#8595;</text> }</a>
```

Change it to:

```razor
                <a class="btn" href="@f.DownloadHref" data-dlreturn>@L["Download"]@if (f.Downloaded) { <text> &#8595;</text> }</a>
```

- [ ] **Step 5: Mark the Cached EPUB anchor only**

In `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`, the `Cached` case currently reads:

```razor
        case ConvertRowState.Cached:
            <a class="btn" href="@baseHref" title="@L["Already converted - downloads right away"]">@if (Model.Downloaded) { <text>EPUB &#8595;</text> } else { <text>EPUB</text> }</a>
            break;
```

Change it to:

```razor
        case ConvertRowState.Cached:
            @* The ONLY convert state that serves a file. The data-warm states are
               intercepted by the poller and never navigate, so arming them would
               leave a stored record that nothing spends. *@
            <a class="btn" href="@baseHref" data-dlreturn title="@L["Already converted - downloads right away"]">@if (Model.Downloaded) { <text>EPUB &#8595;</text> } else { <text>EPUB</text> }</a>
            break;
```

Leave the `Converting`, `Failed`, `default` and Regenerate anchors untouched.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_ItemRow.cshtml src/Inkshelf/Pages/Item.cshtml src/Inkshelf/Pages/Shared/_ConvertAction.cshtml tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "feat: mark the download links that serve a file"
```

---

### Task 3: The gated script

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`
- Test: `tools/uicheck/Program.cs`

**Interfaces:**
- Consumes: `DeviceSettings.ReturnAfterDownload` from Task 1 and the `data-dlreturn` markers from Task 2.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`. `LibraryRequest(factory, settings)` takes a raw `inkshelf_settings` cookie value as its second argument, which is how the setting gets turned on for a render test:

```csharp
    [Fact]
    public async Task The_return_after_download_script_is_absent_unless_the_setting_is_on()
    {
        // CLAUDE.md allows client JS only where unavoidable, and this is a
        // workaround for one reader engine. Nobody else should receive it.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("inkshelf.dlreturn", html);
    }

    [Fact]
    public async Task The_return_after_download_script_is_present_when_the_setting_is_on()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(
            LibraryRequest(factory, "retina=1&gray=0&lang=en&fav=&ret=1"))).Content.ReadAsStringAsync();

        Assert.Contains("inkshelf.dlreturn", html);
    }
```

`inkshelf.dlreturn` is the `sessionStorage` key the script uses, so asserting on it pins the script's presence without depending on its formatting.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~return_after_download_script`
Expected: `..._is_absent_unless_the_setting_is_on` PASSES (nothing is rendered yet) and `..._is_present_when_the_setting_is_on` FAILS.

- [ ] **Step 3: Add the gated script**

In `src/Inkshelf/Pages/Shared/_Layout.cshtml`, after the closing `</script>` of the existing block and immediately before `</body>`:

```razor
    @* Rendered only when the setting is on. A reader engine whose browser is
       killed while its book reader holds the foreground comes back restored from
       a stale snapshot, losing the listing the download started from. Nothing in
       the markup can prevent that: target, a named iframe and pushState padding
       were all measured on device and all failed (see the spec). So this lets the
       restore land wrong and corrects from the page it lands on, which IS a fresh
       document load and therefore runs this script.

       Gated in Razor rather than in JavaScript so a device that does not need the
       workaround never receives it. *@
    @if (Inkshelf.Auth.DeviceSettings.Read(Context.Request).ReturnAfterDownload)
    {
        <script>
        (function () {
            var KEY = 'inkshelf.dlreturn';
            function here() { return window.location.pathname + window.location.search; }
            try {
                // Arm: remember the page a download was started from.
                var links = document.querySelectorAll('a[data-dlreturn]');
                for (var i = 0; i < links.length; i++) {
                    links[i].addEventListener('click', function () {
                        try { sessionStorage.setItem(KEY, here()); } catch (e) {}
                    });
                }

                // Correct: if the restore landed elsewhere, go back.
                var want = sessionStorage.getItem(KEY);
                if (want) {
                    // Cleared BEFORE deciding: a wrong comparison then costs one
                    // needless navigation, never a redirect loop.
                    sessionStorage.removeItem(KEY);
                    // No age limit on purpose. The record is spent by the first
                    // scripted load after the download either way, and a real
                    // reading session measured 154 seconds, so any window would
                    // only ever suppress a correction that should have happened.
                    if (want !== here()) { window.location.replace(want); }
                }
            } catch (e) {}
        })();
        </script>
    }
```

`Context` is available on a Razor view, so the setting is read once per page with no plumbing. If `Context` does not resolve, use `ViewContext.HttpContext.Request` instead and note the change in your report.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 5: Add browser coverage of the correction**

A download cannot be made to misbehave in headless Chromium, so the arming half is not reproducible there. The correcting half is where the bugs live and it IS testable directly.

In `tools/uicheck/Program.cs`, inside the `UICHECK_AUTHED` block, add a check modelled on the existing no-JS block near line 317 (which creates its own `BrowserContext` and logs in again, because cookies do not cross contexts). Note `const string De = "retina=1&gray=0&lang=de&fav=";` at line 61 is the settings cookie the authed pass uses; this check needs `ret=1` added to it.

Note that the authed block's `Shot` helper takes an optional page argument, as
`await Shot("read-nojs-de", noJsPage);` in the no-JS block shows. Use that form.

```csharp
    // Return-after-download: a download cannot be made to misbehave in headless
    // Chromium, so the arming half is not reproducible here. The CORRECTING half
    // is where the bugs live and it is testable directly: seed the record the
    // script would have written, land somewhere else, and assert it sends us back.
    // Needs its own BrowserContext because the setting rides in a cookie, and its
    // own login because cookies do not cross contexts.
    var retCtx = await browser.NewContextAsync(new()
    {
        ViewportSize = new() { Width = vpW, Height = vpH },
    });
    await retCtx.AddCookiesAsync([ new() { Name = "inkshelf_settings", Value = De + "&ret=1", Url = baseUrl } ]);
    var retPage = await retCtx.NewPageAsync();
    try
    {
        await retPage.GotoAsync(baseUrl + "/login");
        await retPage.FillAsync("input[name=Username]", "root");
        await retPage.FillAsync("input[name=Password]", "root");
        await retPage.ClickAsync("button[type=submit]");
        await retPage.WaitForSelectorAsync("text=Bibliotheken", new() { Timeout = 15000 });

        // Land on a listing and seed the record the arming half would have stored.
        await retPage.ClickAsync("a[href^='/library/']");
        await retPage.WaitForLoadStateAsync();
        var listingUrl = retPage.Url;
        await retPage.EvaluateAsync(
            "sessionStorage.setItem('inkshelf.dlreturn', location.pathname + location.search)");

        // Go somewhere else, as the stale restore would.
        await retPage.GotoAsync(baseUrl + "/");
        await retPage.WaitForLoadStateAsync();
        await Shot("dlreturn-de", retPage);

        if (retPage.Url != listingUrl)
            failures.Add($"dlreturn: expected to be sent back to {listingUrl}, got {retPage.Url}");
        var spent = await retPage.EvaluateAsync<string?>(
            "sessionStorage.getItem('inkshelf.dlreturn')");
        if (spent is not null)
            failures.Add($"dlreturn: record was not spent, still \"{spent}\"");

        // No-op case: a record naming the page we are already on must not navigate.
        await retPage.EvaluateAsync(
            "sessionStorage.setItem('inkshelf.dlreturn', location.pathname + location.search)");
        var beforeReload = retPage.Url;
        await retPage.ReloadAsync();
        await retPage.WaitForLoadStateAsync();
        if (retPage.Url != beforeReload)
            failures.Add($"dlreturn-noop: navigated from {beforeReload} to {retPage.Url}");
        var spentNoop = await retPage.EvaluateAsync<string?>(
            "sessionStorage.getItem('inkshelf.dlreturn')");
        if (spentNoop is not null)
            failures.Add($"dlreturn-noop: record was not cleared, still \"{spentNoop}\"");
    }
    catch (Exception ex)
    {
        failures.Add($"dlreturn: {ex.Message}");
        try { await Shot("dlreturn-error", retPage); } catch { }
    }
```

Adjust the login selectors and the wait text only if the neighbouring blocks use
different ones; copy from them rather than from here if they differ.

If either assertion fails, that is a real bug in the script. STOP and report it
rather than adjusting the test until it passes.

- [ ] **Step 6: Run the browser pass**

Run: `tools/uicheck/run.sh`
Expected: exit 0 at both viewport widths. Open the screenshots in `tools/uicheck/shots/` and confirm the landed page is the listing. Exit code alone is not sufficient.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_Layout.cshtml tests/Inkshelf.Tests/ListingRenderTests.cs tools/uicheck/Program.cs
git commit -m "feat: return to the listing after a download"
```

---

### Task 4: Documentation

**Files:**
- Modify: `docs/tolino.md`
- Modify: `docs/DEVICES.md`
- Modify: `docs/FAQ.md`
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: the finished feature.
- Produces: nothing code-facing.

- [ ] **Step 1: Record the mechanism where reader behaviour lives**

`docs/tolino.md` is the home for reader-engine behaviour. Add a short subsection under its reader-engine material recording what the spike established: the browser is killed when the book reader takes the foreground and restored from a stale snapshot, `history.length` grows rather than shrinking, and therefore no markup change can prevent it. Name the setting as the workaround. Keep it to a short paragraph plus the essential measurements; the full findings live in the spec.

- [ ] **Step 2: Note it on the device row**

In `docs/DEVICES.md`, extend the Tolino epos 2 row's *Working settings* cell to mention the setting, preserving the existing text about page scale and enlarge small pages. It is an HTML table; keep it well formed.

- [ ] **Step 3: Add a FAQ entry**

In `docs/FAQ.md`, add an entry in the existing register (short, declarative, second person) for the symptom a user would search by: after downloading, the reader leaves the list. Say what the setting does and that it costs an extra page load.

- [ ] **Step 4: Move the issue to Done**

Add an entry to `docs/ROADMAP.md`'s `## Done` section matching the format the existing entries use (`- **Title** (#N) - description`), noting it closes #68.

Do NOT add anything to `docs/ARCHITECTURE.md`. It is a map, not a diary, and `CLAUDE.md` is explicit that a per-feature entry there is a smell. The one thing that would belong is an invariant, and this feature's invariant (do not arm an anchor that does not navigate) is already recorded as a comment next to the code it constrains, which is the right place for it. If on reading the file you believe an existing bullet genuinely needs qualifying, say why in your report rather than adding a bullet.

- [ ] **Step 5: Verify no banned dashes across the branch**

Run: `git diff main | grep -nP '^\+.*[\x{2013}\x{2014}]'`
Expected: no matching lines printed.

- [ ] **Step 6: Commit**

```bash
git add docs/
git commit -m "docs: document the return-after-download setting"
```

---

## After the plan

`tools/uicheck/run.sh` covers the correcting half in a real browser, but the misbehaviour this feature exists for does not occur in any browser CI can run, so **a device pass by the user is mandatory**.

The EPUB-on-`/converted` path is the motivating workflow and is the one path the spike never exercised: the probes only ever armed the raw Download link. The user has agreed to test it once the PR is up. If the Cached EPUB anchor behaves differently from the raw Download anchor, the design holds but that anchor may need separate treatment, and that would be a follow-up rather than a change to this plan.

Then use `superpowers:finishing-a-development-branch`.
