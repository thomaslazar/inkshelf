# Resolution Override Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user hand-set width, height and pixel ratio on `/settings` so conversion works when the `scr` screen probe is missing, wrong, or simply not what they want.

**Architecture:** Four new fields ride in the existing `inkshelf_settings` cookie. `ScreenTarget.FromCookie` consults the override **before** the cookie, so it also covers the no-probe case. `Dpr` joins the EPUB cache key, because an explicit override makes two different targets otherwise collide on one filename.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit, ImageSharp (untouched here). Spec: `docs/superpowers/specs/2026-08-20-resolution-override-design.md`.

## Global Constraints

- .NET 10, **no AOT**. Plain server-rendered HTML.
- JS is a guideline, not a hard rule: **one** ES5 inline script is in scope for this feature (`getElementById`, `onclick`, no libraries). The page must still work with JS off.
- `dotnet test` from the repo root must pass. `dotnet format --verify-no-changes` must pass — CI enforces it.
- Conventional Commits: `type: subject`, imperative, lowercase, no period. **No** `Co-Authored-By` or "Generated with" lines.
- **Never** edit `CHANGELOG.md` — that belongs to the release skill only.
- `docs/ARCHITECTURE.md` is a map, not a diary: add invariants only, no per-feature entries.
- Commits per task are pre-authorised for this plan (the user asked for subagent-driven implementation). Do not push and do not open a PR.
- Existing bounds to reuse, do not redefine: `ScreenTarget.MaxDimension` = 4096, `ScreenTarget.MaxDpr` = 4.0.
- Branch is already `feat/resolution-override`. Stay on it.

---

### Task 1: Storage — the four fields in the settings cookie

**Files:**
- Modify: `src/Inkshelf/Convert/RenderTarget.cs` (append the new type)
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public readonly record struct ScreenOverride(int W, int H, double Dpr)` in namespace `Inkshelf.Convert`
  - `DeviceSettings.OverrideScreen` (`bool`, default `false`), `.OverrideW` (`int`, default `0`), `.OverrideH` (`int`, default `0`), `.OverrideDpr` (`double`, default `0`)
  - `DeviceSettings.ActiveOverride` → `ScreenOverride?` (null unless the flag is on **and** all three numbers are usable)
  - `public static int SanitizeDim(int px)`, `public static double SanitizeDpr(double dpr)`, `public static double ParseDpr(string s)`
  - Cookie keys: `ovr`, `ovrw`, `ovrh`, `ovrd`

- [ ] **Step 1: Write the failing tests**

Append inside the existing `DeviceSettingsTests` class in `tests/Inkshelf.Tests/DeviceSettingsTests.cs`:

```csharp
    [Fact]
    public void Screen_override_round_trips_through_the_cookie()
    {
        var s = new DeviceSettings(true, false, "de")
        {
            OverrideScreen = true, OverrideW = 1264, OverrideH = 1680, OverrideDpr = 1.875,
        };
        var wire = s.Serialize();
        Assert.Contains("ovr=1", wire);
        Assert.Contains("ovrw=1264", wire);
        Assert.Contains("ovrh=1680", wire);
        Assert.Contains("ovrd=1.875", wire);   // invariant, never "1,875"

        var read = DeviceSettings.Read(RequestWithCookie(wire));
        Assert.True(read.OverrideScreen);
        Assert.Equal(1264, read.OverrideW);
        Assert.Equal(1680, read.OverrideH);
        Assert.Equal(1.875, read.OverrideDpr);
    }

    [Fact]
    public void Screen_override_defaults_to_off_and_empty()
    {
        // A cookie written before this setting existed.
        var s = DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav="));
        Assert.False(s.OverrideScreen);
        Assert.Equal(0, s.OverrideW);
        Assert.Equal(0, s.OverrideH);
        Assert.Equal(0, s.OverrideDpr);
        Assert.Null(s.ActiveOverride);
    }

    [Fact]
    public void Screen_override_accepts_a_comma_decimal_ratio()
    {
        // The UI is translated; a German-locale user typing 1,875 must not
        // silently fall through to the invalid-value path.
        var s = DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=de&fav=&ovr=1&ovrw=800&ovrh=1000&ovrd=1,875"));
        Assert.Equal(1.875, s.OverrideDpr);
    }

    [Theory]
    [InlineData("0", "1000", "1")]        // zero width
    [InlineData("-5", "1000", "1")]       // negative width
    [InlineData("99999", "1000", "1")]    // past MaxDimension
    [InlineData("800", "1000", "0")]      // zero ratio
    [InlineData("800", "1000", "99")]     // past MaxDpr
    [InlineData("800", "1000", "abc")]    // unparseable ratio
    public void Screen_override_rejects_values_out_of_range(string w, string h, string dpr)
    {
        // A hand-edited cookie must not mint an absurd page size: the value is
        // dropped to 0, which makes the override inactive rather than dangerous.
        var s = DeviceSettings.Read(RequestWithCookie($"retina=1&gray=0&lang=&fav=&ovr=1&ovrw={w}&ovrh={h}&ovrd={dpr}"));
        Assert.Null(s.ActiveOverride);
    }

    [Fact]
    public void Active_override_needs_the_flag_and_all_three_numbers()
    {
        var numbers = new DeviceSettings(true, false, "") { OverrideW = 800, OverrideH = 1000, OverrideDpr = 2 };
        Assert.Null(numbers.ActiveOverride);                                  // flag off
        Assert.Null((numbers with { OverrideScreen = true, OverrideH = 0 }).ActiveOverride);

        var on = numbers with { OverrideScreen = true };
        Assert.Equal(new ScreenOverride(800, 1000, 2), on.ActiveOverride);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --nologo -v q --filter DeviceSettingsTests`
Expected: FAIL — compile errors, `DeviceSettings` has no `OverrideScreen` / `ActiveOverride`, and `ScreenOverride` does not exist.

- [ ] **Step 3: Add the `ScreenOverride` type**

Append to `src/Inkshelf/Convert/RenderTarget.cs`:

```csharp
// A hand-entered screen geometry, replacing the "scr" probe. W/H are physical
// image pixels (what a vendor spec sheet gives); Dpr is how many image pixels the
// reader draws per CSS layout pixel, so viewport = px × scale ÷ Dpr.
//
// Only ever constructed from already-sanitised values (DeviceSettings clamps them
// on the way out of the cookie), but ScreenTarget clamps again — the numbers cross
// a trust boundary and clamping twice is cheaper than trusting once.
public readonly record struct ScreenOverride(int W, int H, double Dpr);
```

- [ ] **Step 4: Add the fields, sanitisers and serialisation**

In `src/Inkshelf/Auth/DeviceSettings.cs`, add `using System.Globalization;` to the usings, then insert after the `Scales` array:

```csharp
    // A hand-entered screen geometry, used INSTEAD of the "scr" probe when
    // OverrideScreen is set. The numbers are kept even while the override is off,
    // so switching it off does not throw them away and the fields can show what
    // was last used. 0 means "nothing stored", which is what renders the field
    // blank rather than a misleading 0.
    public bool OverrideScreen { get; init; }
    public int OverrideW { get; init; }
    public int OverrideH { get; init; }
    public double OverrideDpr { get; init; }

    // The override as the converter wants it, or null when it is off or
    // incomplete. Incomplete counts as off: a half-filled override would produce
    // a zero-sized page, which is worse than falling back to the probe.
    public ScreenOverride? ActiveOverride =>
        OverrideScreen && OverrideW > 0 && OverrideH > 0 && OverrideDpr > 0
            ? new ScreenOverride(OverrideW, OverrideH, OverrideDpr)
            : null;
```

Add the sanitisers next to `SanitizeScale`:

```csharp
    // Out of range becomes 0 ("nothing stored") rather than being clamped to the
    // bound: a typo'd 99999 is not a request for 4096, it is a mistake, and
    // silently converting at a size the user never asked for is worse than
    // falling back to the probe.
    public static int SanitizeDim(int px) => px > 0 && px <= Convert.ScreenTarget.MaxDimension ? px : 0;

    public static double SanitizeDpr(double dpr) => dpr > 0 && dpr <= Convert.ScreenTarget.MaxDpr ? dpr : 0;

    // Accepts both "1.875" and "1,875": the UI is translated and a comma is what a
    // German-locale user will type. 0 on anything unparseable.
    public static double ParseDpr(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0;
```

Extend `Serialize()`'s final line:

```csharp
        + $"&spread={Spread.ToString().ToLowerInvariant()}&scale={Scale}"
        + $"&ovr={(OverrideScreen ? 1 : 0)}&ovrw={OverrideW}&ovrh={OverrideH}"
        + $"&ovrd={OverrideDpr.ToString(CultureInfo.InvariantCulture)}";
```

And in `Read`'s object initialiser, after the `Scale = …` line:

```csharp
            OverrideScreen = Flag(q, "ovr", Default.OverrideScreen),
            OverrideW = q.TryGetValue("ovrw", out var ow) && int.TryParse(ow.ToString(), out var owv)
                ? SanitizeDim(owv) : 0,
            OverrideH = q.TryGetValue("ovrh", out var oh) && int.TryParse(oh.ToString(), out var ohv)
                ? SanitizeDim(ohv) : 0,
            OverrideDpr = q.TryGetValue("ovrd", out var od) ? SanitizeDpr(ParseDpr(od.ToString())) : 0,
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --nologo -v q --filter DeviceSettingsTests`
Expected: PASS. Then `dotnet test --nologo -v q` — the whole suite must still pass. `Serialize_emits_keyed_pairs` and `Fav_is_sanitized_on_the_way_into_the_cookie` assert the **exact** cookie string, so append `&ovr=0&ovrw=0&ovrh=0&ovrd=0` to their expected values.

- [ ] **Step 6: Verify formatting and commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf tests/Inkshelf.Tests
git commit -m "feat: store a hand-entered screen override in the settings cookie"
```

---

### Task 2: `ScreenTarget` consults the override first

**Files:**
- Modify: `src/Inkshelf/Convert/ScreenTarget.cs`
- Test: `tests/Inkshelf.Tests/ScreenTargetTests.cs`

**Interfaces:**
- Consumes: `ScreenOverride` and `DeviceSettings.ActiveOverride` from Task 1.
- Produces: `ScreenTarget.FromCookie(string? scr, bool retina = false, bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100, ScreenOverride? over = null)` — the new parameter is last and optional, so existing call sites keep compiling.

- [ ] **Step 1: Write the failing tests**

Append inside the existing `ScreenTargetTests` class:

```csharp
    [Fact]
    public void An_override_beats_a_perfectly_good_probe()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", retina: true, over: new ScreenOverride(1000, 2000, 2));
        Assert.Equal(1000, t.MaxW);
        Assert.Equal(2000, t.MaxH);
        Assert.Equal(2, t.Dpr);
    }

    [Fact]
    public void An_override_works_with_no_probe_at_all()
    {
        // The whole point: FromCookie used to return (0,0,1) the moment the cookie
        // was missing and never look further, so there was no cap — no downscaling,
        // and SpreadMode.Fit had no box to letterbox a spread onto.
        var t = ScreenTarget.FromCookie(null, over: new ScreenOverride(1000, 2000, 1));
        Assert.Equal(1000, t.MaxW);
        Assert.Equal(2000, t.MaxH);
    }

    [Fact]
    public void An_override_ignores_the_retina_toggle()
    {
        // Retina's only job is choosing CSS vs CSS × dpr. Both numbers are stated
        // explicitly here, so there is nothing left for it to decide.
        var on = ScreenTarget.FromCookie("769x953x1.875", retina: true, over: new ScreenOverride(1000, 2000, 2));
        var off = ScreenTarget.FromCookie("769x953x1.875", retina: false, over: new ScreenOverride(1000, 2000, 2));
        Assert.Equal(on, off);
    }

    [Fact]
    public void An_override_is_clamped_to_the_same_bounds_as_the_probe()
    {
        var t = ScreenTarget.FromCookie(null, over: new ScreenOverride(99999, 99999, 99));
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxW);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxH);
        Assert.Equal(ScreenTarget.MaxDpr, t.Dpr);
    }

    [Fact]
    public void An_incomplete_override_falls_back_to_the_probe()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", over: new ScreenOverride(0, 2000, 1));
        Assert.Equal(769, t.MaxW);
        Assert.Equal(953, t.MaxH);
    }

    [Fact]
    public void An_override_carries_the_other_knobs_through()
    {
        var t = ScreenTarget.FromCookie(null, grayscale: true, spread: SpreadMode.RotateLeft, scale: 90,
            over: new ScreenOverride(800, 1000, 1));
        Assert.True(t.Grayscale);
        Assert.Equal(SpreadMode.RotateLeft, t.Spread);
        Assert.Equal(90, t.Scale);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --nologo -v q --filter ScreenTargetTests`
Expected: FAIL — `FromCookie` has no `over` parameter.

- [ ] **Step 3: Implement it**

In `src/Inkshelf/Convert/ScreenTarget.cs`, change the signature and add the override branch as the **first** thing in the method body:

```csharp
    public static RenderTarget FromCookie(string? scr, bool retina = false, bool grayscale = false,
        SpreadMode spread = SpreadMode.Fit, int scale = 100, ScreenOverride? over = null)
    {
        // FIRST, before the cookie is even looked at. Being merely "preferred over a
        // bad value" would not help: the no-probe case returns at the bottom of this
        // method, so an override consulted later would never be reached when the
        // cookie is absent — which is one of the reasons the override exists.
        //
        // retina is deliberately not consulted: it only chooses between the CSS size
        // and CSS × dpr, and both numbers are explicit here.
        if (over is { W: > 0, H: > 0, Dpr: > 0 } o)
            return new RenderTarget(
                Math.Min(o.W, MaxDimension), Math.Min(o.H, MaxDimension),
                Math.Min(o.Dpr, MaxDpr), grayscale) { Spread = spread, Scale = scale };
```

Leave the rest of the method as it is.

Also extend the method's doc comment above the signature with one line:

```csharp
    // An override (hand-entered geometry) wins over the cookie entirely, including
    // when the cookie is missing.
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --nologo -v q --filter ScreenTargetTests` → PASS
Then: `dotnet test --nologo -v q` → all pass.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf tests/Inkshelf.Tests
git commit -m "feat: let a screen override take precedence over the scr probe"
```

---

### Task 3: Thread the override to every conversion target

**Files:**
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs:14`
- Modify: `src/Inkshelf/Pages/Item.cshtml.cs:55`
- Modify: `src/Inkshelf/Pages/ConvertWhy.cshtml.cs:36`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs:142`
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs:66`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: `FromCookie(..., ScreenOverride? over)` from Task 2, `DeviceSettings.ActiveOverride` from Task 1.
- Produces: nothing new. Every conversion target in the app now honours the override.

- [ ] **Step 1: Write the failing test**

`ListingRenderTests.LibraryRequest` always sends a `scr` cookie, so it needs a way not to. Change its signature and cookie line:

```csharp
    private static HttpRequestMessage LibraryRequest(WebApplicationFactory<Program> factory,
        string? settings = null, bool includeScr = true)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var sessionCookie = protector.Protect("access\nrefresh");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/library/{LibId}");
        var cookie = $"inkshelf_session={Uri.EscapeDataString(sessionCookie)}";
        if (includeScr) cookie += $"; scr={W}x{H}x1";
        if (settings is not null) cookie += $"; inkshelf_settings={settings}";
        req.Headers.Add("Cookie", cookie);
        return req;
    }
```

Then append this test to the class:

```csharp
    // The override has to reach the REAL conversion target, not just ScreenTarget's
    // unit tests: with no scr cookie at all, a cache file at the overridden size must
    // count as this request's cache path and the row must render as converted.
    [Fact]
    public async Task An_override_supplies_the_target_when_there_is_no_probe()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cache = factory.Services.GetRequiredService<EpubCache>();
        File.WriteAllText(cache.PathFor(ItemId, Size, Mtime, 1000, 2000,
            spread: DeviceSettings.Default.Spread), "epub");

        // No scr cookie; the override supplies 1000x2000 at dpr 1.
        const string overridden = "retina=0&gray=0&lang=&fav=&spread=splitleftfirst&scale=100"
            + "&ovr=1&ovrw=1000&ovrh=2000&ovrd=1";
        var response = await client.SendAsync(LibraryRequest(factory, overridden, includeScr: false));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("data-warm", PrimaryConvertAnchor(html));

        // Same request with the override switched off: no probe, so no cap, so the
        // 1000x2000 file is not this request's cache path and the row still offers
        // a plain Convert.
        const string plain = "retina=0&gray=0&lang=&fav=&spread=splitleftfirst&scale=100"
            + "&ovr=0&ovrw=1000&ovrh=2000&ovrd=1";
        var off = await client.SendAsync(LibraryRequest(factory, plain, includeScr: false));
        Assert.Contains("data-warm data-why=", PrimaryConvertAnchor(await off.Content.ReadAsStringAsync()));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --nologo -v q --filter An_override_supplies_the_target_when_there_is_no_probe`
Expected: FAIL — the override is not wired into `Library.cshtml.cs`, so the row renders as not-converted in both halves.

- [ ] **Step 3: Wire all five call sites**

Add `, ds.ActiveOverride` (or `s.` / `settings.`, matching each site's local variable name) as the last argument:

- `src/Inkshelf/Endpoints/ConvertEndpoints.cs:14` — `ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"], ds.Retina, ds.Grayscale, ds.Spread, ds.Scale, ds.ActiveOverride)`
- `src/Inkshelf/Pages/Item.cshtml.cs:55` — `…, ds.Spread, ds.Scale, ds.ActiveOverride)`
- `src/Inkshelf/Pages/ConvertWhy.cshtml.cs:36` — `…, ds.Spread, ds.Scale, ds.ActiveOverride)`
- `src/Inkshelf/Pages/Library.cshtml.cs:142` — `…, s.Spread, s.Scale, s.ActiveOverride)`
- `src/Inkshelf/Pages/Converted.cshtml.cs:66` — `…, settings.Spread, settings.Scale, settings.ActiveOverride)`

Verify none were missed:

```bash
grep -rn "FromCookie(" --include=*.cs src | grep -v ScreenTarget.cs | grep -c ActiveOverride
```

Expected output: `5`

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --nologo -v q --filter ListingRenderTests` → PASS
Then: `dotnet test --nologo -v q` → all pass.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf tests/Inkshelf.Tests
git commit -m "feat: honour the screen override wherever a conversion target is built"
```

---

### Task 4: `Dpr` joins the EPUB cache key

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs`
- Modify: `src/Inkshelf/Convert/ConvertService.cs:106`
- Modify: `src/Inkshelf/Pages/Support/ConvertRowStateResolver.cs:27`
- Modify: `src/Inkshelf/Pages/Converted.cshtml.cs` (the variant-matching `continue`)
- Test: `tests/Inkshelf.Tests/EpubCacheTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100, double dpr = 1)`; `TryGet(…, out string path, SpreadMode spread = SpreadMode.Fit, int scale = 100, double dpr = 1)`; `CachedVariant` gains `double Dpr = 1` as its last positional-with-default member.

- [ ] **Step 1: Write the failing tests**

Append to `EpubCacheTests`:

```csharp
    [Fact]
    public void PathFor_encodes_dpr_only_when_it_is_not_one()
    {
        // Dpr got away with being absent from the key while it was always implied by
        // WxH: under retina the cap IS css × dpr, and without retina it is always 1.
        // An explicit override breaks that — 1000x2000 at dpr 1 and at dpr 2 are
        // different EPUBs — so the second device would be served the first one's file.
        var c = new EpubCache(TempDirPath());
        Assert.EndsWith("i1-1-2-800x1000-f.epub", c.PathFor("i1", 1, 2, 800, 1000));
        Assert.EndsWith("i1-1-2-800x1000-f-d1.875.epub", c.PathFor("i1", 1, 2, 800, 1000, dpr: 1.875));
        Assert.EndsWith("i1-1-2-800x1000-f-s90-d2.epub",
            c.PathFor("i1", 1, 2, 800, 1000, scale: 90, dpr: 2));
    }

    [Fact]
    public void ListVariants_round_trips_dpr()
    {
        var dir = TempDirPath();
        var c = new EpubCache(dir);
        File.WriteAllText(c.PathFor("i1", 1, 2, 800, 1000, spread: SpreadMode.RotateLeft, scale: 90, dpr: 1.875), "e");
        File.WriteAllText(c.PathFor("i2", 3, 4, 800, 1000), "e");

        var v = c.ListVariants().OrderBy(x => x.ItemId).ToList();
        Assert.Equal(2, v.Count);
        Assert.Equal((1.875, 90, SpreadMode.RotateLeft), (v[0].Dpr, v[0].Scale, v[0].Spread));
        Assert.Equal((1.0, 100, SpreadMode.Fit), (v[1].Dpr, v[1].Scale, v[1].Spread));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --nologo -v q --filter EpubCacheTests`
Expected: FAIL — `PathFor` has no `dpr` parameter.

- [ ] **Step 3: Implement it**

In `EpubCache.cs`, add `using System.Globalization;`, then change `PathFor` and `TryGet`:

```csharp
    public string PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH,
        bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100, double dpr = 1) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}{(grayscale ? "-g" : "")}"
            + $"-{Letter(spread)}{(scale == 100 ? "" : $"-s{scale}")}"
            + (dpr == 1 ? "" : $"-d{dpr.ToString(CultureInfo.InvariantCulture)}") + ".epub");

    public bool TryGet(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale,
        out string path, SpreadMode spread = SpreadMode.Fit, int scale = 100, double dpr = 1)
    {
        path = PathFor(itemId, size, mtimeMs, maxW, maxH, grayscale, spread, scale, dpr);
        return File.Exists(path);
    }
```

Extend the `PathFor` doc comment with: `// Dpr is emitted only when it is not 1 — see the dpr test for why it has to be in the key at all.`

Add `Dpr` to the record:

```csharp
    public sealed record CachedVariant(
        string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path,
        DateTime ConvertedAtUtc, SpreadMode Spread = SpreadMode.Fit, int Scale = 100, double Dpr = 1);
```

In `TryParse`, parse `-d` **before** `-s` (reverse of the order `PathFor` writes them). Insert immediately after the `var name = …GetFileNameWithoutExtension(path);` line and before the existing scale block:

```csharp
        var dpr = 1.0;
        var di = name.LastIndexOf("-d", StringComparison.Ordinal);
        if (di > 0 && double.TryParse(name[(di + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDpr))
        { dpr = parsedDpr; name = name[..di]; }
```

and pass it through at the end:

```csharp
        return new CachedVariant(itemId, size, mtimeMs, maxW, maxH, grayscale, path,
            file.LastWriteTimeUtc, spread, scale, dpr);
```

Update the three call sites to pass the target's dpr:

- `ConvertService.cs:106` → `_cache.PathFor(id, size, mtime, target.MaxW, target.MaxH, target.Grayscale, target.Spread, target.Scale, target.Dpr)`
- `ConvertRowStateResolver.cs:27` → `cache.PathFor(itemId, size, mtimeMs, target.MaxW, target.MaxH, target.Grayscale, target.Spread, target.Scale, target.Dpr)`
- `Converted.cshtml.cs` → add `|| v.Dpr != target.Dpr` to the existing variant-mismatch `continue` condition.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --nologo -v q --filter EpubCacheTests` → PASS
Then: `dotnet test --nologo -v q` → all pass. If a render test now misses its cache file, it is because the request's target has a dpr ≠ 1; pass the same `dpr:` to that test's `PathFor` call.

- [ ] **Step 5: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf tests/Inkshelf.Tests
git commit -m "fix: put the pixel ratio in the epub cache key"
```

---

### Task 5: The settings UI, and the disabled-field rules

**Files:**
- Modify: `src/Inkshelf/Pages/Settings.cshtml`
- Modify: `src/Inkshelf/Pages/Settings.cshtml.cs`
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Inkshelf/wwwroot/app.css`
- Modify: `src/Inkshelf/locales/de.json`
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: the `DeviceSettings` fields and sanitisers from Task 1.
- Produces: form fields `ovr` (checkbox), `ovrw`, `ovrh`, `ovrd`; `SettingsModel.PrefillW`, `.PrefillH`, `.PrefillDpr` (`int`, `int`, `string`).

- [ ] **Step 1: Write the failing tests**

Append to `EndpointTests`:

```csharp
    [Fact]
    public async Task Saving_with_the_override_on_keeps_retina()
    {
        // A disabled checkbox is NOT submitted, and the UI disables retina while the
        // override is on. With the usual "absent means off" rule that would silently
        // switch retina off every time the override was saved.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        // Retina on, override off.
        await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["retina"] = "on",
            ["lang"] = "en",
        }));

        // Now turn the override on. The disabled retina box sends nothing.
        var saved = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["ovr"] = "on",
            ["ovrw"] = "1000",
            ["ovrh"] = "2000",
            ["ovrd"] = "1.5",
        }));

        var setCookie = string.Join(" ", saved.Headers.GetValues("Set-Cookie"));
        Assert.Contains("retina%3D1", setCookie);   // survived
        Assert.Contains("ovr%3D1", setCookie);
        Assert.Contains("ovrw%3D1000", setCookie);
    }

    [Fact]
    public async Task Saving_with_the_override_off_keeps_the_numbers()
    {
        // The three fields are disabled while the override is off, so they submit
        // nothing — and must not be zeroed, or switching the override off would
        // throw away numbers the user had to look up.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client);

        await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
            ["ovr"] = "on",
            ["ovrw"] = "1000",
            ["ovrh"] = "2000",
            ["ovrd"] = "1.5",
        }));

        var off = await client.PostAsync("/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["lang"] = "en",
        }));

        var setCookie = string.Join(" ", off.Headers.GetValues("Set-Cookie"));
        Assert.Contains("ovr%3D0", setCookie);      // switched off
        Assert.Contains("ovrw%3D1000", setCookie);  // but remembered
        Assert.Contains("ovrd%3D1.5", setCookie);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --nologo -v q --filter Saving_with_the_override`
Expected: FAIL — the endpoint does not read `ovr` at all, so no `ovr%3D1` appears.

- [ ] **Step 3: Implement the endpoint rules**

Replace the body of the POST handler in `src/Inkshelf/Endpoints/SettingsEndpoints.cs` between `var form = …` and `DeviceSettings.Set(…)`:

```csharp
            var form = await ctx.Request.ReadFormAsync();
            // Unchecked checkboxes send no field → absent == off. lang comes from
            // the <select>; DeviceSettings sanitises it on both write (Serialize)
            // and read.
            // `with`, NOT a fresh instance — the favorite lives in this same cookie
            // and constructing a new record would wipe it.
            var stored = DeviceSettings.Read(ctx.Request);
            var overriding = form.ContainsKey("ovr");
            var settings = stored with
            {
                // A DISABLED input is not submitted, and the page disables retina
                // while the override is on. So "absent" only means "off" here when
                // the override is off — otherwise saving the override would quietly
                // switch retina off.
                Retina = overriding ? stored.Retina : form.ContainsKey("retina"),
                Grayscale = form.ContainsKey("grayscale"),
                Lang = form["lang"].ToString(),
                Spread = Enum.TryParse<SpreadMode>(form["spread"].ToString(), true, out var sp)
                    ? sp : DeviceSettings.Default.Spread,
                Scale = int.TryParse(form["scale"].ToString(), out var pc)
                    ? DeviceSettings.SanitizeScale(pc) : DeviceSettings.Default.Scale,
                OverrideScreen = overriding,
                // Same trap, other direction: the three numbers are disabled while
                // the override is off, so keep what is stored rather than zeroing
                // values the user had to look up.
                OverrideW = form.ContainsKey("ovrw")
                    ? DeviceSettings.SanitizeDim(int.TryParse(form["ovrw"].ToString(), out var ow) ? ow : 0)
                    : stored.OverrideW,
                OverrideH = form.ContainsKey("ovrh")
                    ? DeviceSettings.SanitizeDim(int.TryParse(form["ovrh"].ToString(), out var oh) ? oh : 0)
                    : stored.OverrideH,
                OverrideDpr = form.ContainsKey("ovrd")
                    ? DeviceSettings.SanitizeDpr(DeviceSettings.ParseDpr(form["ovrd"].ToString()))
                    : stored.OverrideDpr,
            };
```

- [ ] **Step 4: Run the endpoint tests to verify they pass**

Run: `dotnet test --nologo -v q --filter Saving_with_the_override` → PASS

- [ ] **Step 5: Add the prefill values to the page model**

In `src/Inkshelf/Pages/Settings.cshtml.cs`, add to the class:

```csharp
    // What the override fields show: the stored override when there is one, else
    // whatever the probe reported, else blank. 0 / "" render as an empty field.
    public int PrefillW { get; private set; }
    public int PrefillH { get; private set; }
    public string PrefillDpr { get; private set; } = "";
```

and at the end of `OnGet()`:

```csharp
        var probe = ParseScreen(Request.Cookies["scr"]);
        PrefillW = Settings.OverrideW > 0 ? Settings.OverrideW : probe?.W ?? 0;
        PrefillH = Settings.OverrideH > 0 ? Settings.OverrideH : probe?.H ?? 0;
        var dpr = Settings.OverrideDpr > 0 ? Settings.OverrideDpr : probe?.Dpr ?? 0;
        PrefillDpr = dpr > 0 ? dpr.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
```

plus this helper next to `FormatScreen`:

```csharp
    // "769x953x1.875" → (769, 953, 1.875). null when absent/unparseable. The probe
    // reports CSS pixels × dpr; multiplying gives the physical size, which is what
    // the override fields want.
    private static (int W, int H, double Dpr)? ParseScreen(string? scr)
    {
        if (string.IsNullOrEmpty(scr)) return null;
        var p = scr.Split('x');
        if (p.Length < 2
            || !int.TryParse(p[0], out var w) || !int.TryParse(p[1], out var h)
            || w <= 0 || h <= 0) return null;
        var dpr = 1.0;
        if (p.Length >= 3 && double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0) dpr = d;
        return ((int)Math.Round(w * dpr), (int)Math.Round(h * dpr), dpr);
    }
```

- [ ] **Step 6: Add the UI block**

In `src/Inkshelf/Pages/Settings.cshtml`, insert a new `<p>` immediately **after** the Page scale block and before the Language block:

```html
    <p>
        <label>
            <input type="checkbox" name="ovr" id="ovr" value="on" @(Model.Settings.OverrideScreen ? "checked" : "") />
            @L["Override detected screen"]
        </label>
        <label>@L["Width"]
            <input type="number" name="ovrw" id="ovrw" min="1" max="4096" value="@(Model.PrefillW > 0 ? Model.PrefillW.ToString() : "")" />
        </label>
        <label>@L["Height"]
            <input type="number" name="ovrh" id="ovrh" min="1" max="4096" value="@(Model.PrefillH > 0 ? Model.PrefillH.ToString() : "")" />
        </label>
        <label>@L["Pixel ratio"]
            <input type="text" name="ovrd" id="ovrd" value="@Model.PrefillDpr" />
        </label>
    </p>
```

Give the retina checkbox an id so the script can reach it — change its input to:

```html
            <input type="checkbox" name="retina" id="retina" value="on" @(Model.Settings.Retina ? "checked" : "") />
```

Add the script as the last line of the file, after `</form>`:

```html
<script>
/* Light on purpose: ES5, no libraries, nothing an old e-reader browser cannot do.
   With JS off the fields stay enabled and the server still ignores them unless the
   checkbox is set, so the page degrades cleanly. */
(function () {
    var cb = document.getElementById("ovr");
    if (!cb) { return; }
    function sync() {
        var off = !cb.checked;
        document.getElementById("ovrw").disabled = off;
        document.getElementById("ovrh").disabled = off;
        document.getElementById("ovrd").disabled = off;
        /* Retina only chooses CSS vs CSS x dpr; an explicit override states both. */
        var r = document.getElementById("retina");
        if (r) { r.disabled = cb.checked; }
    }
    cb.onclick = sync;
    sync();
})();
</script>
```

- [ ] **Step 7: Style the number fields**

In `src/Inkshelf/wwwroot/app.css`, after the existing `.settings-form select` rule:

```css
/* Narrow, so three of them read as one row of numbers rather than three settings.
   A disabled field must LOOK disabled — the whole point of disabling it is to stop
   it implying it affects anything. */
.settings-form input[type=number], .settings-form input[type=text] { font: inherit; padding: .4rem; width: 6rem; }
.settings-form input:disabled { color: #888; background: #eee; }
```

- [ ] **Step 8: Add the German strings**

In `src/Inkshelf/locales/de.json`, after the `"Page scale"` entry:

```json
  "Override detected screen": "Erkannten Bildschirm überschreiben",
  "Width": "Breite",
  "Height": "Höhe",
  "Pixel ratio": "Pixelverhältnis",
```

Verify the file still parses:

```bash
python3 -c "import json; json.load(open('src/Inkshelf/locales/de.json')); print('ok')"
```

- [ ] **Step 9: Run everything**

Run: `dotnet test --nologo -v q`
Expected: all pass.

- [ ] **Step 10: Commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf tests/Inkshelf.Tests
git commit -m "feat: add the screen override controls to the settings page"
```

---

### Task 6: Browser pass and docs

**Files:**
- Modify: `tools/uicheck/Program.cs`
- Modify: `docs/ROADMAP.md`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/superpowers/specs/2026-08-20-resolution-override-design.md` (status line)

**Interfaces:**
- Consumes: the UI strings from Task 5.
- Produces: nothing.

- [ ] **Step 1: Assert the new controls in both languages**

In `tools/uicheck/Program.cs`, add `"Erkannten Bildschirm überschreiben", "Pixelverhältnis"` to the `settings-de` `mustContain` list, and `"Override detected screen", "Pixel ratio"` to `settings-en`'s.

- [ ] **Step 2: Run the browser pass**

The dev server may be holding port 5099; uicheck defaults to the same port and would silently test the wrong server.

Run: `PORT=5098 bash tools/uicheck/run.sh`
Expected: `PASS` twice (desktop and phone viewport).

Then **read** `tools/uicheck/shots/settings-en.png` and confirm: the three fields sit inside the override block with no separator line between them, and the whole group is separated from Page scale and Language.

- [ ] **Step 3: Move the roadmap item to Done**

Delete the `**Resolution override.**` bullet from `## Settings` in `docs/ROADMAP.md` and add to `## Done`:

```markdown
- **Resolution override** — width, height and pixel ratio can be set by hand when
  the `scr` probe is missing, wrong, or simply not what the user wants. It takes
  precedence over the probe entirely, including when the probe is absent, which is
  also what finally gives `SpreadMode.Fit` a page box on a device with no
  JavaScript. The pixel ratio joined the EPUB cache key with it: an explicit
  override makes two targets that differ only in ratio collide on one filename.
```

- [ ] **Step 4: Record the invariant, not the feature**

In `docs/ARCHITECTURE.md`, in the **Per-device state** section, extend the existing cache-key bullet so it names dpr, and add one bullet:

```markdown
- **A hand-set screen override wins over the probe, and is consulted first.**
  `ScreenTarget.FromCookie` returns early when the `scr` cookie is missing, so an
  override checked later would never be reached in exactly the case it exists for.
  `retina` is not consulted while an override is active — it only chooses between
  the CSS size and CSS × dpr, and both are explicit.
- **A disabled input is not submitted.** The settings form disables fields it does
  not want used, so the POST handler treats an absent field as "keep what is
  stored" for those — otherwise saving would silently zero the override numbers, or
  turn retina off, since absent normally means off for a checkbox.
```

- [ ] **Step 5: Mark the spec implemented**

In the spec, change `**Status:** design approved, not yet implemented` to `**Status:** design approved, implemented`.

- [ ] **Step 6: Commit**

```bash
dotnet format --verify-no-changes
dotnet test --nologo -v q
git add tools docs
git commit -m "docs: record the resolution override"
```

---

## Done criteria

- `dotnet test` passes; `dotnet format --verify-no-changes` clean.
- `PORT=5098 bash tools/uicheck/run.sh` passes and the settings screenshot looks right.
- An override with **no** `scr` cookie produces a real cap, so `Fit` has a box.
- Retina cannot be changed while the override is on, and is not lost by saving.
- Switching the override off keeps the numbers.
- A real device pass stays with the user — the headless run cannot reproduce the e-ink engine.
