# Structured Settings Cookie Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `DeviceSettings`' positional cookie encoding (`"10de"`) with a keyed one, and fold the separate `Favorites` cookie into it, so the app ends with one preferences cookie and adding a setting is one key.

**Architecture:** `DeviceSettings` keeps its positional record shape for the three rendering settings and gains `Fav` as an `init` property. The cookie value becomes `retina=1&gray=0&lang=de&fav=lib_x`, parsed with `QueryHelpers.ParseQuery` and written by string interpolation. Legacy positional values are detected by the absence of `=` and parsed by the old code path; the legacy `inkshelf_fav_library` cookie is read as a fallback and deleted on every write. All five `Favorites` call sites move to read-modify-write via `with`.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit. `Microsoft.AspNetCore.WebUtilities.QueryHelpers` (already in the shared framework — no package to add).

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-27-structured-settings-cookie-design.md`. Read it before starting.
- **No new dependencies.** `QueryHelpers` ships in the ASP.NET Core shared framework.
- **All work happens inside the devcontainer.** There is no `dotnet` on the host.
- **Branch:** `refactor/structured-settings-cookie` (already created, spec already committed).
- **Conventional Commits**, imperative lowercase subject, max ~72 chars. Types used here: `refactor`, `test`, `docs`.
- **Do NOT add `Co-Authored-By:` or "Generated with Claude Code" lines to commits.**
- **Do NOT edit `CHANGELOG.md`.** It is written only by the release skill. Shipped work is recorded in `ROADMAP.md`'s Done section and `ARCHITECTURE.md`.
- **`ParseQuery` returns `Dictionary<string, StringValues>`, whose indexer throws `KeyNotFoundException` on a missing key.** Never use `q["key"]`. Always `TryGetValue`.
- **Every key is always written by `Serialize`, including empty ones** (`fav=`). Key *presence* is load-bearing for the un-favorite guard.
- Run the full suite with `dotnet test` from `/workspaces/inkshelf`. It should report **226 passed** before you start.

---

### Task 1: Keyed encoding in `DeviceSettings` (rendering fields only)

Adds the new format and the legacy read path, leaving `Fav` for Task 2. This keeps the encoding change reviewable on its own.

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `DeviceSettings.Serialize()` returning `retina={0|1}&gray={0|1}&lang={code}`; `DeviceSettings.Read(HttpRequest)` handling both the keyed and the legacy positional shapes. Record shape stays `DeviceSettings(bool Retina, bool Grayscale, string Lang)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`. Note the two existing tests that assert the old exact strings — `Serialize_roundtrips_lang` and `Set_writes_essential_root_path_cookie_with_value` — are **replaced** here, not added to.

```csharp
    [Fact]
    public void Serialize_emits_keyed_pairs()
    {
        Assert.Equal("retina=1&gray=0&lang=de&fav=", new DeviceSettings(true, false, "de").Serialize());
        Assert.Equal("retina=1&gray=1&lang=&fav=", new DeviceSettings(true, true, "").Serialize());
    }

    [Fact]
    public void Read_parses_keyed_pairs()
    {
        var s = DeviceSettings.Read(RequestWithCookie("retina=0&gray=1&lang=de&fav="));
        Assert.False(s.Retina);
        Assert.True(s.Grayscale);
        Assert.Equal("de", s.Lang);
    }

    [Fact]
    public void Read_absent_key_falls_back_to_the_documented_default_not_false()
    {
        // Only gray is present. Retina must stay ON — it defaults on, and a naive
        // `q["retina"] == "1"` would silently turn it off.
        var s = DeviceSettings.Read(RequestWithCookie("gray=1"));
        Assert.True(s.Retina);
        Assert.True(s.Grayscale);
        Assert.Equal("", s.Lang);
    }

    [Fact]
    public void Read_unknown_keys_are_ignored()
    {
        var s = DeviceSettings.Read(RequestWithCookie("retina=0&whatever=9&lang=fr"));
        Assert.False(s.Retina);
        Assert.Equal("fr", s.Lang);
    }

    [Fact]
    public void Read_legacy_positional_cookie_still_works()
    {
        Assert.Equal(new DeviceSettings(true, false, "de"), DeviceSettings.Read(RequestWithCookie("10de")));
        Assert.Equal(new DeviceSettings(false, true, ""), DeviceSettings.Read(RequestWithCookie("01")));
    }

    [Fact]
    public void Set_then_Read_roundtrips_through_real_cookie_escaping()
    {
        // The `&` and `=` are escaped to %26/%3D on the way out and unescaped on the
        // way in. This test exists so a framework change can't break that silently.
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, true, "pt-br"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        Assert.Contains("%26", value);              // the separators really are escaped
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"{DeviceSettings.Cookie}={value}";

        var read = DeviceSettings.Read(ctx2.Request);
        Assert.Equal(new DeviceSettings(false, true, "pt-br"), read);
    }
```

Then **delete** these two now-obsolete tests from the same file:

```csharp
    // DELETE — asserts the old positional format
    [Fact]
    public void Serialize_roundtrips_lang()
    {
        Assert.Equal("10de", new DeviceSettings(true, false, "de").Serialize());
        Assert.Equal("11", new DeviceSettings(true, true, "").Serialize());
    }
```

and change the cookie-value assertion in `Set_writes_essential_root_path_cookie_with_value` from the old packed value to the new escaped one:

```csharp
    [Fact]
    public void Set_writes_essential_root_path_cookie_with_value()
    {
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(true, true, ""));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{DeviceSettings.Cookie}=retina%3D1%26gray%3D1", setCookie);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }
```

Leave every other existing test in the file exactly as it is. `Read_parses_flags_and_lang`, `Read_legacy_two_char_cookie_has_empty_lang`, `Read_junk_lang_sanitises_to_empty`, `Read_accepts_region_code`, `Read_accepts_script_subtag_up_to_eight_chars`, `Read_explicit_00_is_both_off_distinct_from_default` and `Read_parses_both_flags` all feed positional values and must keep passing via the legacy path — they are the proof that backward compat works.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests"`
Expected: FAIL. `Serialize_emits_keyed_pairs` reports `Expected: "retina=1&gray=0&lang=de&fav=" Actual: "10de"`.

- [ ] **Step 3: Rewrite `Serialize` and `Read`**

Replace the body of `src/Inkshelf/Auth/DeviceSettings.cs` between the `Default` field and `SanitizeLang` with the following. Add `using Microsoft.AspNetCore.WebUtilities;` and `using Microsoft.Extensions.Primitives;` at the top of the file, keeping the existing `using Microsoft.Extensions.DependencyInjection;`.

```csharp
    // Keyed, NOT positional: "retina=1&gray=0&lang=de&fav=". Looks like a query
    // string because it is parsed by QueryHelpers, but it is a cookie value —
    // Response.Cookies.Append escapes the & and = to %26/%3D and the request side
    // unescapes them. Every key is always written, including empty ones: Read
    // distinguishes "key present but empty" from "key absent" and they mean
    // different things for fav (see Read).
    public string Serialize() =>
        $"retina={(Retina ? 1 : 0)}&gray={(Grayscale ? 1 : 0)}&lang={SanitizeLang(Lang)}&fav=";

    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v))
            return Default;

        // No '=' means the legacy positional shape ("10", "10de"). Written before
        // the keyed format; parsed here so existing devices keep their settings.
        if (!v.Contains('=')) return ReadLegacy(v);

        var q = QueryHelpers.ParseQuery(v);
        return new DeviceSettings(
            Flag(q, "retina", Default.Retina),
            Flag(q, "gray", Default.Grayscale),
            q.TryGetValue("lang", out var lang) ? SanitizeLang(lang.ToString()) : Default.Lang);
    }

    // An absent key means "not specified", which must land on the DOCUMENTED
    // default — retina defaults ON, so a plain `== "1"` would silently flip it off.
    // ParseQuery hands back a plain Dictionary whose indexer THROWS on a missing
    // key, so every lookup goes through TryGetValue.
    private static bool Flag(Dictionary<string, StringValues> q, string key, bool fallback) =>
        q.TryGetValue(key, out var v) && v.Count > 0 ? v[0] == "1" : fallback;

    // Two 0/1 flags then an optional language code, e.g. "10de". Anything
    // malformed → Default.
    private static DeviceSettings ReadLegacy(string v) =>
        v is { Length: >= 2 } && v[0] is '0' or '1' && v[1] is '0' or '1'
            ? new DeviceSettings(v[0] == '1', v[1] == '1', SanitizeLang(v.Length > 2 ? v[2..] : ""))
            : Default;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests"`
Expected: PASS, all tests in the class.

Then run the full suite: `dotnet test`
Expected: PASS, **231** tests — 226 plus the six added, minus the one deleted
(`Serialize_roundtrips_lang`; `Set_writes_essential_root_path_cookie_with_value`
is edited, not removed).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs
git commit -m "refactor: key the settings cookie instead of packing it positionally"
```

---

### Task 2: Add `Fav` to `DeviceSettings` with the legacy-cookie migration

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: `Serialize()`, `Read()`, `Flag()` from Task 1.
- Produces: `DeviceSettings.Fav` (`string`, `init`, default `""`); `DeviceSettings.LegacyFavCookie` (`const string` = `"inkshelf_fav_library"`); `SanitizeId(string)`. `Serialize()` now emits a real `fav=` value. `Set()` additionally deletes the legacy cookie.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/DeviceSettingsTests.cs`. The helper below is needed because these tests set two cookies at once; add it next to the existing `RequestWithCookie`.

```csharp
    private static HttpRequest RequestWithCookies(string? settings, string? legacyFav)
    {
        var ctx = new DefaultHttpContext();
        var parts = new List<string>();
        if (settings is not null) parts.Add($"{DeviceSettings.Cookie}={settings}");
        if (legacyFav is not null) parts.Add($"{DeviceSettings.LegacyFavCookie}={legacyFav}");
        if (parts.Count > 0) ctx.Request.Headers.Cookie = string.Join("; ", parts);
        return ctx.Request;
    }

    [Fact]
    public void Serialize_includes_fav()
    {
        var s = new DeviceSettings(true, false, "de") { Fav = "lib_abc" };
        Assert.Equal("retina=1&gray=0&lang=de&fav=lib_abc", s.Serialize());
    }

    [Fact]
    public void Read_parses_fav()
    {
        Assert.Equal("lib_abc", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=lib_abc")).Fav);
    }

    [Fact]
    public void Read_picks_up_the_legacy_fav_cookie_when_the_key_is_absent()
    {
        // Legacy positional settings + the old separate favorite cookie: the state
        // every device is in at deploy time.
        Assert.Equal("lib_old", DeviceSettings.Read(RequestWithCookies("10de", "lib_old")).Fav);
        // Also when there is no settings cookie at all.
        Assert.Equal("lib_old", DeviceSettings.Read(RequestWithCookies(null, "lib_old")).Fav);
    }

    [Fact]
    public void An_empty_fav_key_does_not_resurrect_the_legacy_cookie()
    {
        // `fav=` present-but-empty means deliberately un-favorited. Falling back to
        // the legacy cookie here would bring back a favorite the user just cleared.
        var s = DeviceSettings.Read(RequestWithCookies("retina=1&gray=0&lang=&fav=", "lib_old"));
        Assert.Equal("", s.Fav);
    }

    [Fact]
    public void Set_deletes_the_legacy_fav_cookie()
    {
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(true, false, "") { Fav = "lib_x" });
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        // Deletion is a Set-Cookie with an expiry in the past.
        Assert.Contains(DeviceSettings.LegacyFavCookie, setCookie);
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("x&retina=0", "x")]          // the injection the form value could carry
    [InlineData("lib_a-b_9", "lib_a-b_9")]   // legitimate ABS id shapes survive
    [InlineData("has space", "")]
    [InlineData("semi;colon", "")]
    [InlineData("per%cent", "")]
    public void Fav_is_sanitized_on_the_way_into_the_cookie(string raw, string expected)
    {
        var s = new DeviceSettings(true, false, "") { Fav = raw };
        Assert.Equal($"retina=1&gray=0&lang=&fav={expected}", s.Serialize());
    }

    [Fact]
    public void Fav_is_sanitized_on_the_way_out_of_the_cookie()
    {
        // A hand-edited cookie must not smuggle an unsafe id into Index's redirect.
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=a b")).Fav);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests"`
Expected: FAIL to **compile** first — `'DeviceSettings' has no property 'Fav'` and `'DeviceSettings' has no member 'LegacyFavCookie'`. That is the expected first failure; a compile error is a legitimate red.

- [ ] **Step 3: Add `Fav`, `SanitizeId`, the legacy fallback and the legacy delete**

In `src/Inkshelf/Auth/DeviceSettings.cs`:

Add the constant and the property just below the existing `Default` field:

```csharp
    public const string LegacyFavCookie = "inkshelf_fav_library";

    // An init property rather than a fourth positional parameter, so the ten
    // existing `new DeviceSettings(a, b, c)` sites in the tests keep compiling —
    // those tests are the regression net for this refactor. Record equality still
    // covers it and `with { Fav = ... }` still works.
    public string Fav { get; init; } = "";
```

Replace `Serialize()` so it emits the real value:

```csharp
    public string Serialize() =>
        $"retina={(Retina ? 1 : 0)}&gray={(Grayscale ? 1 : 0)}"
        + $"&lang={SanitizeLang(Lang)}&fav={SanitizeId(Fav)}";
```

Replace the three `return` sites in `Read` so each resolves `Fav`:

```csharp
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v))
            return Default with { Fav = LegacyFav(req) };

        if (!v.Contains('=')) return ReadLegacy(v) with { Fav = LegacyFav(req) };

        var q = QueryHelpers.ParseQuery(v);
        return new DeviceSettings(
            Flag(q, "retina", Default.Retina),
            Flag(q, "gray", Default.Grayscale),
            q.TryGetValue("lang", out var lang) ? SanitizeLang(lang.ToString()) : Default.Lang)
        {
            // PRESENCE, not emptiness. `fav=` present-but-empty means deliberately
            // un-favorited; falling back to the legacy cookie on empty would
            // resurrect a favorite the user just cleared.
            Fav = q.TryGetValue("fav", out var fav) ? SanitizeId(fav.ToString()) : LegacyFav(req),
        };
```

Add the two helpers next to `SanitizeLang`:

```csharp
    private static string LegacyFav(HttpRequest req) =>
        req.Cookies.TryGetValue(LegacyFavCookie, out var v) ? SanitizeId(v) : "";

    // An opaque ABS library id, so allow only what those ids use. This is a trust
    // boundary, not tidiness: `libraryId` arrives from a form POST, and a value
    // containing '&' would inject extra keys into the cookie we write. Rejecting
    // '%' also rules out double-decoding, since ParseQuery URL-decodes a value the
    // cookie layer already unescaped once.
    private static string SanitizeId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return "";
        return s;
    }
```

In `Set`, after the existing `res.Cookies.Append(...)` call, add:

```csharp
        // The favorite now lives in the settings cookie. Drop the old one so it
        // can't linger and shadow a later un-favorite.
        res.Cookies.Delete(LegacyFavCookie, new CookieOptions { Path = "/" });
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DeviceSettingsTests"`
Expected: PASS.

Then: `dotnet test`
Expected: PASS, **242** tests — 231 plus eleven, because xUnit counts each
`[InlineData]` of `Fav_is_sanitized_on_the_way_into_the_cookie` as its own test
(six `[Fact]`s + five theory cases).

Note `Serialize_emits_keyed_pairs` from Task 1 already expects the trailing
`fav=`, so it needs no change.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs
git commit -m "refactor: move the favorite library into the settings cookie"
```

---

### Task 3: Move the five `Favorites` call sites and delete the class

**Files:**
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs:18-20`
- Modify: `src/Inkshelf/Endpoints/SessionEndpoints.cs:30-31`
- Modify: `src/Inkshelf/Pages/Index.cshtml.cs:22-30`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs:47`
- Delete: `src/Inkshelf/Auth/Favorites.cs`
- Delete: `tests/Inkshelf.Tests/FavoritesTests.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: `DeviceSettings.Read`, `.Set`, `.Fav`, `with` from Task 2.
- Produces: no new API. `Inkshelf.Auth.Favorites` no longer exists.

- [ ] **Step 1: Write the failing test for the clobber hazard**

This is the bug this task can introduce, so it gets a real end-to-end test first. It drives the actual HTTP endpoints, because the hazard lives in the endpoint wiring, not in `DeviceSettings`.

Add to `tests/Inkshelf.Tests/EndpointTests.cs`, which already has the `CreateFactory` and `GetAntiforgeryTokenAsync` helpers this uses. Neither `POST /favorite` nor `POST /settings` calls ABS — both only read and write cookies and redirect — so no ABS stubbing is needed.

```csharp
    [Fact]
    public async Task Saving_settings_keeps_the_favorite_library()
    {
        // The hazard of one shared cookie: a settings save that builds a fresh
        // DeviceSettings instead of using `with` silently wipes the favorite, and
        // the symptom (favorite vanishes after visiting Settings) points nowhere
        // near the cause.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var token = await GetAntiforgeryTokenAsync(client);

        // Favorite a library, then save unrelated settings. Both go through the
        // client's own cookie container — do NOT set a Cookie header by hand, it
        // fights the container and drops the antiforgery cookie.
        var fav = await client.PostAsync("/favorite", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["libraryId"] = "lib_keep",
            }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, fav.StatusCode);

        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["grayscale"] = "on",
                ["lang"] = "de",
            }));

        var setCookie = string.Join(" ", saved.Headers.GetValues("Set-Cookie"));
        Assert.Contains("fav%3Dlib_keep", setCookie);   // the favorite survived the save
        Assert.Contains("gray%3D1", setCookie);         // and the new choice was applied
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Saving_settings_keeps_the_favorite"`
Expected: FAIL. After Task 2 the settings POST still builds a fresh `DeviceSettings`, so it writes `fav=` and the assertion reports the `fav%3Dlib_keep` substring missing. This is a genuine red — the exact bug, before the fix.

- [ ] **Step 3: Update `SettingsEndpoints.cs`**

Replace lines 18–20 (`var settings = new DeviceSettings(...)` through `DeviceSettings.Set(...)`) with:

```csharp
            // `with`, NOT a fresh instance — the favorite lives in this same cookie
            // and constructing a new record would wipe it.
            var settings = DeviceSettings.Read(ctx.Request) with
            {
                Retina = form.ContainsKey("retina"),
                Grayscale = form.ContainsKey("grayscale"),
                Lang = form["lang"].ToString(),
            };
            DeviceSettings.Set(ctx.Response, settings);
```

- [ ] **Step 4: Update `SessionEndpoints.cs`**

Replace lines 30–31 (the `if (Favorites.Read(...)) ... else ...` pair) with:

```csharp
            // Toggle: favoriting the library you already favorited clears it.
            var s = DeviceSettings.Read(ctx.Request);
            DeviceSettings.Set(ctx.Response, s with { Fav = s.Fav == libraryId ? "" : libraryId });
```

The file's `using Inkshelf.Auth;` already covers `DeviceSettings`.

- [ ] **Step 5: Update `Index.cshtml.cs`**

Replace the body of `OnGetAsync` from `var fav = Favorites.Read(Request);` through the `Favorites.Clear(Response);` line with:

```csharp
        var settings = DeviceSettings.Read(Request);
        var fav = settings.Fav;
        if (!string.IsNullOrEmpty(fav) && string.IsNullOrEmpty(all))
        {
            // Only honor the favorite if it still exists on the ABS we're pointed
            // at now — a cookie saved against a different ABS would otherwise
            // redirect into a library this one doesn't have. Drop the stale
            // favorite and fall through to the list rather than looping on a dead
            // link.
            if (Libraries.Any(l => l.Id == fav)) return Redirect($"/library/{fav}");
            DeviceSettings.Set(Response, settings with { Fav = "" });
        }
```

Add `using Inkshelf.Auth;` to the top of the file if it is not already there.

- [ ] **Step 6: Update `Library.cshtml.cs`**

Replace line 47:

```csharp
        IsFavorite = DeviceSettings.Read(Request).Fav == Id;
```

- [ ] **Step 7: Delete the dead class and its tests**

```bash
git rm src/Inkshelf/Auth/Favorites.cs tests/Inkshelf.Tests/FavoritesTests.cs
```

`FavoritesTests.cs` held the forced/default `Secure` pair. That rule is already covered for the surviving cookie by `DeviceSettingsTests.Set_forces_secure_flag_when_configured` and `Set_omits_secure_flag_on_http_by_default`, so nothing is lost.

- [ ] **Step 8: Fix `FavoriteLibraryRoutingTests`**

Run: `dotnet build`
Expected: two errors in `tests/Inkshelf.Tests/FavoriteLibraryRoutingTests.cs`, at the two `Favorites.Cookie` references. Make exactly these changes.

Line 32, inside the `WithContext` helper — seed the favorite inside the settings cookie instead of its own:

```csharp
        // The favorite lives in the settings cookie now. A raw unescaped value is
        // fine in a request Cookie header: parsing splits on ';' and the first '=',
        // and unescaping is a no-op without '%'.
        if (favCookie is not null)
            http.Request.Headers.Cookie = $"{DeviceSettings.Cookie}=retina=1&gray=0&lang=&fav={favCookie}";
```

Line 55, in `Index_drops_a_stale_favorite_and_shows_the_list` — the stale-clear now rewrites the settings cookie rather than deleting a cookie, so the `expires=…1970` assertion on the following line no longer applies to it. Replace both lines:

```csharp
        var setCookie = model.Response.Headers.SetCookie.ToString();
        Assert.Contains(DeviceSettings.Cookie, setCookie);   // the stale favorite is cleared
        Assert.Contains("fav%3D;", setCookie);               // ...by writing an empty fav
```

`fav` is the last key and `Set-Cookie` always continues with `; path=/`, so
`fav%3D;` is the empty-favorite signature.

Leave the other three tests in the file untouched — they exercise the redirect behavior, which is unchanged.

- [ ] **Step 9: Run the full suite**

Run: `dotnet test`
Expected: PASS, **241** tests — 242 plus the one added in Step 1, minus the two deleted `FavoritesTests`.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "refactor: read the favorite from the settings cookie everywhere"
```

---

### Task 4: Verify in a browser, then docs

The change touches the favorite-redirect flow and the settings form — both plain-HTML paths that tests exercise only at the unit level. A browser pass comes before the docs so a broken flow is found while the code is still fresh.

**Files:**
- Modify: `docs/ARCHITECTURE.md` (three places, see Step 3)
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: everything from Tasks 1–3.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Run the app**

```bash
cd /workspaces/inkshelf && dotnet run --project src/Inkshelf --urls http://localhost:5099
```

Port 5099 is the one the e-reader's bookmark points at — use it, not the 5197 in `launchSettings.json`.

- [ ] **Step 2: Walk the four flows that changed**

With a browser (Playwright .NET is available in the devcontainer; there is no `sudo`, so do not attempt a browser install that needs it):

1. Log in, open a library, click the favorite control. Expect a redirect to that library.
2. Return to `/`. Expect the automatic redirect into the favorited library.
3. Go to `/settings`, toggle grayscale, save. Then go to `/`. **Expect the favorite redirect to still happen** — this is the clobber hazard; if the favorite is gone, `SettingsEndpoints` is not using `with`.
4. Click the favorite control again on the same library to un-favorite. Go to `/`. Expect the library *list*, and expect it to stay that way on reload — if the favorite comes back, the legacy-cookie delete or the presence check is wrong.

Also confirm with devtools that only `inkshelf_settings` is present and `inkshelf_fav_library` is gone after any save.

- [ ] **Step 3: Update `ARCHITECTURE.md`**

Three places name `Favorites` or describe two preferences cookies. Make exactly these three replacements.

**1. The `Auth/` directory map (~line 30).** Replace:

```
  Auth/                 TokenStore (encrypted cookie), Tokens, Favorites (fav-library
                        cookie), DeviceSettings (per-device settings cookie).
```

with:

```
  Auth/                 TokenStore (encrypted cookie), Tokens, DeviceSettings
                        (per-device settings + favorite library, one cookie).
```

**2. The cookie `Secure` rule (~line 116).** Replace:

```
  `ForceSecureCookies || Request.IsHttps`. `TokenStore` and `Favorites` must apply
  the same rule — keep them in sync.
```

with:

```
  `ForceSecureCookies || Request.IsHttps`. `TokenStore` and `DeviceSettings` must
  apply the same rule — keep them in sync.
```

**3. "Two device cookies, two purposes" (~line 140).** The `scr` versus
`inkshelf_settings` split is still the meaningful distinction, so the heading and
its first half stay. Append to the end of that bullet:

```
  The settings cookie is a **keyed** value (`retina=1&gray=0&lang=de&fav=lib_x`),
  parsed with `QueryHelpers.ParseQuery`: a new setting is one key, and an absent
  key falls back to that setting's own default rather than to `false`. The
  favorite library is a field in it, not a second cookie — so every write is a
  read-modify-write via `with`, or it silently drops the other fields.
```

Keep the house style: present-tense design description, no changelog, no "shipped" status, no per-change enumeration.

- [ ] **Step 4: Move the roadmap item to Done**

In `docs/ROADMAP.md`, delete the **Structured settings cookie (refactor)** bullet from the Settings section and add to the top of `## Done`:

```markdown
- **Structured settings cookie** — `DeviceSettings` stores a keyed value
  (`retina=1&gray=0&lang=de&fav=lib_x`) instead of a positional string, so adding
  a setting is one key and an absent key falls back to that setting's documented
  default. The favorite library folded into the same cookie, retiring
  `inkshelf_fav_library` and leaving one preferences cookie; legacy positional
  values and the old favorite cookie are still read, so existing devices keep
  their settings.
```

Leave the two remaining Settings bullets (resolution override, EPUB2 fallback) in place — they were blocked on this and are now unblocked, but they are not done. Do **not** touch `CHANGELOG.md`.

- [ ] **Step 5: Final full run and commit**

Run: `dotnet test`
Expected: PASS, 241 tests.

```bash
git add docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "docs: record the single keyed preferences cookie"
```

---

## Done criteria

- `dotnet test` reports 241 passing.
- `grep -rn "Favorites" src/ tests/ --include=*.cs --include=*.cshtml` returns nothing outside `LegacyFavCookie`-related comments.
- A device with a legacy `inkshelf_settings=10de` cookie plus `inkshelf_fav_library=lib_x` keeps all four values on first read, and `inkshelf_fav_library` is gone after the next write.
- Un-favoriting sticks across a reload.
- Saving settings does not clear the favorite.
