# Settings System + Retina & Grayscale Toggles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-device settings cookie + Settings page, and use it to expose a retina toggle (replacing the hard-coded `ScreenTarget.Retina` const) and a grayscale toggle for converted comic pages.

**Architecture:** A server-written `inkshelf_settings` cookie (`DeviceSettings`, modeled on `Favorites`) holds the two flags. `ScreenTarget.FromCookie` combines the JS-written `scr` device probe with those flags into a new `RenderTarget` record that flows through the conversion pipeline and cache key. A plain-`<form>` Settings Razor Page writes the cookie via a `POST /settings` minimal-API endpoint. The retina/grayscale variants are distinguished in the cache: retina already changes `maxW/maxH`; grayscale adds a `-g` filename marker.

**Tech Stack:** ASP.NET Core Razor Pages + minimal APIs, .NET 10 (no AOT), SixLabors.ImageSharp, xUnit, `WebApplicationFactory<Program>` for endpoint tests.

## Global Constraints

- **No AOT.** .NET 10, ASP.NET Core Razor Pages for HTML, minimal APIs for streams/actions.
- **Near-zero client JS.** No new JavaScript. Plain `<form>` and `<a>` only.
- **Defensive CSS only** — no `object-fit`, no flex `gap` (old e-reader engines).
- **Cookie `Secure` rule:** every cookie writer uses `Secure = ForceSecureCookies || Request.IsHttps` (never bare `Request.IsHttps`). Read `ForceSecureCookies` from `AbsOptions` via `RequestServices`, exactly as `Favorites.Set` does.
- **Conventional Commits**, imperative lowercase subject. **No** `Co-Authored-By` / "Generated with" lines.
- `dotnet test` from the repo root (inside the devcontainer) must stay green after every task.
- Build/test command: `dotnet test` (run from `/workspaces/inkshelf`). Build only: `dotnet build src/Inkshelf/Inkshelf.csproj`.

---

### Task 1: `DeviceSettings` cookie type

The per-device settings cookie. Pure value + static read/write helpers, modeled on `Auth/Favorites.cs`. No DI registration needed (static methods, like `Favorites`).

**Files:**
- Create: `src/Inkshelf/Auth/DeviceSettings.cs`
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs`

**Interfaces:**
- Consumes: `AbsOptions` (via `HttpContext.RequestServices`, for `ForceSecureCookies`).
- Produces:
  - `record DeviceSettings(bool Retina, bool Grayscale)`
  - `DeviceSettings.Cookie` → `"inkshelf_settings"`
  - `DeviceSettings.Default` → `new(false, false)`
  - `DeviceSettings.Read(HttpRequest req)` → `DeviceSettings`
  - `DeviceSettings.Set(HttpResponse res, DeviceSettings settings)` → `void`
  - `settings.Serialize()` → `string` (e.g. `"r=1&g=0"`)

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/DeviceSettingsTests.cs`:

```csharp
using Inkshelf;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Tests;

public class DeviceSettingsTests
{
    private static HttpRequest RequestWithCookie(string? value)
    {
        var ctx = new DefaultHttpContext();
        if (value is not null) ctx.Request.Headers.Cookie = $"{DeviceSettings.Cookie}={value}";
        return ctx.Request;
    }

    [Fact]
    public void Read_absent_cookie_returns_default()
    {
        Assert.Equal(DeviceSettings.Default, DeviceSettings.Read(RequestWithCookie(null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("r=&g=")]
    public void Read_malformed_cookie_returns_default(string value)
    {
        Assert.Equal(DeviceSettings.Default, DeviceSettings.Read(RequestWithCookie(value)));
    }

    [Fact]
    public void Read_parses_both_flags()
    {
        Assert.Equal(new DeviceSettings(true, false), DeviceSettings.Read(RequestWithCookie("r=1&g=0")));
        Assert.Equal(new DeviceSettings(false, true), DeviceSettings.Read(RequestWithCookie("r=0&g=1")));
        Assert.Equal(new DeviceSettings(true, true), DeviceSettings.Read(RequestWithCookie("r=1&g=1")));
    }

    [Fact]
    public void Serialize_round_trips_through_read()
    {
        var s = new DeviceSettings(true, false);
        Assert.Equal(s, DeviceSettings.Read(RequestWithCookie(s.Serialize())));
    }

    [Fact]
    public void Set_writes_essential_root_path_cookie_with_value()
    {
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(true, true));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{DeviceSettings.Cookie}=r=1&g=1", setCookie);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        var services = new ServiceCollectionStub(new AbsOptions { ForceSecureCookies = true });
        ctx.RequestServices = services;
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, false));
        Assert.Contains("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false, no AbsOptions → ForceSecureCookies false
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, false));
        Assert.DoesNotContain("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Minimal IServiceProvider that returns one AbsOptions instance (mirrors how
    // RequestServices.GetService<AbsOptions>() resolves in production).
    private sealed class ServiceCollectionStub(AbsOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(AbsOptions) ? options : null;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter DeviceSettingsTests`
Expected: FAIL — `DeviceSettings` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Inkshelf/Auth/DeviceSettings.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Auth;

// Per-device rendering preferences, stored in a server-written cookie. Modeled
// on Favorites: static Read/Set, same cookie-flag rules. Distinct from the
// JS-written "scr" device probe — this is user CHOICE, scr is device TRUTH; the
// two are read together where conversion happens.
public sealed record DeviceSettings(bool Retina, bool Grayscale)
{
    public const string Cookie = "inkshelf_settings";
    public static readonly DeviceSettings Default = new(false, false);

    // Compact "r=<0|1>&g=<0|1>". '&' and '=' are valid in a cookie value.
    public string Serialize() => $"r={(Retina ? 1 : 0)}&g={(Grayscale ? 1 : 0)}";

    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v)) return Default;
        bool retina = false, grayscale = false;
        foreach (var part in v.Split('&'))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;
            var on = kv[1] == "1";
            if (kv[0] == "r") retina = on;
            else if (kv[0] == "g") grayscale = on;
        }
        return new DeviceSettings(retina, grayscale);
    }

    public static void Set(HttpResponse res, DeviceSettings settings)
    {
        var forceSecure = res.HttpContext.RequestServices.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
        res.Cookies.Append(Cookie, settings.Serialize(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = forceSecure || res.HttpContext.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter DeviceSettingsTests`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs
git commit -m "feat: add DeviceSettings per-device settings cookie"
```

---

### Task 2: `RenderTarget` record + `ScreenTarget` rework + dpr-clamp fix

Replace the `const bool Retina` with a `retina` parameter, add a `grayscale` passthrough, return a `RenderTarget` record, and fix the security bug: clamp dimensions **after** multiplying by `dpr`, and bound `dpr`. Update the two call sites to consume the record (still passing `retina:false, grayscale:false` — behavior stays identical this task; the cookie is wired in Task 6).

**Files:**
- Create: `src/Inkshelf/Convert/RenderTarget.cs`
- Modify: `src/Inkshelf/Convert/ScreenTarget.cs`
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs:12` (the `FromCookie` call + destructure)
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs:103` (`ComputeConvertStates`)
- Test: `tests/Inkshelf.Tests/ScreenTargetTests.cs` (rewrite)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `readonly record struct RenderTarget(int MaxW, int MaxH, double Dpr, bool Grayscale)`
  - `ScreenTarget.FromCookie(string? scr, bool retina = false, bool grayscale = false)` → `RenderTarget`
  - `ScreenTarget.MaxDimension` (unchanged, `4096`), `ScreenTarget.MaxDpr` (new, `4.0`)
  - The `const bool Retina` is **removed**.

- [ ] **Step 1: Create the `RenderTarget` record**

Create `src/Inkshelf/Convert/RenderTarget.cs`:

```csharp
namespace Inkshelf.Convert;

// The resolved per-device render knobs for one conversion: the page-image pixel
// cap (MaxW/MaxH, 0 = no cap), the pixel ratio used to derive each page's CSS
// viewport (viewport = image px / Dpr), and whether pages are desaturated.
public readonly record struct RenderTarget(int MaxW, int MaxH, double Dpr, bool Grayscale);
```

- [ ] **Step 2: Rewrite the failing tests**

Replace the contents of `tests/Inkshelf.Tests/ScreenTargetTests.cs`:

```csharp
using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ScreenTargetTests
{
    [Fact]
    public void Non_retina_uses_css_size_and_dpr_1()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", retina: false);
        Assert.Equal(769, t.MaxW);
        Assert.Equal(953, t.MaxH);
        Assert.Equal(1.0, t.Dpr, 3);
    }

    [Fact]
    public void Retina_scales_cap_by_dpr_and_keeps_dpr()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", retina: true);
        Assert.Equal(1442, t.MaxW); // round(769 * 1.875)
        Assert.Equal(1787, t.MaxH); // round(953 * 1.875)
        Assert.Equal(1.875, t.Dpr, 3);
    }

    [Fact]
    public void Grayscale_flag_is_passed_through()
    {
        Assert.True(ScreenTarget.FromCookie("769x953x1", grayscale: true).Grayscale);
        Assert.False(ScreenTarget.FromCookie("769x953x1").Grayscale);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Missing_or_unparseable_cookie_is_safe(string? scr)
    {
        var t = ScreenTarget.FromCookie(scr);
        Assert.Equal(0, t.MaxW);
        Assert.Equal(0, t.MaxH);
        Assert.Equal(1.0, t.Dpr, 3);
    }

    [Fact]
    public void Legacy_two_part_cookie_still_parses()
    {
        var t = ScreenTarget.FromCookie("769x953");
        Assert.Equal(769, t.MaxW);
        Assert.Equal(953, t.MaxH);
        Assert.Equal(1.0, t.Dpr, 3);
    }

    [Fact]
    public void Non_retina_clamps_oversized_dimensions()
    {
        var t = ScreenTarget.FromCookie("9999x9999x1");
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxW);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxH);
    }

    [Fact]
    public void Retina_clamps_AFTER_multiplying_by_dpr()
    {
        // 3000 * 2 = 6000 → must clamp to MaxDimension (bug was clamping 3000 first).
        var t = ScreenTarget.FromCookie("3000x3000x2", retina: true);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxW);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxH);
    }

    [Fact]
    public void Dpr_is_bounded()
    {
        var t = ScreenTarget.FromCookie("10x10x999", retina: true);
        Assert.Equal(ScreenTarget.MaxDpr, t.Dpr, 3);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter ScreenTargetTests`
Expected: FAIL — `FromCookie` signature/return type and `MaxDpr` don't exist yet (compile error).

- [ ] **Step 4: Rewrite `ScreenTarget`**

Replace the contents of `src/Inkshelf/Convert/ScreenTarget.cs`:

```csharp
using System.Globalization;

namespace Inkshelf.Convert;

public static class ScreenTarget
{
    // Upper bound on a page dimension fed into the converter + cache key, so a
    // client-set "scr" cookie can't mint absurd sizes (disk exhaustion / OOM).
    public const int MaxDimension = 4096;

    // Upper bound on the client-supplied device-pixel-ratio. Bounded because it
    // multiplies the page dimensions under retina — an unbounded dpr would blow
    // past MaxDimension's intent.
    public const double MaxDpr = 4.0;

    // Parse the "scr" cookie ("<cssW>x<cssH>x<dpr>", written by the layout script)
    // into a RenderTarget. The Tolino reader lays fixed-layout pages out in CSS
    // pixels, so the viewport must be the CSS size to fill the screen.
    //
    //   retina = false → cap = CSS size,        Dpr = 1   (image == page == CSS; softer, light)
    //   retina = true  → cap = CSS size × dpr,  Dpr = dpr (physical image in a CSS page; crisp, heavy)
    //
    // dpr is bounded to MaxDpr, and dimensions are clamped to MaxDimension AFTER
    // the dpr multiply (a raw cssW × dpr must not exceed the cap). Returns
    // (0, 0, 1, grayscale) when absent/unparseable → no downscaling.
    public static RenderTarget FromCookie(string? scr, bool retina = false, bool grayscale = false)
    {
        if (!string.IsNullOrEmpty(scr))
        {
            var p = scr.Split('x');
            if (p.Length >= 3
                && int.TryParse(p[0], out var cw) && int.TryParse(p[1], out var ch)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dpr)
                && cw > 0 && ch > 0 && dpr > 0)
            {
                if (retina)
                {
                    dpr = Math.Min(dpr, MaxDpr);
                    var w = Math.Min((int)Math.Round(cw * dpr), MaxDimension);
                    var h = Math.Min((int)Math.Round(ch * dpr), MaxDimension);
                    return new RenderTarget(w, h, dpr, grayscale);
                }
                return new RenderTarget(Math.Min(cw, MaxDimension), Math.Min(ch, MaxDimension), 1, grayscale);
            }
            // Legacy 2-part physical cookie, transient until the script rewrites it.
            if (p.Length == 2 && int.TryParse(p[0], out var w2) && int.TryParse(p[1], out var h2) && w2 > 0 && h2 > 0)
                return new RenderTarget(Math.Min(w2, MaxDimension), Math.Min(h2, MaxDimension), 1, grayscale);
        }
        return new RenderTarget(0, 0, 1, grayscale);
    }
}
```

- [ ] **Step 5: Update the two call sites to consume `RenderTarget`**

In `src/Inkshelf/Endpoints/ConvertEndpoints.cs`, replace the destructure at line 12 and its uses:

```csharp
var t = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"]);

// JS poll: report status, no enqueue.
if (status is "1")
{
    var s = await convert.StatusAsync(id, t.MaxW, t.MaxH, t.Dpr, ct);
    return s.Status == ConvertStatus.None
        ? Results.NotFound()
        : Results.Text(Text(s.Status));
}

var result = await convert.KickAsync(id, fresh is "1" or "true", t.MaxW, t.MaxH, t.Dpr, ct);
```

In `src/Inkshelf/Pages/Library.cshtml.cs`, `ComputeConvertStates` (line ~103):

```csharp
var t = ScreenTarget.FromCookie(Request.Cookies["scr"]);
foreach (var item in Items)
{
    _structured.TryGetValue(item.Id, out var media);
    var state = RowState(item, media, t.MaxW, t.MaxH);
    _states[item.Id] = state;
    if (state == ConvertRowState.Converting) AnyConverting = true;
}
```

(`RowState(AbsItem, AbsBatchMedia?, int w, int h)` is unchanged this task.)

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS. Behavior is unchanged (retina defaults false, as before).

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Convert/RenderTarget.cs src/Inkshelf/Convert/ScreenTarget.cs \
  src/Inkshelf/Endpoints/ConvertEndpoints.cs src/Inkshelf/Pages/Library.cshtml.cs \
  tests/Inkshelf.Tests/ScreenTargetTests.cs
git commit -m "feat: introduce RenderTarget and parameterize ScreenTarget retina + dpr clamp"
```

---

### Task 3: Grayscale marker in the EPUB cache key

Colour and grayscale variants at the same dimensions must not collide on disk. Add an optional `grayscale` parameter to `EpubCache.PathFor`/`TryGet` (default `false` keeps existing callers compiling; wired for real in Tasks 5–6).

**Files:**
- Modify: `src/Inkshelf/Convert/EpubCache.cs`
- Test: `tests/Inkshelf.Tests/EpubCacheTests.cs` (add cases)

**Interfaces:**
- Produces:
  - `EpubCache.PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale = false)` → `string`
  - `EpubCache.TryGet(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale, out string path)` (grayscale added; existing 5-arg callers add `false`)

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EpubCacheTests.cs`:

```csharp
[Fact]
public void PathFor_grayscale_differs_from_colour()
{
    var c = new EpubCache(TempDirPath());
    Assert.NotEqual(
        c.PathFor("i1", 100, 200, 800, 1000, grayscale: false),
        c.PathFor("i1", 100, 200, 800, 1000, grayscale: true));
}

[Fact]
public void PathFor_grayscale_uses_g_marker()
{
    var c = new EpubCache(TempDirPath());
    Assert.EndsWith("i1-100-200-800x1000-g.epub", c.PathFor("i1", 100, 200, 800, 1000, grayscale: true));
    Assert.EndsWith("i1-100-200-800x1000.epub", c.PathFor("i1", 100, 200, 800, 1000, grayscale: false));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter EpubCacheTests`
Expected: FAIL — `PathFor` has no `grayscale` parameter (compile error).

- [ ] **Step 3: Implement the marker**

In `src/Inkshelf/Convert/EpubCache.cs`, update `PathFor` and `TryGet`:

```csharp
// The downscale target (maxW×maxH) AND grayscale are part of the key: two
// devices with different screen resolutions, or colour vs grayscale, must not be
// served each other's variant.
public string PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale = false) =>
    Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}{(grayscale ? "-g" : "")}.epub");

public bool TryGet(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale, out string path)
{
    path = PathFor(itemId, size, mtimeMs, maxW, maxH, grayscale);
    return File.Exists(path);
}
```

(`RemoveForItem` still matches `{itemId}-*.epub`, which covers the `-g` variants — no change.)

- [ ] **Step 4: Fix the existing `TryGet` call sites**

`TryGet`'s new `grayscale` parameter is **required** (an optional parameter can't precede the `out` parameter). Update the three existing calls in `tests/Inkshelf.Tests/EpubCacheTests.cs` (lines ~52–54) to pass `false`:

```csharp
Assert.False(c.TryGet("i1", 1, 1, 0, 0, false, out _));
Assert.False(c.TryGet("i1", 2, 2, 800, 1000, false, out _));
Assert.True(c.TryGet("i2", 1, 1, 0, 0, false, out _));
```

(The `PathFor` calls in that file are unaffected — its `grayscale` parameter defaults to `false`.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter EpubCacheTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/EpubCache.cs tests/Inkshelf.Tests/EpubCacheTests.cs
git commit -m "feat: key the EPUB cache on grayscale"
```

---

### Task 4: Grayscale desaturation in `PageImageProcessor`

When grayscale is requested, every page is decoded, desaturated, and re-encoded as JPEG — including in-bounds non-WebP images that would otherwise pass through untouched.

**Files:**
- Modify: `src/Inkshelf/Convert/PageImageProcessor.cs`
- Modify: `src/Inkshelf/Convert/EpubConverter.cs` (caller — pass `grayscale: false` literal for now; wired in Task 5)
- Test: `tests/Inkshelf.Tests/PageImageProcessorTests.cs` (add cases)

**Interfaces:**
- Produces: `PageImageProcessor.ProcessAsync(byte[] bytes, string extension, int maxWidth, int maxHeight, bool grayscale, CancellationToken ct)` → `Task<ProcessedImage>` (new required `grayscale` param before `ct`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/PageImageProcessorTests.cs` (add `using SixLabors.ImageSharp.PixelFormats;` at the top):

```csharp
private static byte[] Solid(int w, int h, byte r, byte g, byte b, SixLabors.ImageSharp.Formats.IImageEncoder enc)
{
    using var img = new Image<Rgba32>(w, h, new Rgba32(r, g, b));
    using var ms = new MemoryStream(); img.Save(ms, enc); return ms.ToArray();
}

[Fact]
public async Task ProcessAsync_grayscale_desaturates_in_bounds_image()
{
    var red = Solid(80, 120, 255, 0, 0, new JpegEncoder());
    var r = await PageImageProcessor.ProcessAsync(red, ".jpg", 0, 0, grayscale: true, default);
    Assert.NotSame(red, r.Bytes); // re-encoded, not passed through
    Assert.Equal(".jpg", r.Extension);

    using var outImg = Image.Load<Rgba32>(r.Bytes);
    var px = outImg[40, 60];
    Assert.True(Math.Abs(px.R - px.G) <= 4 && Math.Abs(px.G - px.B) <= 4,
        $"expected gray, got ({px.R},{px.G},{px.B})");
}

[Fact]
public async Task ProcessAsync_non_grayscale_still_passes_in_bounds_through()
{
    var bytes = Solid(80, 120, 255, 0, 0, new JpegEncoder());
    var r = await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, grayscale: false, default);
    Assert.Same(bytes, r.Bytes);
}
```

Update the three **existing** `PageImageProcessorTests` calls to pass the new argument (`grayscale: false` before `default`), e.g.:

```csharp
var r = await PageImageProcessor.ProcessAsync(Img(80, 120, new WebpEncoder()), ".webp", 0, 0, grayscale: false, default);
```
```csharp
var r = await PageImageProcessor.ProcessAsync(Img(400, 600, new JpegEncoder()), ".jpg", 200, 200, grayscale: false, default);
```
```csharp
var r = await PageImageProcessor.ProcessAsync(bytes, ".jpg", 0, 0, grayscale: false, default);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter PageImageProcessorTests`
Expected: FAIL — `ProcessAsync` has no `grayscale` parameter (compile error).

- [ ] **Step 3: Implement grayscale**

In `src/Inkshelf/Convert/PageImageProcessor.cs`, update `ProcessAsync`:

```csharp
public static async Task<ProcessedImage> ProcessAsync(byte[] bytes, string extension,
    int maxWidth, int maxHeight, bool grayscale, CancellationToken ct)
{
    var cap = maxWidth > 0 && maxHeight > 0;
    var info = Image.Identify(bytes);
    var (w, h) = (info.Width, info.Height);
    var oversized = cap && (w > maxWidth || h > maxHeight);
    if (oversized || extension == ".webp" || grayscale)
    {
        using var img = Image.Load(bytes);
        if (oversized)
        {
            var scale = Math.Min((double)maxWidth / img.Width, (double)maxHeight / img.Height);
            img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * scale)),
                                     Math.Max(1, (int)Math.Round(img.Height * scale))));
        }
        if (grayscale) img.Mutate(x => x.Grayscale());
        using var outMs = new MemoryStream();
        await img.SaveAsJpegAsync(outMs, ct);
        return new ProcessedImage(outMs.ToArray(), ".jpg", img.Width, img.Height);
    }
    return new ProcessedImage(bytes, extension, w, h);
}
```

- [ ] **Step 4: Update the `EpubConverter` caller (keep the build green)**

In `src/Inkshelf/Convert/EpubConverter.cs`, `ProcessPagesAsync`, pass a literal `false` (real value threaded in Task 5):

```csharp
var img = await PageImageProcessor.ProcessAsync(raw.Bytes, ext, maxWidth, maxHeight, grayscale: false, ct);
```

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Convert/PageImageProcessor.cs src/Inkshelf/Convert/EpubConverter.cs \
  tests/Inkshelf.Tests/PageImageProcessorTests.cs
git commit -m "feat: support grayscale page desaturation in PageImageProcessor"
```

---

### Task 5: Thread `RenderTarget` through the conversion pipeline

Carry the full render target (incl. grayscale) from the endpoint down to the cache key and the image processor, replacing the loose `(maxW, maxH, dpr)` params. This activates grayscale in the cache key and the pipeline; retina/grayscale values still originate as `false` until Task 6 reads the cookie.

**Files:**
- Modify: `src/Inkshelf/Convert/ConvertJob.cs`
- Modify: `src/Inkshelf/Convert/ConvertService.cs`
- Modify: `src/Inkshelf/Convert/ConvertWorker.cs`
- Modify: `src/Inkshelf/Convert/EpubConverter.cs`
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs`
- Test (adjust call sites — see Step 6): `tests/Inkshelf.Tests/ConvertServiceTests.cs`, `tests/Inkshelf.Tests/EpubConverterTests.cs`, `tests/Inkshelf.Tests/ConvertWorkerTests.cs`, `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: `RenderTarget` (Task 2), `EpubCache.PathFor(..., grayscale)` (Task 3), `PageImageProcessor.ProcessAsync(..., grayscale, ct)` (Task 4).
- Produces:
  - `record ConvertJob(string ItemId, string AccessToken, string CachePath, EbookMeta Meta, RenderTarget Target)`
  - `ConvertService.KickAsync(string id, bool fresh, RenderTarget target, CancellationToken ct)` → `Task<KickResult>`
  - `ConvertService.StatusAsync(string id, RenderTarget target, CancellationToken ct)` → `Task<KickResult>`
  - `EpubConverter.ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target, CancellationToken ct)` → `Task`

- [ ] **Step 1: Update `ConvertJob` to carry the target**

In `src/Inkshelf/Convert/ConvertJob.cs`, replace the record:

```csharp
public sealed record ConvertJob(
    string ItemId, string AccessToken, string CachePath,
    EbookMeta Meta, RenderTarget Target);
```

- [ ] **Step 2: Update `ConvertService`**

In `src/Inkshelf/Convert/ConvertService.cs`, change the three methods to take `RenderTarget`:

```csharp
public async Task<KickResult> KickAsync(string id, bool fresh, RenderTarget target, CancellationToken ct)
{
    var r = await ResolveAsync(id, target, ct);
    if (r is null) return new KickResult(ConvertStatus.None);
    var (path, meta, downloadName) = r.Value;

    if (fresh) _cache.RemoveForItem(id);
    if (System.IO.File.Exists(path))
    {
        _cache.Touch(path);
        return new KickResult(ConvertStatus.Done, path, downloadName);
    }

    var tokens = _tokens.Read();
    if (tokens is null) return new KickResult(ConvertStatus.None); // no session
    var status = _queue.Enqueue(new ConvertJob(id, tokens.Access, path, meta, target));
    return new KickResult(status);
}

public async Task<KickResult> StatusAsync(string id, RenderTarget target, CancellationToken ct)
{
    var r = await ResolveAsync(id, target, ct);
    if (r is null) return new KickResult(ConvertStatus.None);
    var (path, _, downloadName) = r.Value;
    var status = _queue.Status(path);
    return status == ConvertStatus.Done
        ? new KickResult(ConvertStatus.Done, path, downloadName)
        : new KickResult(status);
}

private async Task<(string Path, EbookMeta Meta, string DownloadName)?> ResolveAsync(
    string id, RenderTarget target, CancellationToken ct)
{
    // ...unchanged detail-fetch/validation code down to the path line...
    var path = _cache.PathFor(id, ef.Metadata.Size, ef.Metadata.MtimeMs, target.MaxW, target.MaxH, target.Grayscale);
    var meta = new EbookMeta(title, author, seriesName, seq, id);
    var downloadName = Sanitize($"{author} - {title}") + ".epub";
    return (path, meta, downloadName);
}
```

(Only the signatures and the `PathFor` call change; the detail-fetch/validation body of `ResolveAsync` is untouched.)

- [ ] **Step 3: Update `ConvertWorker`**

In `src/Inkshelf/Convert/ConvertWorker.cs`, `ProcessAsync`, change the converter call:

```csharp
await using (var read = new FileStream(dlTmp, FileMode.Open, FileAccess.Read))
    await _converter.ConvertAsync(read, job.Meta, job.CachePath, job.Target, ct);
```

- [ ] **Step 4: Update `EpubConverter`**

In `src/Inkshelf/Convert/EpubConverter.cs`, take the `RenderTarget` and thread grayscale + dpr:

```csharp
public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target, CancellationToken ct)
{
    var dpr = target.Dpr <= 0 ? 1 : target.Dpr;
    await EpubWriter.WriteAsync(outPath, meta,
        ProcessPagesAsync(archive, target.MaxW, target.MaxH, target.Grayscale, ct), dpr, ct);
}

private static async IAsyncEnumerable<EpubWriter.Page> ProcessPagesAsync(
    Stream archive, int maxWidth, int maxHeight, bool grayscale, [EnumeratorCancellation] CancellationToken ct)
{
    var idx = 0;
    await foreach (var raw in ComicArchiveReader.ReadAsync(archive, ct))
    {
        ct.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(raw.Key).ToLowerInvariant();
        var img = await PageImageProcessor.ProcessAsync(raw.Bytes, ext, maxWidth, maxHeight, grayscale, ct);
        idx++;
        yield return new EpubWriter.Page($"page-{idx:D4}{img.Extension}", img.Bytes, img.Width, img.Height);
    }
}
```

- [ ] **Step 5: Update `ConvertEndpoints`**

In `src/Inkshelf/Endpoints/ConvertEndpoints.cs`, pass the `RenderTarget` directly:

```csharp
var t = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"]);

if (status is "1")
{
    var s = await convert.StatusAsync(id, t, ct);
    return s.Status == ConvertStatus.None ? Results.NotFound() : Results.Text(Text(s.Status));
}

var result = await convert.KickAsync(id, fresh is "1" or "true", t, ct);
```

- [ ] **Step 6: Fix ALL test call sites of the changed signatures**

The `ConvertJob`, `ConvertService`, and `EpubConverter.ConvertAsync` signature changes break several existing test files. Update every one so the tree compiles (add `using Inkshelf.Convert;` where missing):

**`tests/Inkshelf.Tests/ConvertServiceTests.cs`** — every `KickAsync`/`StatusAsync` call (lines ~55, 67, 83, 96, 109):

```csharp
// before: await svc.KickAsync("item1", fresh: false, 100, 200, 1.0, default);
await svc.KickAsync("item1", fresh: false, new RenderTarget(100, 200, 1.0, false), default);
```
```csharp
// before: await svc.StatusAsync("item1", 100, 200, 1.0, default);
await svc.StatusAsync("item1", new RenderTarget(100, 200, 1.0, false), default);
```

**`tests/Inkshelf.Tests/EpubConverterTests.cs`** — the three `ConvertAsync` calls (lines ~37, 78, 100), replacing the trailing `maxW, maxH, dpr` with a `RenderTarget`:

```csharp
// before: ...ConvertAsync(Cbz(), new EbookMeta("Vol 1","Artist","Saga","1"), outPath, 0, 0, 1, default);
await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("Vol 1", "Artist", "Saga", "1"), outPath, new RenderTarget(0, 0, 1, false), default);
```
```csharp
// before: ...ConvertAsync(ms, new EbookMeta("T","A",null,null), outPath, 200, 200, 1, default);
await new EpubConverter().ConvertAsync(ms, new EbookMeta("T", "A", null, null), outPath, new RenderTarget(200, 200, 1, false), default);
```
```csharp
// before: ...ConvertAsync(ms, new EbookMeta("T","A",null,null), outPath, 0, 0, 2, default);
await new EpubConverter().ConvertAsync(ms, new EbookMeta("T", "A", null, null), outPath, new RenderTarget(0, 0, 2, false), default);
```

**`tests/Inkshelf.Tests/ConvertWorkerTests.cs`** — the `Job` helper (line ~51):

```csharp
private static ConvertJob Job(string path) =>
    new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"), new RenderTarget(0, 0, 1.0, false));
```

**`tests/Inkshelf.Tests/ListingRenderTests.cs`** — the `new ConvertJob(...)` at line ~115:

```csharp
queue.Enqueue(new ConvertJob(ItemId, "tok", path, new EbookMeta("T", "A", null, null, ItemId), new RenderTarget(W, H, 1.0, false)));
```

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/Convert/ConvertJob.cs src/Inkshelf/Convert/ConvertService.cs \
  src/Inkshelf/Convert/ConvertWorker.cs src/Inkshelf/Convert/EpubConverter.cs \
  src/Inkshelf/Endpoints/ConvertEndpoints.cs tests/Inkshelf.Tests/ConvertServiceTests.cs
git commit -m "refactor: thread RenderTarget through the conversion pipeline"
```

---

### Task 6: Read the settings cookie in the convert endpoint + row-state

Activate the feature end-to-end: build the `RenderTarget` from **both** the `scr` probe and the `inkshelf_settings` cookie, in the two places that compute it, so a real conversion and the "✓ converted" badge agree.

**Files:**
- Modify: `src/Inkshelf/Endpoints/ConvertEndpoints.cs`
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs` (`ComputeConvertStates` + `RowState`)
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs` (add a grayscale-variant cache-hit assertion — see Step 3)

**Interfaces:**
- Consumes: `DeviceSettings.Read` (Task 1), `ScreenTarget.FromCookie(scr, retina, grayscale)` (Task 2), `EpubCache.PathFor(..., grayscale)` (Task 3).
- Produces: `RowState(AbsItem item, AbsBatchMedia? media, RenderTarget target)` (signature changes from `(…, int w, int h)`).

- [ ] **Step 1: Update `ConvertEndpoints` to read the settings cookie**

In `src/Inkshelf/Endpoints/ConvertEndpoints.cs`, build the target from both cookies (add `using Inkshelf.Auth;`):

```csharp
var s = DeviceSettings.Read(httpContext.Request);
var t = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"], s.Retina, s.Grayscale);
```

(The rest of the handler — `StatusAsync(id, t, ct)`, `KickAsync(id, …, t, ct)` — is unchanged from Task 5.)

- [ ] **Step 2: Update `Library.cshtml.cs` to match**

In `src/Inkshelf/Pages/Library.cshtml.cs` (add `using Inkshelf.Auth;`), make `ComputeConvertStates` build the same target and pass it to `RowState`, and key the cache on grayscale:

```csharp
private void ComputeConvertStates()
{
    var s = DeviceSettings.Read(Request);
    var t = ScreenTarget.FromCookie(Request.Cookies["scr"], s.Retina, s.Grayscale);
    foreach (var item in Items)
    {
        _structured.TryGetValue(item.Id, out var media);
        var state = RowState(item, media, t);
        _states[item.Id] = state;
        if (state == ConvertRowState.Converting) AnyConverting = true;
    }
}

private ConvertRowState RowState(AbsItem item, AbsBatchMedia? media, RenderTarget target)
{
    var fmt = item.Media?.EbookFormat;
    if (fmt != "cbz" && fmt != "cbr") return ConvertRowState.NotConvertible;
    var efm = media?.EbookFile?.Metadata;
    if (efm is null) return ConvertRowState.NotConvertible; // can't key the cache
    var path = _cache.PathFor(item.Id, efm.Size, efm.MtimeMs, target.MaxW, target.MaxH, target.Grayscale);
    return _queue.Status(path) switch
    {
        ConvertStatus.Done => ConvertRowState.Cached,
        ConvertStatus.Queued or ConvertStatus.Running => ConvertRowState.Converting,
        ConvertStatus.Failed => ConvertRowState.Failed,
        _ => ConvertRowState.Convert,
    };
}
```

- [ ] **Step 3: Add a row-state grayscale test**

`ListingRenderTests` renders `/library/{id}` end-to-end and can seed the cache dir. Add a test that a cached **grayscale** variant is only detected as converted when the `inkshelf_settings` cookie has `g=1`. Mirror the file's existing factory/stub setup (`CreateFactory(stub, cachePath, keysPath)` + `LibraryRequest`). Concretely:
- Pre-write an empty file at the grayscale cache path for a known item: `{itemId}-{size}-{mtime}-{w}x{h}-g.epub` (dimensions from the `scr` cookie the test request sends).
- Request the listing **with** `Cookie: scr=…; inkshelf_settings=r=0&g=1` → assert the row renders as converted (e.g. the "EPUB ↓" / `data-ready` marker the existing tests assert on).
- Request the same listing **without** the settings cookie (colour) → assert the same row renders as still-convertible (the `-g` file is not its cache path).

Follow the existing assertions in `ListingRenderTests` for exactly how a "converted" vs "convert" row is detected in the HTML; reuse those helpers rather than inventing new markers.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Endpoints/ConvertEndpoints.cs src/Inkshelf/Pages/Library.cshtml.cs \
  tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "feat: apply device settings to conversion and row-state"
```

---

### Task 7: `POST /settings` write endpoint

The action that writes the settings cookie, following the `/favorite` + `/logout` convention (minimal API, manual antiforgery validation, `DisableAntiforgery()` on the route).

**Files:**
- Create: `src/Inkshelf/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Inkshelf/Program.cs` (map the endpoint)
- Test: `tests/Inkshelf.Tests/EndpointTests.cs` (add cases)

**Interfaces:**
- Produces: `IEndpointRouteBuilder.MapSettingsEndpoints()` extension mapping `POST /settings`.
- Consumes: `DeviceSettings.Set` (Task 1).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EndpointTests.cs`. `GetAntiforgeryTokenAsync` there fetches `/login`; add a variant that fetches `/settings` (which will carry a form token once Task 8 lands, but the endpoint test can reuse the `/login` token + shared antiforgery cookie since the token is site-wide):

```csharp
[Fact]
public async Task Settings_post_sets_cookie_and_redirects()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var token = await GetAntiforgeryTokenAsync(client);
    var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = token,
        ["retina"] = "on",
        // grayscale checkbox unchecked → not sent
    });

    var response = await client.PostAsync("/settings", content);

    Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    Assert.Equal("/settings", response.Headers.Location?.OriginalString);
    var setCookie = response.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(";", v) : "";
    Assert.Contains("inkshelf_settings=10", setCookie); // retina on, grayscale off → "10"
}

[Fact]
public async Task Settings_post_without_antiforgery_returns_bad_request()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var response = await client.PostAsync("/settings", content: null);

    Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter EndpointTests`
Expected: FAIL — `/settings` returns 404 (route not mapped).

- [ ] **Step 3: Implement the endpoint**

Create `src/Inkshelf/Endpoints/SettingsEndpoints.cs`:

```csharp
using Inkshelf.Auth;
using Microsoft.AspNetCore.Antiforgery;

namespace Inkshelf.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/settings", async (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            var form = await ctx.Request.ReadFormAsync();
            // Unchecked checkboxes send no field → absent == off.
            var settings = new DeviceSettings(form.ContainsKey("retina"), form.ContainsKey("grayscale"));
            DeviceSettings.Set(ctx.Response, settings);
            return Results.Redirect("/settings"); // PRG: back to the page, showing saved state
        }).DisableAntiforgery();
    }
}
```

- [ ] **Step 4: Map it in `Program.cs`**

In `src/Inkshelf/Program.cs`, next to `app.MapSessionEndpoints();`:

```csharp
app.MapSessionEndpoints();
app.MapSettingsEndpoints();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter EndpointTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Endpoints/SettingsEndpoints.cs src/Inkshelf/Program.cs \
  tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: add POST /settings endpoint to write the settings cookie"
```

---

### Task 8: Settings page + cog entry links

The Settings Razor Page (form + device readout) and the cog-glyph links in the Index and Library heads.

**Files:**
- Create: `src/Inkshelf/Pages/Settings.cshtml`
- Create: `src/Inkshelf/Pages/Settings.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Index.cshtml`
- Modify: `src/Inkshelf/Pages/Library.cshtml` (the `.page-head` block)
- Modify: `src/Inkshelf/wwwroot/app.css`
- Test: `tests/Inkshelf.Tests/EndpointTests.cs` (add a GET render check)

**Interfaces:**
- Consumes: `DeviceSettings.Read` (Task 1).
- Produces: a `GET /settings` page rendering a `<form method="post" action="/settings">` with `retina` + `grayscale` checkboxes and an antiforgery token.

- [ ] **Step 1: Write the failing test**

Add to `tests/Inkshelf.Tests/EndpointTests.cs`:

```csharp
[Fact]
public async Task Settings_get_renders_form_with_checkboxes()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient();

    var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();

    Assert.Contains("name=\"retina\"", html);
    Assert.Contains("name=\"grayscale\"", html);
    Assert.Contains("action=\"/settings\"", html);
    Assert.Contains("__RequestVerificationToken", html);
}

[Fact]
public async Task Settings_get_checks_boxes_from_cookie()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient();

    var req = new HttpRequestMessage(HttpMethod.Get, "/settings");
    req.Headers.Add("Cookie", "inkshelf_settings=r=1&g=0");
    var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

    // retina checkbox is checked, grayscale is not. Assert the retina input carries "checked".
    var retinaInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"retina\"[^>]*>").Value;
    Assert.Contains("checked", retinaInput);
    var grayInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"grayscale\"[^>]*>").Value;
    Assert.DoesNotContain("checked", grayInput);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter EndpointTests`
Expected: FAIL — `/settings` GET returns 404 (no page).

- [ ] **Step 3: Write the page model**

Create `src/Inkshelf/Pages/Settings.cshtml.cs`:

```csharp
using Inkshelf.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class SettingsModel : PageModel
{
    public DeviceSettings Settings { get; private set; } = DeviceSettings.Default;

    // The raw device probe, shown as a read-only readout so "retina" has context.
    public string? DetectedScreen { get; private set; }

    public void OnGet()
    {
        Settings = DeviceSettings.Read(Request);
        DetectedScreen = FormatScreen(Request.Cookies["scr"]);
    }

    // "769x953x1.875" → "769 × 953 @ dpr 1.875". null when absent/unparseable.
    private static string? FormatScreen(string? scr)
    {
        if (string.IsNullOrEmpty(scr)) return null;
        var p = scr.Split('x');
        if (p.Length >= 3) return $"{p[0]} × {p[1]} @ dpr {p[2]}";
        if (p.Length == 2) return $"{p[0]} × {p[1]}";
        return null;
    }
}
```

- [ ] **Step 4: Write the page**

Create `src/Inkshelf/Pages/Settings.cshtml`:

```html
@page
@model Inkshelf.Pages.SettingsModel
<div class="page-head">
    <h1 class="page-title">
        <img src="~/img/icon-black.png" alt="" class="title-icon" />
        <a href="/?all=1">Libraries</a> <span class="crumb-sep">›</span> Settings
    </h1>
</div>

<p class="settings-note">These settings apply to <strong>this device / browser</strong> only.</p>
@if (Model.DetectedScreen is not null)
{
    <p class="settings-note">Detected screen: @Model.DetectedScreen</p>
}

<form class="settings-form" method="post" action="/settings">
    @Html.AntiForgeryToken()
    <p>
        <label>
            <input type="checkbox" name="retina" value="on" @(Model.Settings.Retina ? "checked" : "") />
            Retina pages (full-resolution; crisper but heavier — may strain low-memory readers)
        </label>
    </p>
    <p>
        <label>
            <input type="checkbox" name="grayscale" value="on" @(Model.Settings.Grayscale ? "checked" : "") />
            Grayscale pages (smaller files on e-ink)
        </label>
    </p>
    <p><button type="submit">Save</button></p>
</form>
```

- [ ] **Step 5: Add the cog entry links**

In `src/Inkshelf/Pages/Index.cshtml`, add the cog link **after** the logout form inside `.page-head`:

```html
<div class="page-head">
    <h1 class="page-title"><img src="~/img/icon-black.png" alt="" class="title-icon" /> Libraries</h1>
    <form class="logout-form" method="post" action="/logout">@Html.AntiForgeryToken()<button type="submit">Log out</button></form>
    <a class="settings-link" href="/settings" title="Settings" aria-label="Settings">⚙</a>
</div>
```

In `src/Inkshelf/Pages/Library.cshtml`, add the cog link **after** the searchbar form inside `.page-head`:

```html
    <form class="searchbar" method="get" action="/library/@Model.Id">
        <input type="search" name="q" value="@Model.Q" placeholder="Search…" />
        <button type="submit">Search</button>
    </form>
    <a class="settings-link" href="/settings" title="Settings" aria-label="Settings">⚙</a>
</div>
```

- [ ] **Step 6: Add CSS**

Append to `src/Inkshelf/wwwroot/app.css` (defensive: no flex `gap`, no `object-fit`):

```css
.settings-link { margin-left: .6rem; font-size: 1.6rem; line-height: 1; text-decoration: none; }
.settings-note { color: #555; margin: .4rem 0; }
.settings-form { margin: 1rem 0; }
.settings-form label { cursor: pointer; }
```

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Verify the page renders in the running app**

Use the `run` skill (or `dotnet run --project src/Inkshelf` on port 5099) and open `/settings`; confirm the two checkboxes, the "applies to this device" note, and the detected-screen readout render, and that toggling + Save round-trips (checkbox stays checked after redirect). Confirm the cog appears in the Index and Library heads.

- [ ] **Step 9: Commit**

```bash
git add src/Inkshelf/Pages/Settings.cshtml src/Inkshelf/Pages/Settings.cshtml.cs \
  src/Inkshelf/Pages/Index.cshtml src/Inkshelf/Pages/Library.cshtml \
  src/Inkshelf/wwwroot/app.css tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: add Settings page and cog entry links"
```

---

### Task 9: Documentation — ARCHITECTURE + ROADMAP

Record the new structure and move the shipped items to Done.

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Update `ARCHITECTURE.md`**

In the layout map (`Auth/` and `Convert/` and `Endpoints/` and `Pages/` sections), add:
- `Auth/` line: mention `DeviceSettings` (the per-device settings cookie) alongside `TokenStore` / `Favorites`.
- `Convert/ScreenTarget.cs` description: "parses the `scr` probe + settings flags into a `RenderTarget`"; add `Convert/RenderTarget.cs` — "resolved per-device render knobs (cap, dpr, grayscale)".
- `Endpoints/` line: add `Settings` to the list of endpoint groups.
- `Pages/` line: add `Settings`.

Add one load-bearing convention bullet under "Load-bearing conventions":

```markdown
- **Two device cookies, two purposes.** `scr` is JS-written device *truth* (the
  screen probe); `inkshelf_settings` (`DeviceSettings`) is server-written user
  *choice* (retina, grayscale). Wherever conversion is computed —
  `ConvertEndpoints` and the Library row-state — read **both** and combine them
  via `ScreenTarget.FromCookie(scr, retina, grayscale)` into a `RenderTarget`, so
  a real conversion and the "✓ converted" badge agree. Grayscale is part of the
  cache key (`-g` marker); retina already changes `maxW/maxH`.
```

- [ ] **Step 2: Update `ROADMAP.md`**

- **Priority section:** remove item 1 ("Settings system + retina toggle"). If the list is now empty, replace the intro line with a short note that the settings system shipped and the remaining backlog is below.
- **Settings section:** rewrite so the settings *system* is described as shipped, and keep **Resolution override** and **EPUB2 reflowable fallback** as remaining future settings (they are not built). Remove the retina and grayscale bullets (now Done).
- **Security section:** remove the **Retina dpr clamp** bullet (handled in Task 2).
- **Done section:** add:

```markdown
- **Per-device settings + retina/grayscale** — a server-written
  `inkshelf_settings` cookie (`DeviceSettings`) with a plain-`<form>` Settings
  page (cog link in the Index/Library heads) exposing a **retina** toggle
  (replaces the hard-coded `ScreenTarget.Retina`) and a **grayscale** toggle.
  Both flow through a `RenderTarget` into conversion + the cache key (grayscale
  `-g` marker); includes the retina dpr clamp-after-multiply + dpr bound fix.
```

- [ ] **Step 3: Commit**

```bash
git add docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "docs: record settings system + retina/grayscale, trim roadmap"
```

---

## Self-Review Notes

- **Spec coverage:** settings cookie (Task 1), Settings page + cog links (Task 8), retina toggle replacing the const + dpr-clamp fix (Tasks 2, 5, 6), grayscale toggle (Tasks 3, 4, 5, 6), `/settings` write endpoint (Task 7), docs incl. moving items to Done and removing the handled Security item (Task 9). Resolution override + EPUB2 explicitly stay on the roadmap (Task 9). Favorite cookie left separate. All covered.
- **Type consistency:** `RenderTarget(int MaxW, int MaxH, double Dpr, bool Grayscale)` used consistently; `ScreenTarget.FromCookie(scr, retina, grayscale)` returns it; `ConvertJob(..., RenderTarget Target)`; `KickAsync/StatusAsync(id, [fresh,] RenderTarget target, ct)`; `EpubConverter.ConvertAsync(..., RenderTarget target, ct)`; `EpubCache.PathFor(..., bool grayscale = false)`; `PageImageProcessor.ProcessAsync(..., bool grayscale, ct)`. Cross-checked across tasks.
- **Incrementality:** each task ends with a green build/test. Signature changes update their callers within the same task (literal `false` where the real value arrives later), so no task leaves the tree uncompilable.
