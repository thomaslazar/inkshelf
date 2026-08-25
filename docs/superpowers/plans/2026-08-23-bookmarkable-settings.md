# Bookmarkable Device Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a device restore its settings from a bookmarked URL, so a reader that loses its cookies on every browser restart no longer needs its screen override re-measured by hand.

**Architecture:** The settings cookie's value is already a query string (`retina=1&gray=0&…`). One parser serves both sources: the cookie and - on `/settings` only - the request query. Saving redirects to `/settings?<serialized settings>`, so the page you land on is the page that restores what you saved, captured with the browser's own bookmark button.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 10), xUnit, `WebApplicationFactory` integration tests, `tools/uicheck` Playwright pass.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-23-bookmarkable-settings-design.md`.
- The recognised settings keys are exactly: `retina`, `gray`, `lang`, `fav`, `did`, `spread`, `scale`, `ovr`, `ovrw`, `ovrh`, `ovrd`. `range` and `scalerange` are warning markers, not settings.
- Query settings are honoured on `/settings` **only**. No other page may read settings from the query.
- Wholesale replacement: absent keys fall to documented defaults, never to the device's current values.
- No new sanitisation. Every field already passes `SanitizeDim` / `SanitizeDpr` / `SanitizeScale` / `SanitizeId` / `SanitizeLang`; a URL is the same trust boundary as the cookie.
- Session tokens never travel in a URL.
- Run `dotnet test` from the repo root. Run `dotnet format --verify-no-changes` before each commit.
- Commit style: Conventional Commits, imperative lowercase subject, max ~72 chars, no `Co-Authored-By` trailer.
- Do NOT edit `CHANGELOG.md`.

---

### Task 1: One parser for cookie and query

Extract the keyed-settings parsing out of `Read` so the same code serves a cookie value and an `IQueryCollection`. `FromQuery` returns `null` when the query carries none of the recognised keys, which is how a plain page load is told apart from a restore.

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs` (the `Read` method, currently at lines 83-117)
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public static DeviceSettings? FromQuery(IQueryCollection q)` - settings parsed from a query, or `null` when no recognised key is present. Does not read cookies and does not apply `LegacyFav`.
  - `Read(HttpRequest)` keeps its existing signature and behaviour.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`. `RequestWithCookie` already exists in that file; use it as-is.

```csharp
    // The cookie's value and a bookmark's query are the same wire format, so one
    // parser must serve both - otherwise the two drift and a restored bookmark
    // means something subtly different from the cookie it came from.
    [Fact]
    public void FromQuery_matches_the_cookie_parser_for_the_same_string()
    {
        var wire = new DeviceSettings(true, false, "de")
        {
            Fav = "lib_abc",
            Did = "9c2f1a4b8e07d631",
            Spread = SpreadMode.RotateLeft,
            Scale = 98,
            OverrideScreen = true,
            OverrideW = 1120,
            OverrideH = 1355,
            OverrideDpr = 1.325,
        }.Serialize();

        var fromCookie = DeviceSettings.Read(RequestWithCookie(wire));
        var fromQuery = DeviceSettings.FromQuery(
            new QueryCollection(QueryHelpers.ParseQuery(wire)));

        Assert.Equal(fromCookie, fromQuery);
    }

    [Fact]
    public void FromQuery_is_null_when_no_settings_key_is_present()
    {
        // `range` and `scalerange` are the save page's warning markers. A URL
        // carrying only those is not a restore and must not overwrite anything.
        Assert.Null(DeviceSettings.FromQuery(
            new QueryCollection(QueryHelpers.ParseQuery("range=1&scalerange=1"))));
        Assert.Null(DeviceSettings.FromQuery(new QueryCollection(new Dictionary<string, StringValues>())));
    }

    [Fact]
    public void FromQuery_accepts_a_single_recognised_key_and_defaults_the_rest()
    {
        // Wholesale replacement: everything absent lands on the documented
        // default, NOT on whatever the device had.
        var s = DeviceSettings.FromQuery(
            new QueryCollection(QueryHelpers.ParseQuery("ovrw=1120")));

        Assert.NotNull(s);
        Assert.Equal(1120, s!.OverrideW);
        Assert.Equal(DeviceSettings.Default.Retina, s.Retina);
        Assert.Equal(DeviceSettings.Default.Scale, s.Scale);
        Assert.Equal(DeviceSettings.Default.Spread, s.Spread);
    }

    [Fact]
    public void FromQuery_sanitises_hostile_values()
    {
        var s = DeviceSettings.FromQuery(new QueryCollection(QueryHelpers.ParseQuery(
            "ovr=1&ovrw=99999&ovrh=1355&ovrd=abc&did=..%2F..%2Fx&scale=400")));

        Assert.NotNull(s);
        Assert.Equal(0, s!.OverrideW);          // out of range → inactive
        Assert.Equal(0, s.OverrideDpr);          // unparseable → 0
        Assert.Equal("", s.Did);                 // rejected, re-minted by Set
        Assert.Equal(DeviceSettings.Default.Scale, s.Scale);
    }
```

Add these `using` directives at the top of the test file if not already present:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FromQuery`
Expected: FAIL - compile error, `DeviceSettings` has no member `FromQuery`.

- [ ] **Step 3: Implement**

In `src/Inkshelf/Auth/DeviceSettings.cs`, replace the body of `Read` from `var q = QueryHelpers.ParseQuery(v);` to the end of the method with a delegation, and add `FromQuery` plus the key list below it. `Read` keeps applying `LegacyFav` for the `fav` fallback; `FromQuery` does not, because a query has no legacy cookie behind it.

```csharp
    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v))
            return Default with { Fav = LegacyFav(req) };

        // No '=' means the legacy positional shape ("10", "10de"). Written before
        // the keyed format; parsed here so existing devices keep their settings.
        if (!v.Contains('=')) return ReadLegacy(v) with { Fav = LegacyFav(req) };

        var q = new QueryCollection(QueryHelpers.ParseQuery(v));
        var s = Parse(q);
        // PRESENCE, not emptiness. `fav=` present-but-empty means deliberately
        // un-favorited; falling back to the legacy cookie on empty would
        // resurrect a favorite the user just cleared.
        return q.ContainsKey("fav") ? s : s with { Fav = LegacyFav(req) };
    }

    // The keys Serialize writes, and nothing else. A query carrying none of them
    // is not a settings payload - `range`/`scalerange` are warning markers.
    private static readonly string[] Keys =
        ["retina", "gray", "lang", "fav", "did", "spread", "scale", "ovr", "ovrw", "ovrh", "ovrd"];

    // Settings from a URL query, or null when it carries none of Keys. The cookie
    // and a bookmarked URL are the same wire format, so both go through Parse and
    // cannot drift apart.
    public static DeviceSettings? FromQuery(IQueryCollection q)
    {
        foreach (var k in Keys)
            if (q.ContainsKey(k)) return Parse(q);
        return null;
    }

    private static DeviceSettings Parse(IQueryCollection q) =>
        new DeviceSettings(
            Flag(q, "retina", Default.Retina),
            Flag(q, "gray", Default.Grayscale),
            q.TryGetValue("lang", out var lang) ? SanitizeLang(lang.ToString()) : Default.Lang)
        {
            Fav = q.TryGetValue("fav", out var fav) ? SanitizeId(fav.ToString()) : "",
            Did = q.TryGetValue("did", out var did) ? SanitizeId(did.ToString()) : "",
            // Absent (a cookie written before these settings existed) or unparseable →
            // the documented default, NOT the enum's zero value / a zero scale.
            Spread = q.TryGetValue("spread", out var sp) && Enum.TryParse<SpreadMode>(sp.ToString(), true, out var sm)
                ? sm : Default.Spread,
            Scale = q.TryGetValue("scale", out var sc) && int.TryParse(sc.ToString(), out var pc)
                ? SanitizeScale(pc) : Default.Scale,
            OverrideScreen = Flag(q, "ovr", Default.OverrideScreen),
            OverrideW = q.TryGetValue("ovrw", out var ow) && int.TryParse(ow.ToString(), out var owv)
                ? SanitizeDim(owv) : 0,
            OverrideH = q.TryGetValue("ovrh", out var oh) && int.TryParse(oh.ToString(), out var ohv)
                ? SanitizeDim(ohv) : 0,
            OverrideDpr = q.TryGetValue("ovrd", out var od) ? SanitizeDpr(ParseDpr(od.ToString())) : 0,
        };
```

`Flag` currently takes `Dictionary<string, StringValues>`. Change its parameter type to `IQueryCollection` - its body needs no other change:

```csharp
    private static bool Flag(IQueryCollection q, string key, bool fallback) =>
        q.TryGetValue(key, out var v) ? v.ToString() == "1" : fallback;
```

Check whether `Flag`'s existing signature differs from the above; match the real one and change only the parameter type. Add `using Microsoft.AspNetCore.Http;` to the file if it is not already there.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, and the whole suite still green - `Read` is used everywhere, so a regression here shows up broadly. Pay attention to the existing `Fav` tests: `fav=` present-but-empty must still mean "deliberately un-favorited" while an absent `fav` falls back to the legacy cookie.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf/Auth/DeviceSettings.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs
git commit -m "refactor: parse settings from a query or the cookie with one parser"
```

---

### Task 2: Restore settings from the URL on /settings

`GET /settings` carrying recognised keys applies them: sanitised, written to the cookie, rendered in the fields.

**Files:**
- Modify: `src/Inkshelf/Pages/Settings.cshtml.cs` (the `OnGet` method)
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `DeviceSettings.FromQuery(IQueryCollection)` from Task 1.
- Produces: no new API. `/settings?<keys>` writes the settings cookie as a side effect of the GET.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EndpointTests.cs`. `CreateFactory` already exists there; use it as-is.

```csharp
    // A bookmarked URL is how a device that loses its cookies every restart gets
    // its measured override back. Opening it must write the cookie, not just
    // render the values for one request.
    [Fact]
    public async Task Settings_from_the_url_are_applied_and_stored()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync(
            "/settings?retina=1&gray=0&lang=&fav=&did=9c2f1a4b8e07d631&spread=rotateleft"
            + "&scale=98&ovr=1&ovrw=1120&ovrh=1355&ovrd=1.325");

        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        var setCookie = res.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(";", v) : "";
        Assert.Contains("ovrw%3D1120", setCookie);
        Assert.Contains("ovrh%3D1355", setCookie);
        Assert.Contains("scale%3D98", setCookie);

        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("value=\"1120\"", html);   // prefilled into the override field
    }

    [Fact]
    public async Task A_plain_settings_page_load_does_not_write_settings()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync("/settings?range=1");

        var setCookie = res.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(";", v) : "";
        Assert.DoesNotContain("inkshelf_settings", setCookie);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter Settings_from_the_url_are_applied_and_stored`
Expected: FAIL - no `Set-Cookie` for settings, because nothing reads the query yet.

- [ ] **Step 3: Implement**

In `src/Inkshelf/Pages/Settings.cshtml.cs`, replace the first line of `OnGet` (`Settings = DeviceSettings.Read(Request);`) with:

```csharp
        // A bookmarked URL carrying settings IS the restore mechanism for devices
        // that lose their cookies on a browser restart: apply it and store it, so
        // the rest of the session behaves as if the values had been typed in.
        // Only this page honours query settings - see the spec.
        var restored = DeviceSettings.FromQuery(Request.Query);
        Settings = restored is { } r ? DeviceSettings.Set(Response, r) : DeviceSettings.Read(Request);
```

`DeviceSettings.Set` returns the settings it wrote, with a device id minted when the URL carried none.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf/Pages/Settings.cshtml.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: restore device settings from a bookmarked url"
```

---

### Task 3: Saving lands on the restorable URL

The POST-redirect-GET target carries the saved settings, so the page you land on after Save is the one to bookmark.

**Files:**
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs` (the redirect at the end of the POST handler)
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: Task 2's behaviour - the redirect target must be a URL that Task 2 honours.
- Produces: no new API.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/EndpointTests.cs`. `GetAntiforgeryTokenAsync` already exists there.

```csharp
    // The page you land on after saving is the page to bookmark, so the redirect
    // has to carry the values - and following it must reproduce them, which is
    // what makes the bookmark work at all.
    [Fact]
    public async Task Saving_redirects_to_a_url_that_restores_the_same_settings()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["retina"] = "on",
            ["lang"] = "de",
            ["scale"] = "98",
            ["ovr"] = "on",
            ["ovrw"] = "1120",
            ["ovrh"] = "1355",
            ["ovrd"] = "1.325",
        });

        var res = await client.PostAsync("/settings", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.OriginalString;
        Assert.StartsWith("/settings?", location);
        Assert.Contains("ovrw=1120", location);
        Assert.Contains("ovrh=1355", location);
        Assert.Contains("scale=98", location);
        Assert.Contains("lang=de", location);

        // Following it restores the same values on a client with no cookies.
        using var fresh = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var restored = await fresh.GetAsync(location);
        Assert.Equal(System.Net.HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains("value=\"1120\"", await restored.Content.ReadAsStringAsync());
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter Saving_redirects_to_a_url_that_restores`
Expected: FAIL - the location is `/settings`, so `Assert.StartsWith("/settings?", …)` fails.

- [ ] **Step 3: Implement**

In `src/Inkshelf/Endpoints/SettingsEndpoints.cs`, replace the PRG block at the end of the POST handler:

```csharp
            // PRG back to the page - carrying the saved settings, so the URL in the
            // address bar is one a device can bookmark to restore them. Warning
            // flags ride along as extra params; they are not settings keys.
            var flags = (unusable ? "&range=1" : "") + (scaleRejected ? "&scalerange=1" : "");
            return Results.Redirect($"/settings?{settings.Serialize()}{flags}");
```

Delete the previous two lines that built `flags` without leading `&` and the conditional `Results.Redirect(flags.Length == 0 ? "/settings" : $"/settings?{flags}")`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS. `Settings_post_sets_cookie_and_redirects` asserts `response.Headers.Location == "/settings"`; it now needs to assert `StartsWith("/settings?")` instead. Update that assertion - it is testing the same behaviour, which has deliberately changed.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf/Endpoints/SettingsEndpoints.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: land on a bookmarkable url after saving settings"
```

---

### Task 4: Tell the user what the bookmark does

One sentence on the settings page, in both languages, plus uicheck coverage. Without it the feature is invisible: nothing on screen suggests the URL is worth keeping.

**Files:**
- Modify: `src/Inkshelf/Pages/Settings.cshtml`
- Modify: `src/Inkshelf/locales/de.json`
- Modify: `tools/uicheck/Program.cs`

**Interfaces:**
- Consumes: Tasks 2 and 3.
- Produces: the localised string key `Bookmark this page to restore these settings later.`

- [ ] **Step 1: Add the sentence to the view**

In `src/Inkshelf/Pages/Settings.cshtml`, immediately after the existing `<p class="settings-note">@L["These settings apply to this device / browser only."]</p>` line, add:

```html
<p class="settings-note">@L["Bookmark this page to restore these settings later."]</p>
```

- [ ] **Step 2: Add the German string**

In `src/Inkshelf/locales/de.json`, next to the existing `"Automatic"` entry, add:

```json
  "Bookmark this page to restore these settings later.": "Diese Seite als Lesezeichen speichern, um diese Einstellungen später wiederherzustellen.",
```

Keep the file valid JSON - check for a trailing comma problem by running the tests, which load the catalog.

- [ ] **Step 3: Assert it in the browser pass**

In `tools/uicheck/Program.cs`, add the new strings to the two settings checks. In the German check's `mustContain` array add `"als Lesezeichen speichern"`, and in the English check's `mustContain` array add `"Bookmark this page"`.

- [ ] **Step 4: Run the tests and the browser pass**

Run: `dotnet test`
Expected: PASS - `LocalizationCatalogTests` and `LocalizationIntegrationTests` load the German catalog, so malformed JSON fails here.

Run: `PORT=5130 tools/uicheck/run.sh`
Expected: `PASS` for both viewports. Read `tools/uicheck/shots/settings-de.png` and confirm the sentence renders and does not push the first Save button off the visible area.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf/Pages/Settings.cshtml src/Inkshelf/locales/de.json tools/uicheck/Program.cs
git commit -m "feat: say that the settings page can be bookmarked"
```

---

### Task 5: Guard the invariant that other pages ignore query settings

The spec's central safety property - only `/settings` honours query settings - is currently true by construction rather than by test. Lock it down so a future change to `DeviceSettings.Read` cannot quietly turn every URL into a settings link.

**Files:**
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: nothing.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`. That file's `CreateFactory`, `MakeStub`, `LibraryRequest`, `ItemId`, `TempDir` helpers already exist; use them as-is.

```csharp
    // Query settings are honoured on /settings ONLY. A link is allowed to change
    // settings on the page where changing settings is the point - nowhere else,
    // or any URL anyone sends becomes a silent settings rewrite.
    [Fact]
    public async Task A_listing_url_carrying_settings_keys_ignores_them()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = LibraryRequest(factory, settings: null);
        req.RequestUri = new Uri($"/library/{LibId}?ovr=1&ovrw=1120&ovrh=1355&ovrd=1.325", UriKind.Relative);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var setCookie = res.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(";", v) : "";
        Assert.DoesNotContain("ovrw%3D1120", setCookie);
    }
```

If `LibraryRequest`'s signature does not accept a null settings cookie, call it the way the neighbouring tests in that file do and set `RequestUri` afterwards as above. `LibId` is the existing library-id constant in that file.

- [ ] **Step 2: Run the test**

Run: `dotnet test --filter A_listing_url_carrying_settings_keys_ignores_them`
Expected: PASS immediately - this is a regression guard for behaviour Task 2 deliberately scoped, not new behaviour. If it FAILS, query settings are leaking outside `/settings`: stop and fix that before continuing.

- [ ] **Step 3: Commit**

```bash
dotnet format --verify-no-changes
git add tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "test: pin query settings to the settings page"
```

---

### Task 6: Document it

**Files:**
- Modify: `docs/FAQ.md`
- Modify: `docs/DEVICES.md` (the shine's Notes row)
- Modify: `docs/ROADMAP.md` (the `## Done` section)

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: nothing.

- [ ] **Step 1: Update the FAQ answer that says nothing can be done**

In `docs/FAQ.md`, replace the body of the "I have to log in again whenever I reopen the browser" entry with:

```markdown
Some older readers keep no cookies across a browser restart, and the device
settings go with them. Logging in again is unavoidable, but the settings are not:
save them once and bookmark the settings page you land on. Opening that bookmark
restores everything, including the screen override and the device's download
marks.
```

- [ ] **Step 2: Update the shine's note in the matrix**

In `docs/DEVICES.md`, in the `Notes on the shine` cell, replace `so the login and every setting - the override included - are re-entered each session` with:

```html
        so you log in again each session; bookmark the settings page and its
        values come back with one tap
```

Keep the surrounding sentence grammatical - read the whole cell after editing.

- [ ] **Step 3: Record it as shipped**

In `docs/ROADMAP.md`, add a bullet to the `## Done` section matching the style of the entries already there, describing that device settings can be restored from a bookmarked URL. Read the neighbouring entries first and match their voice and level of detail.

- [ ] **Step 4: Commit**

```bash
git add docs/FAQ.md docs/DEVICES.md docs/ROADMAP.md
git commit -m "docs: record bookmarkable settings"
```

---

## Verification before handing back

- [ ] `dotnet test` - whole suite green.
- [ ] `dotnet format --verify-no-changes` - clean.
- [ ] `PORT=5130 tools/uicheck/run.sh` - PASS at both viewports, and `settings-de.png` read, not just the exit code.
- [ ] Manual round trip against the seeded ABS: save settings, copy the redirect URL, clear cookies, open the URL, confirm the fields come back.
- [ ] `docs/ARCHITECTURE.md` deliberately untouched: this adds no new invariant and changes no structure. The rule for that file is that most features change it not at all.
