# UI Localisation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Localise Inkshelf's own UI strings (German first) via a file-backed JSON catalog, with English as the source-language key and per-string fallback.

**Architecture:** A singleton `LocalizationCatalog` loads `<lang>.json` files from a configurable directory at startup. A singleton `Localizer`, injected into every Razor view, resolves the request language (explicit `DeviceSettings` cookie choice → `Accept-Language` match → English) and looks up the English string as the key. There is no `CultureInfo`, no middleware, and no new NuGet dependency. Code-side user-facing strings (login errors, filter labels) stay as English literals and are localised at their view call site (`@L[Model.Error]`), so no PageModel needs the localizer.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, `System.Text.Json`, xUnit + `Microsoft.AspNetCore.Mvc.Testing`.

## Global Constraints

- **.NET 10**, ASP.NET Core Razor Pages, **no AOT**.
- **No new NuGet dependency.** Use `System.Text.Json` and the already-registered `IHttpContextAccessor`.
- **Near-zero client JS.** The only JS touched is the existing convert-status script in `_Layout.cshtml`; it gains localised label *values*, no new behaviour.
- **English is the key.** The English source string is the lookup key; there is no `en.json`. A miss returns the key verbatim.
- **Language codes are lowercase** by file-naming convention (`de.json` → `de`); the cookie sanitiser enforces `[a-z-]`, max 5 chars.
- **Commits:** Conventional Commits (`feat`/`test`/`docs`…), imperative lowercase subject, no `Co-Authored-By`/tool trailer.
- **Gate before PR:** `dotnet test` green and `dotnet format Inkshelf.sln --verify-no-changes` clean.

Design spec: `docs/superpowers/specs/2026-07-23-ui-localisation-design.md`.

---

### Task 1: Add `Lang` to `DeviceSettings`

Extend the per-device cookie with a language code, preserving backward compatibility with existing 2-char cookies.

**Files:**
- Modify: `src/Inkshelf/Auth/DeviceSettings.cs`
- Modify: `src/Inkshelf/Endpoints/SettingsEndpoints.cs:16-17` (constructor call gains the lang arg)
- Test: `tests/Inkshelf.Tests/DeviceSettingsTests.cs` (create)

**Interfaces:**
- Produces: `record DeviceSettings(bool Retina, bool Grayscale, string Lang)`; `Lang == ""` means *no explicit choice*. `DeviceSettings.Read(HttpRequest) → DeviceSettings`, `Serialize() → string`, `DeviceSettings.Default`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/DeviceSettingsTests.cs`:

```csharp
using Inkshelf.Auth;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Tests;

public class DeviceSettingsTests
{
    private static HttpRequest RequestWithCookie(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{DeviceSettings.Cookie}={value}";
        return ctx.Request;
    }

    [Fact]
    public void Serialize_roundtrips_lang()
    {
        Assert.Equal("10de", new DeviceSettings(true, false, "de").Serialize());
        Assert.Equal("11", new DeviceSettings(true, true, "").Serialize());
    }

    [Fact]
    public void Read_parses_flags_and_lang()
    {
        var s = DeviceSettings.Read(RequestWithCookie("10de"));
        Assert.True(s.Retina);
        Assert.False(s.Grayscale);
        Assert.Equal("de", s.Lang);
    }

    [Fact]
    public void Read_legacy_two_char_cookie_has_empty_lang()
    {
        var s = DeviceSettings.Read(RequestWithCookie("10"));
        Assert.True(s.Retina);
        Assert.Equal("", s.Lang);
    }

    [Fact]
    public void Read_junk_lang_sanitises_to_empty()
    {
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("10DE!")).Lang);
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("10toolongcode")).Lang);
    }

    [Fact]
    public void Read_accepts_region_code()
    {
        Assert.Equal("pt-br", DeviceSettings.Read(RequestWithCookie("00pt-br")).Lang);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DeviceSettingsTests`
Expected: FAIL — `DeviceSettings` has no 3-arg constructor / `Lang` member.

- [ ] **Step 3: Update `DeviceSettings`**

Edit `src/Inkshelf/Auth/DeviceSettings.cs` — change the record, `Default`, `Serialize`, `Read`, and add `SanitizeLang`:

```csharp
public sealed record DeviceSettings(bool Retina, bool Grayscale, string Lang)
{
    public const string Cookie = "inkshelf_settings";
    // Retina defaults ON — most readers want crisp pages; opt out per device.
    // Lang "" = no explicit choice yet (resolved from Accept-Language at render).
    public static readonly DeviceSettings Default = new(true, false, "");

    // "<retina><grayscale>" flags, then an optional lowercase language code
    // (e.g. "10de"). No cookie-reserved characters, so it survives cookie encoding.
    public string Serialize() => $"{(Retina ? 1 : 0)}{(Grayscale ? 1 : 0)}{Lang}";

    public static DeviceSettings Read(HttpRequest req)
    {
        // Two 0/1 flags, then an optional language code. Legacy 2-char cookies
        // parse with Lang "" (English/resolve). Anything malformed → Default.
        if (req.Cookies.TryGetValue(Cookie, out var v) && v is { Length: >= 2 }
            && v[0] is '0' or '1' && v[1] is '0' or '1')
            return new DeviceSettings(v[0] == '1', v[1] == '1',
                SanitizeLang(v.Length > 2 ? v[2..] : ""));
        return Default;
    }

    // Accept a short lowercase code (letters + dash), else "" (→ resolve from header).
    private static string SanitizeLang(string s)
    {
        if (s.Length is 0 or > 5) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or '-')) return "";
        return s;
    }

    public static void Set(HttpResponse res, DeviceSettings settings)
    {
        var forceSecure = res.HttpContext.RequestServices?.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
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

- [ ] **Step 4: Fix the one broken construction site**

Edit `src/Inkshelf/Endpoints/SettingsEndpoints.cs` — read `lang` from the form and pass it:

```csharp
            var form = await ctx.Request.ReadFormAsync();
            // Unchecked checkboxes send no field → absent == off. lang comes from
            // the <select>; Read sanitises it on the next request.
            var settings = new DeviceSettings(
                form.ContainsKey("retina"), form.ContainsKey("grayscale"), form["lang"].ToString());
            DeviceSettings.Set(ctx.Response, settings);
```

- [ ] **Step 5: Run tests + full suite**

Run: `dotnet test`
Expected: PASS (new `DeviceSettingsTests` + all existing tests still green).

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Auth/DeviceSettings.cs src/Inkshelf/Endpoints/SettingsEndpoints.cs tests/Inkshelf.Tests/DeviceSettingsTests.cs
git commit -m "feat: add per-device language to DeviceSettings"
```

---

### Task 2: `LocalizationCatalog`

Load `<lang>.json` files into an immutable lookup.

**Files:**
- Create: `src/Inkshelf/Localization/LocalizationCatalog.cs`
- Test: `tests/Inkshelf.Tests/LocalizationCatalogTests.cs`

**Interfaces:**
- Produces:
  - `LocalizationCatalog.Load(string dir, ILogger? logger = null) → LocalizationCatalog`
  - `string Get(string? lang, string key)` — translation, else `key`
  - `bool Has(string lang)`
  - `string DisplayName(string lang)` — `$name` value, else the code
  - `IReadOnlyCollection<string> Languages` — loaded codes (excludes English)

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/LocalizationCatalogTests.cs`:

```csharp
using Inkshelf.Localization;

namespace Inkshelf.Tests;

public class LocalizationCatalogTests
{
    private static string WriteLocales(params (string lang, string json)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "loc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        foreach (var (lang, json) in files)
            File.WriteAllText(Path.Combine(dir, $"{lang}.json"), json);
        return dir;
    }

    [Fact]
    public void Get_returns_translation_on_hit()
    {
        var c = LocalizationCatalog.Load(WriteLocales(("de", """{"Download":"Herunterladen"}""")));
        Assert.Equal("Herunterladen", c.Get("de", "Download"));
    }

    [Fact]
    public void Get_falls_back_to_key_on_miss_or_unknown_lang()
    {
        var c = LocalizationCatalog.Load(WriteLocales(("de", """{"Download":"Herunterladen"}""")));
        Assert.Equal("Save", c.Get("de", "Save"));      // missing key
        Assert.Equal("Download", c.Get("fr", "Download")); // unknown lang
        Assert.Equal("Download", c.Get(null, "Download")); // English
    }

    [Fact]
    public void Malformed_file_is_skipped_not_thrown()
    {
        var c = LocalizationCatalog.Load(WriteLocales(
            ("de", "{ this is not json "),
            ("es", """{"Download":"Descargar"}""")));
        Assert.False(c.Has("de"));
        Assert.Equal("Descargar", c.Get("es", "Download"));
    }

    [Fact]
    public void Missing_directory_yields_empty_catalog()
    {
        var c = LocalizationCatalog.Load(Path.Combine(Path.GetTempPath(), "nope-" + Path.GetRandomFileName()));
        Assert.Empty(c.Languages);
        Assert.Equal("Download", c.Get("de", "Download"));
    }

    [Fact]
    public void DisplayName_uses_name_key_then_falls_back_to_code()
    {
        var c = LocalizationCatalog.Load(WriteLocales(
            ("de", """{"$name":"Deutsch","Download":"Herunterladen"}"""),
            ("es", """{"Download":"Descargar"}""")));
        Assert.Equal("Deutsch", c.DisplayName("de"));
        Assert.Equal("es", c.DisplayName("es"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LocalizationCatalogTests`
Expected: FAIL — `LocalizationCatalog` does not exist.

- [ ] **Step 3: Implement `LocalizationCatalog`**

Create `src/Inkshelf/Localization/LocalizationCatalog.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Inkshelf.Localization;

// Immutable in-memory translation catalog loaded once at startup from
// <lang>.json files. English has no file: the source string is the key, and a
// miss returns the key verbatim. See the UI localisation design spec.
public sealed class LocalizationCatalog
{
    public const string NameKey = "$name";

    // lang code (case-insensitive) → (English key → translation)
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byLang;

    private LocalizationCatalog(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byLang)
        => _byLang = byLang;

    // Loaded language codes (English is implicit and not listed).
    public IReadOnlyCollection<string> Languages => (IReadOnlyCollection<string>)_byLang.Keys;

    public bool Has(string lang) => _byLang.ContainsKey(lang);

    // Translation for key in lang, or the key itself (English fallback) when the
    // language is unknown/null or the key is absent/empty.
    public string Get(string? lang, string key)
        => lang is not null && _byLang.TryGetValue(lang, out var d)
           && d.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : key;

    // Menu label: the file's "$name", else the bare code.
    public string DisplayName(string lang)
        => _byLang.TryGetValue(lang, out var d) && d.TryGetValue(NameKey, out var n)
           && !string.IsNullOrWhiteSpace(n) ? n : lang;

    // Load every *.json in dir. A malformed/unreadable file is logged and skipped
    // — a bad translation file must never crash the sidecar. Missing dir → empty.
    public static LocalizationCatalog Load(string dir, ILogger? logger = null)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                    if (map is not null) result[lang] = map;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Skipping malformed locale file {File}", file);
                }
            }
        }
        return new LocalizationCatalog(result);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LocalizationCatalogTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Localization/LocalizationCatalog.cs tests/Inkshelf.Tests/LocalizationCatalogTests.cs
git commit -m "feat: add LocalizationCatalog for JSON translation files"
```

---

### Task 3: `Localizer`

Resolve the request language and expose the view-facing lookup.

**Files:**
- Create: `src/Inkshelf/Localization/Localizer.cs`
- Test: `tests/Inkshelf.Tests/LocalizerTests.cs`

**Interfaces:**
- Consumes: `LocalizationCatalog` (Task 2), `DeviceSettings.Read` (Task 1), `IHttpContextAccessor`.
- Produces: `Localizer(LocalizationCatalog, IHttpContextAccessor)`; indexers `string this[string key]` and `string this[string key, params object?[] args]`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Inkshelf.Tests/LocalizerTests.cs`:

```csharp
using Inkshelf.Auth;
using Inkshelf.Localization;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Tests;

public class LocalizerTests
{
    private static string WriteDe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "de.json"),
            """{"Download":"Herunterladen","Page {0} of {1}":"Seite {0} von {1}"}""");
        return dir;
    }

    private static Localizer ForRequest(Action<HttpRequest> setup)
    {
        var catalog = LocalizationCatalog.Load(WriteDe());
        var ctx = new DefaultHttpContext();
        setup(ctx.Request);
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new Localizer(catalog, accessor);
    }

    [Fact]
    public void Explicit_cookie_choice_wins()
    {
        var l = ForRequest(r =>
        {
            r.Headers.Cookie = $"{DeviceSettings.Cookie}=10de";
            r.Headers.AcceptLanguage = "en-US";
        });
        Assert.Equal("Herunterladen", l["Download"]);
    }

    [Fact]
    public void Explicit_english_overrides_german_browser()
    {
        var l = ForRequest(r =>
        {
            r.Headers.Cookie = $"{DeviceSettings.Cookie}=10en";
            r.Headers.AcceptLanguage = "de-DE,de;q=0.9";
        });
        Assert.Equal("Download", l["Download"]);
    }

    [Fact]
    public void No_choice_falls_back_to_accept_language()
    {
        var l = ForRequest(r => r.Headers.AcceptLanguage = "de-DE,de;q=0.9,en;q=0.8");
        Assert.Equal("Herunterladen", l["Download"]);
    }

    [Fact]
    public void No_match_falls_back_to_english()
    {
        var l = ForRequest(r => r.Headers.AcceptLanguage = "fr-FR,fr;q=0.9");
        Assert.Equal("Download", l["Download"]);
    }

    [Fact]
    public void Format_args_applied_to_resolved_template()
    {
        var l = ForRequest(r => r.Headers.Cookie = $"{DeviceSettings.Cookie}=10de");
        Assert.Equal("Seite 2 von 5", l["Page {0} of {1}", 2, 5]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LocalizerTests`
Expected: FAIL — `Localizer` does not exist.

- [ ] **Step 3: Implement `Localizer`**

Create `src/Inkshelf/Localization/Localizer.cs`:

```csharp
using Inkshelf.Auth;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Localization;

// View-facing localiser. Injected once via _ViewImports (`@inject Localizer L`)
// and used as `L["English string"]`. Resolves the request language itself (from
// the DeviceSettings cookie, then Accept-Language) so strings in the layout and
// shared partials — which have no PageModel — need no plumbing.
public sealed class Localizer
{
    private readonly LocalizationCatalog _catalog;
    private readonly IHttpContextAccessor _http;

    public Localizer(LocalizationCatalog catalog, IHttpContextAccessor http)
    {
        _catalog = catalog;
        _http = http;
    }

    public string this[string key] => _catalog.Get(CurrentLang(), key);

    public string this[string key, params object?[] args]
        => string.Format(_catalog.Get(CurrentLang(), key), args);

    // Explicit cookie choice (incl. "en") → best Accept-Language match among
    // loaded catalogs → null (English). Never writes anything.
    public string? CurrentLang()
    {
        var req = _http.HttpContext?.Request;
        if (req is null) return null;

        var chosen = DeviceSettings.Read(req).Lang;
        if (!string.IsNullOrEmpty(chosen)) return chosen;

        foreach (var h in req.GetTypedHeaders().AcceptLanguage.OrderByDescending(x => x.Quality ?? 1.0))
        {
            var code = h.Value.ToString();
            if (string.IsNullOrEmpty(code)) continue;
            if (_catalog.Has(code)) return code;
            var dash = code.IndexOf('-');
            if (dash > 0 && _catalog.Has(code[..dash])) return code[..dash];
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LocalizerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Localization/Localizer.cs tests/Inkshelf.Tests/LocalizerTests.cs
git commit -m "feat: add request-scoped Localizer with language resolution"
```

---

### Task 4: Wire DI, config, the German catalog, and view injection

Make the machinery live without changing any rendered text yet.

**Files:**
- Modify: `src/Inkshelf/AbsOptions.cs`
- Modify: `src/Inkshelf/Program.cs` (config bind ~line 20; registrations before `AddRazorPages` ~line 64)
- Modify: `src/Inkshelf/Inkshelf.csproj`
- Modify: `src/Inkshelf/Pages/_ViewImports.cshtml`
- Create: `src/Inkshelf/locales/de.json`
- Modify: `README.md` (configuration list — add `LOCALES_PATH`)

**Interfaces:**
- Produces: `AbsOptions.LocalesPath`; a registered singleton `LocalizationCatalog` and singleton `Localizer`; `@inject Localizer L` available in all views.

- [ ] **Step 1: Add `LocalesPath` to `AbsOptions`**

Edit `src/Inkshelf/AbsOptions.cs` — add after `DataProtectionKeysPath`:

```csharp
    // Directory holding <lang>.json UI translation files, scanned at startup.
    // Null → "<ContentRoot>/locales". Drop a file in + restart to add a language.
    public string? LocalesPath { get; set; }
```

And extend the config-keys comment near the top to include `LOCALES_PATH`.

- [ ] **Step 2: Bind config and register services in `Program.cs`**

In the `absOptions` initializer (near line 15) add:

```csharp
    LocalesPath = builder.Configuration["LOCALES_PATH"],
```

Add `using Inkshelf.Localization;` to the top usings. Then, immediately before `builder.Services.AddRazorPages(` (line 64), add:

```csharp
// UI localisation: load <lang>.json once at startup; Localizer resolves the
// per-request language and is injected into every view.
var localesPath = absOptions.LocalesPath
    ?? Path.Combine(builder.Environment.ContentRootPath, "locales");
builder.Services.AddSingleton(sp =>
    LocalizationCatalog.Load(localesPath, sp.GetService<ILoggerFactory>()?.CreateLogger("Localization")));
builder.Services.AddSingleton<Localizer>();
```

(`AddHttpContextAccessor()` is already registered at line 40.)

- [ ] **Step 3: Copy `locales/**` to output**

Edit `src/Inkshelf/Inkshelf.csproj` — add inside a new `<ItemGroup>`:

```xml
  <ItemGroup>
    <!-- Ship translation files next to the app; scanned at startup from
         LOCALES_PATH (default <ContentRoot>/locales). Update (not Include):
         the Web SDK already globs *.json as content. -->
    <Content Update="locales\**\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Inject the localizer into all views**

Edit `src/Inkshelf/Pages/_ViewImports.cshtml` — append:

```cshtml
@using Inkshelf.Localization
@inject Localizer L
```

- [ ] **Step 5: Create the German catalog**

Create `src/Inkshelf/locales/de.json` with the full key set (English source string → German). Every string wrapped in later tasks must have its key here:

```json
{
  "$name": "Deutsch",

  "Libraries": "Bibliotheken",
  "Settings": "Einstellungen",
  "Log out": "Abmelden",
  "Log in": "Anmelden",
  "Converted": "Konvertiert",
  "Converted on this device": "Auf diesem Gerät konvertiert",
  "Item": "Element",

  "Prev": "Zurück",
  "Next": "Weiter",
  "Page {0} of {1}": "Seite {0} von {1}",

  "(untitled)": "(ohne Titel)",
  "Download": "Herunterladen",
  "Mark read": "Gelesen markieren",
  "Read": "Gelesen",
  "Mark as read": "Als gelesen markieren",
  "Mark as unread": "Als ungelesen markieren",

  "Convert": "Konvertieren",
  "Converting…": "Konvertiere…",
  "Convert (retry)": "Konvertieren (erneut)",
  "Already converted — downloads right away": "Bereits konvertiert – lädt sofort herunter",
  "Regenerate": "Neu erzeugen",

  "Username": "Benutzername",
  "Password": "Passwort",
  "Invalid username or password.": "Ungültiger Benutzername oder Passwort.",
  "Could not reach the server. Please try again.": "Server nicht erreichbar. Bitte erneut versuchen.",

  "These settings apply to this device / browser only.": "Diese Einstellungen gelten nur für dieses Gerät / diesen Browser.",
  "Detected screen: {0}": "Erkannter Bildschirm: {0}",
  "Retina pages (full-resolution; crisper but heavier — may strain low-memory readers)": "Retina-Seiten (volle Auflösung; schärfer, aber schwerer – kann Reader mit wenig Speicher belasten)",
  "Grayscale pages (smaller files on e-ink)": "Graustufen-Seiten (kleinere Dateien auf E-Ink)",
  "Save": "Speichern",
  "Language": "Sprache",

  "Unset favorite": "Favorit entfernen",
  "Set favorite": "Favorit setzen",
  "Search…": "Suchen…",
  "Search": "Suchen",
  "Results for": "Ergebnisse für",
  "clear": "zurücksetzen",
  "Books": "Bücher",
  "Series": "Serien",
  "Authors": "Autoren",
  "No matches.": "Keine Treffer.",
  "Filtered by": "Gefiltert nach",
  "Sort:": "Sortierung:",
  "Title": "Titel",
  "Author": "Autor",
  "Added": "Hinzugefügt",
  "Sequence": "Reihenfolge",
  "No items.": "Keine Einträge.",
  "Genre": "Genre",
  "Tag": "Tag",
  "Narrator": "Sprecher",
  "Filter": "Filter",

  "Authors:": "Autoren:",
  "Series:": "Serien:",
  "Narrators:": "Sprecher:",
  "Genres:": "Genres:",
  "Tags:": "Schlagwörter:",
  "Files": "Dateien",
  "No downloadable files.": "Keine herunterladbaren Dateien.",

  "Converted & cached on this device.": "Auf diesem Gerät konvertiert & zwischengespeichert.",
  "Couldn't load details from Audiobookshelf. Try again.": "Details konnten nicht von Audiobookshelf geladen werden. Bitte erneut versuchen.",
  "Nothing converted for this device yet.": "Für dieses Gerät wurde noch nichts konvertiert."
}
```

- [ ] **Step 6: Add `LOCALES_PATH` to the README configuration list**

Edit `README.md` — add a row/bullet in the configuration section:

```
- `LOCALES_PATH` — directory of `<lang>.json` UI translation files (default `<content-root>/locales`). Drop a file in and restart to add a language; no rebuild.
```

- [ ] **Step 7: Build and verify the catalog is deployed and loads**

Run: `dotnet build src/Inkshelf`
Expected: build succeeds with **no** NETSDK1022 duplicate-content warning. If that warning appears, the `Update` attribute in Step 3 is correct — re-check it is `Update`, not `Include`.

Run: `ls src/Inkshelf/bin/Debug/net10.0/locales/de.json`
Expected: the file exists (proves `CopyToOutputDirectory` works).

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS (no rendered text changed yet; existing tests unaffected).

- [ ] **Step 9: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs src/Inkshelf/Inkshelf.csproj src/Inkshelf/Pages/_ViewImports.cshtml src/Inkshelf/locales/de.json README.md
git commit -m "feat: load and inject UI translation catalog"
```

---

### Task 5: Localise the login page + end-to-end integration test

Wrap the login view and prove the whole pipeline renders German.

**Files:**
- Modify: `src/Inkshelf/Pages/Login.cshtml`
- Test: `tests/Inkshelf.Tests/LocalizationIntegrationTests.cs` (create)

**Interfaces:**
- Consumes: injected `L` (Task 4), `de.json` keys `Username`, `Password`, `Log in`, and the two login errors.

- [ ] **Step 1: Write the failing integration test**

Create `tests/Inkshelf.Tests/LocalizationIntegrationTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Inkshelf.Tests;

public class LocalizationIntegrationTests : IClassFixture<LocalizationIntegrationTests.Factory>
{
    private readonly Factory _factory;
    public LocalizationIntegrationTests(Factory factory) => _factory = factory;

    // GET /login renders without touching ABS, so it exercises catalog+localizer+view.
    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(c =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["ABS_URL"] = "http://abs.invalid" }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Login_page_is_english_by_default()
    {
        var client = _factory.CreateClient();
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        Assert.Contains("Log in", html);
        Assert.Contains("Password", html);
    }

    [Fact]
    public async Task Login_page_is_german_with_de_cookie()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/login");
        req.Headers.Add("Cookie", "inkshelf_settings=00de");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("Anmelden", html);    // "Log in"
        Assert.Contains("Passwort", html);    // "Password"
    }

    [Fact]
    public async Task Login_page_is_german_via_accept_language()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/login");
        req.Headers.Add("Accept-Language", "de-DE,de;q=0.9,en;q=0.8");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("Anmelden", html);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~LocalizationIntegrationTests`
Expected: the German tests FAIL (page still renders "Log in"/"Password"); the English test passes.

- [ ] **Step 3: Wrap the login view**

Edit `src/Inkshelf/Pages/Login.cshtml`:

```cshtml
@page
@model Inkshelf.Pages.LoginModel
<p class="logo"><img src="~/img/logo-black.png" alt="Inkshelf" /></p>
@if (Model.Error is not null) { <p role="alert">@L[Model.Error]</p> }
<form method="post" class="login-form">
    <p><label>@L["Username"]<br /><input name="Username" autocomplete="username" /></label></p>
    <p><label>@L["Password"]<br /><input name="Password" type="password" autocomplete="current-password" /></label></p>
    <p><button type="submit">@L["Log in"]</button></p>
</form>
```

(`@L[Model.Error]` localises the English error string set in `LoginModel` — the code needs no change.)

- [ ] **Step 4: Run the integration tests**

Run: `dotnet test --filter FullyQualifiedName~LocalizationIntegrationTests`
Expected: PASS (all three).

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Pages/Login.cshtml tests/Inkshelf.Tests/LocalizationIntegrationTests.cs
git commit -m "feat: localise login page + localisation integration test"
```

---

### Task 6: Settings language picker

Add the dropdown so a reader can choose a language.

**Files:**
- Modify: `src/Inkshelf/Pages/Settings.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Settings.cshtml`

**Interfaces:**
- Consumes: `LocalizationCatalog` (`Languages`, `DisplayName`), current `DeviceSettings.Lang`.
- Produces: `SettingsModel.AvailableLanguages` (`IReadOnlyList<(string Code, string Name)>`), `SettingsModel.CurrentLang`.

- [ ] **Step 1: Expose languages from the page model**

Edit `src/Inkshelf/Pages/Settings.cshtml.cs`:

```csharp
using Inkshelf.Auth;
using Inkshelf.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class SettingsModel : PageModel
{
    private readonly LocalizationCatalog _catalog;
    public SettingsModel(LocalizationCatalog catalog) => _catalog = catalog;

    public DeviceSettings Settings { get; private set; } = DeviceSettings.Default;
    public string? DetectedScreen { get; private set; }

    // English first (empty catalog = keys), then each loaded language.
    public IReadOnlyList<(string Code, string Name)> AvailableLanguages { get; private set; } = [];
    public string CurrentLang => Settings.Lang;

    public void OnGet()
    {
        Settings = DeviceSettings.Read(Request);
        DetectedScreen = FormatScreen(Request.Cookies["scr"]);
        var langs = new List<(string, string)> { ("en", "English") };
        foreach (var code in _catalog.Languages.OrderBy(c => c))
            langs.Add((code, _catalog.DisplayName(code)));
        AvailableLanguages = langs;
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

- [ ] **Step 2: Add the `<select>` to the settings form**

Edit `src/Inkshelf/Pages/Settings.cshtml` — add this `<p>` block inside `<form class="settings-form">`, above the `Save` button (other text in this view is localised in Task 8):

```cshtml
    <p>
        <label>@L["Language"]<br />
            <select name="lang">
                @foreach (var (code, name) in Model.AvailableLanguages)
                {
                    <option value="@code" selected="@(code == Model.CurrentLang)">@name</option>
                }
            </select>
        </label>
    </p>
```

Note: the `English` option carries value `en` (an explicit choice), never the empty/unset state — matches the spec.

- [ ] **Step 3: Build and verify manually**

Run: `ABS_URL=http://abs.invalid dotnet run --project src/Inkshelf` (port per your local convention), open `/settings`.
Expected: a **Language** dropdown listing `English` and `Deutsch`. Pick `Deutsch`, Save — the page reloads and the dropdown shows `Deutsch` selected (cookie `inkshelf_settings` now ends in `de`). Stop the server.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Pages/Settings.cshtml.cs src/Inkshelf/Pages/Settings.cshtml
git commit -m "feat: add language picker to Settings"
```

---

### Task 7: Localise shared chrome — layout JS labels + partials

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml` (convert-status JS labels)
- Modify: `src/Inkshelf/Pages/Shared/_Pager.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_ConvertAction.cshtml`

All keys already exist in `de.json` (Task 4).

- [ ] **Step 1: Set `<html lang>` and source the convert-status JS labels**

Edit `src/Inkshelf/Pages/Shared/_Layout.cshtml`. First, reflect the resolved language on the root element — change `<html lang="en">` to:

```cshtml
<html lang="@(L.CurrentLang() ?? "en")">
```

(`CurrentLang()` is the public resolver on `Localizer` from Task 3.)

Then, immediately before the second `<script>` block (the convert-status one), emit the localised labels:

```cshtml
    <script>
    var I18N = {
        convert: "@L["Convert"]",
        converting: "@L["Converting…"]",
        retry: "@L["Convert (retry)"]"
    };
    </script>
```

Then in that convert-status script, replace the four hardcoded English literals with `I18N` fields (leave `'EPUB ↓'` — a format name — as-is):

- `a.firstChild.nodeValue = 'Convert (retry)';` → `a.firstChild.nodeValue = I18N.retry;` (both occurrences: the `poll` failed-branch and the `kick` error-branch)
- in `poll`: `(s === 'failed') ? 'Convert (retry)' : 'Convert'` → `(s === 'failed') ? I18N.retry : I18N.convert`
- `a.firstChild.nodeValue = 'Converting…';` → `a.firstChild.nodeValue = I18N.converting;`

Leave the two `a.firstChild.nodeValue = 'EPUB ↓';` lines unchanged.

- [ ] **Step 2: Localise `_Pager.cshtml`**

```cshtml
@model Inkshelf.Pages.LibraryModel
<nav class="pager">
    @if (Model.Pager.HasPrev)
    {
        <a href="@Model.Links.ListingHref(Model.Sort, Model.Desc, Model.Pager.DisplayPage - 1)">&larr; @L["Prev"]</a>
    }
    <span>@L["Page {0} of {1}", Model.Pager.DisplayPage, Math.Max(1, Model.Pager.TotalPages)]</span>
    @if (Model.Pager.HasNext)
    {
        <a href="@Model.Links.ListingHref(Model.Sort, Model.Desc, Model.Pager.DisplayPage + 1)">@L["Next"] &rarr;</a>
    }
</nav>
```

- [ ] **Step 3: Localise `_ItemRow.cshtml`**

Apply these exact replacements (leave all `@item`, `@m`, `@authors`, `@s` data bindings untouched):

- `"(untitled)"` fallback: `@(m?.Title ?? "(untitled)")` → `@(m?.Title ?? L["(untitled)"])`
- Download link text `>Download<` → `>@L["Download"]<`
- Read button (read state): `<button type="submit" class="read-btn" title="Mark as unread">&#10003; Read</button>` → `<button type="submit" class="read-btn" title="@L["Mark as unread"]">&#10003; @L["Read"]</button>`
- Unread button: `<button type="submit" class="read-btn" title="Mark as read">Mark read</button>` → `<button type="submit" class="read-btn" title="@L["Mark as read"]">@L["Mark read"]</button>`

- [ ] **Step 4: Localise `_ConvertAction.cshtml`**

Replace the visible label/title text (keep `EPUB &#10003;` — format name + glyph — literal; keep `href`/`data-*` untouched):

- `title="Already converted — downloads right away"` → `title="@L["Already converted — downloads right away"]"`
- `<a href="@baseHref" data-warm data-poll>Converting&#8230;</a>` → `<a href="@baseHref" data-warm data-poll>@L["Converting…"]</a>`
- `<a href="@baseHref" data-warm>Convert (retry)</a>` → `<a href="@baseHref" data-warm>@L["Convert (retry)"]</a>`
- `<a href="@baseHref" data-warm>Convert</a>` → `<a href="@baseHref" data-warm>@L["Convert"]</a>`
- `title="Regenerate"` → `title="@L["Regenerate"]"`

- [ ] **Step 5: Build, test, and spot-check German**

Run: `dotnet test`
Expected: PASS.
Run the app with a `de` cookie (or `Accept-Language: de`) and open a library listing: pager reads **Zurück / Seite 1 von N / Weiter**, rows show **Herunterladen / Gelesen markieren / Konvertieren**, and tapping Convert shows **Konvertiere…**.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_Layout.cshtml src/Inkshelf/Pages/Shared/_Pager.cshtml src/Inkshelf/Pages/Shared/_ItemRow.cshtml src/Inkshelf/Pages/Shared/_ConvertAction.cshtml
git commit -m "feat: localise shared layout and row/pager/convert partials"
```

---

### Task 8: Localise the remaining pages

**Files:**
- Modify: `src/Inkshelf/Pages/Index.cshtml`
- Modify: `src/Inkshelf/Pages/Settings.cshtml`
- Modify: `src/Inkshelf/Pages/Library.cshtml`
- Modify: `src/Inkshelf/Pages/Item.cshtml`
- Modify: `src/Inkshelf/Pages/Converted.cshtml`

All keys already exist in `de.json`. In every case wrap only the human-readable text; leave `href`, `class`, `id`, `alt=""`, image paths, ABS data bindings (`@lib.Name`, `@Model.Q`, `@Model.FilterName`, `@f.Name`, counts), and the `Inkshelf`/version brand strings untouched.

- [ ] **Step 1: `Index.cshtml`**

- `title="Libraries"` (home-link) → `title="@L["Libraries"]"`
- heading ` Libraries</h1>` → ` @L["Libraries"]</h1>`
- `<button type="submit">Log out</button>` → `<button type="submit">@L["Log out"]</button>`
- gear link `title="Settings"` and `alt="Settings"` → `title="@L["Settings"]"` and `alt="@L["Settings"]"`
- `<a href="/converted">Converted on this device &#8594;</a>` → `<a href="/converted">@L["Converted on this device"] &#8594;</a>`
- Leave `<small>(@lib.MediaType)</small>` and `Inkshelf v@(Model.Version)` unchanged (data / brand).

- [ ] **Step 2: `Settings.cshtml`** (the picker was added in Task 6)

- home-link `title="Libraries"` → `title="@L["Libraries"]"`
- `<a href="/?all=1">Libraries</a>` → `<a href="/?all=1">@L["Libraries"]</a>`
- ` Settings</h1>` (after the crumb-sep) → ` @L["Settings"]</h1>`
- `<p class="settings-note">These settings apply to <strong>this device / browser</strong> only.</p>` → `<p class="settings-note">@L["These settings apply to this device / browser only."]</p>` (drop the inner `<strong>`; the sentence is one key)
- `<p class="settings-note">Detected screen: @Model.DetectedScreen</p>` → `<p class="settings-note">@L["Detected screen: {0}", Model.DetectedScreen]</p>`
- retina label text → `@L["Retina pages (full-resolution; crisper but heavier — may strain low-memory readers)"]`
- grayscale label text → `@L["Grayscale pages (smaller files on e-ink)"]`
- `<button type="submit">Save</button>` → `<button type="submit">@L["Save"]</button>`

- [ ] **Step 3: `Library.cshtml`**

- home-link `title="Libraries"` → `title="@L["Libraries"]"`; `<a href="/?all=1">Libraries</a>` → `<a href="/?all=1">@L["Libraries"]</a>`
- fav button `title="@(Model.IsFavorite ? "Unset favorite" : "Set favorite")"` → `title="@(Model.IsFavorite ? L["Unset favorite"] : L["Set favorite"])"`
- search input `placeholder="Search…"` → `placeholder="@L["Search…"]"`; search `<button type="submit">Search</button>` → `<button type="submit">@L["Search"]</button>`
- gear `title="Settings"`/`alt="Settings"` → `@L["Settings"]`
- results line `<p>Results for "<strong>@Model.Q</strong>" · <a href="/library/@Model.Id">clear</a></p>` → `<p>@L["Results for"] "<strong>@Model.Q</strong>" · <a href="/library/@Model.Id">@L["clear"]</a></p>`
- tabs and headings — wrap the words, keep counts/glyphs/ids:
  - `Books (@books.Count)` → `@L["Books"] (@books.Count)` (both the `<a>` and the `<span>` form)
  - `Series (@series.Count)` → `@L["Series"] (@series.Count)`
  - `Authors (@authors.Count)` → `@L["Authors"] (@authors.Count)`
  - `<p>No matches.</p>` → `<p>@L["No matches."]</p>`
  - `<h2 id="books">Books <a class="totop" href="#top">↑</a></h2>` → `<h2 id="books">@L["Books"] <a class="totop" href="#top">↑</a></h2>` (same for `Series`, `Authors`)
- filtered line `<p>Filtered by <strong>@Model.FilterDisplay</strong> · <a href="/library/@Model.Id">clear</a></p>` → localise the prefix, the type token, and `clear`, keeping the ABS name:
  ```cshtml
  <p>@L["Filtered by"] <strong>@L[Model.FilterType!]@(Model.FilterName is null ? "" : $": {Model.FilterName}")</strong> · <a href="/library/@Model.Id">@L["clear"]</a></p>
  ```
  (`@L[Model.FilterType!]` uses the English token — `Author`/`Series`/`Genre`/`Tag`/`Narrator`/`Filter` — as the key; an unmapped custom group falls back to itself.)
- sort bar: `Sort:` → `@L["Sort:"]`; then wrap each label *before* its `Arrow(...)` glyph:
  - `>Title@(Inkshelf.Pages.SortLinks.Arrow(...))` → `>@L["Title"]@(Inkshelf.Pages.SortLinks.Arrow(...))`
  - `Author…` → `@L["Author"]…`, `Added…` → `@L["Added"]…`, `Sequence…` → `@L["Sequence"]…`
- `<p>No items.</p>` → `<p>@L["No items."]</p>`

- [ ] **Step 4: `Item.cshtml`**

- home-link/breadcrumb `Libraries` (title + link text) → `@L["Libraries"]`
- breadcrumb fallback `@(m?.Title ?? "Item")` → `@(m?.Title ?? L["Item"])`
- gear `title`/`alt` `Settings` → `@L["Settings"]`
- `<h2>@(m?.Title ?? "(untitled)")</h2>` → `<h2>@(m?.Title ?? L["(untitled)"])</h2>`
- section labels: `<p>Authors:` → `<p>@L["Authors:"]`; `<p>Series:` → `<p>@L["Series:"]`; `<p>Narrators:` → `<p>@L["Narrators:"]`; `<p>Genres:` → `<p>@L["Genres:"]`; `<p>Tags:` → `<p>@L["Tags:"]` (keep the loops/links after each label unchanged)
- read form buttons — identical to `_ItemRow` (Task 7 Step 3): `title="@L["Mark as unread"]">&#10003; @L["Read"]` and `title="@L["Mark as read"]">@L["Mark read"]`
- `<h2>Files</h2>` → `<h2>@L["Files"]</h2>`
- `<p>No downloadable files.</p>` → `<p>@L["No downloadable files."]</p>`
- file-row `<a href="@f.DownloadHref">Download</a>` → `<a href="@f.DownloadHref">@L["Download"]</a>`

- [ ] **Step 5: `Converted.cshtml`**

- home-link/breadcrumb `Libraries` → `@L["Libraries"]`
- ` Converted</h1>` (after crumb-sep) → ` @L["Converted"]</h1>`
- gear `title`/`alt` `Settings` → `@L["Settings"]`
- `<p>Converted &amp; cached on this device.</p>` → `<p>@L["Converted & cached on this device."]</p>`
- `<p>Couldn't load details from Audiobookshelf. Try again.</p>` → `<p>@L["Couldn't load details from Audiobookshelf. Try again."]</p>`
- `<p>Nothing converted for this device yet.</p>` → `<p>@L["Nothing converted for this device yet."]</p>`

- [ ] **Step 6: Build, format, and full manual sweep in German**

Run: `dotnet format Inkshelf.sln` then `dotnet test`
Expected: PASS, no format changes outstanding.
With a `de` cookie, walk Libraries → a library listing → search → an item detail → Settings → Converted. Expected: all chrome is German; ABS titles/authors/descriptions and the `Inkshelf` brand/version remain unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Pages/Index.cshtml src/Inkshelf/Pages/Settings.cshtml src/Inkshelf/Pages/Library.cshtml src/Inkshelf/Pages/Item.cshtml src/Inkshelf/Pages/Converted.cshtml
git commit -m "feat: localise index, library, item, settings, and converted pages"
```

---

## Known non-localised strings (deliberate)

- **ABS content** — titles, subtitles, author/series/narrator/genre/tag names, descriptions, media type — stay in ABS's language.
- **Brand** — `Inkshelf`, the `<title>`, the version string.
- **`EPUB ✓` / `EPUB ↓`** — format name plus a universal glyph.
- **`LibraryName` fallback `"Library"`** (`Library.cshtml.cs:40,52`) — shown only when a library id can't be resolved (a degenerate/error state); left English rather than injecting the localizer into the page model for one edge case.

## Self-Review

- **Spec coverage:** catalog format + `$name` (Task 2, 4) · location/`LOCALES_PATH`/shipping (Task 4) · resilient startup load (Task 2) · Localizer + `@inject` + format args (Task 3, 4) · resolution order incl. Accept-Language (Task 3) · `DeviceSettings.Lang` + backward compat (Task 1) · Settings picker with `en` value (Task 6) · `<html lang>` + convert-status JS labels (Task 7 Step 1) · full string inventory incl. code-side errors/filter labels (Tasks 5, 7, 8). All spec sections map to a task.
- **Placeholder scan:** none — every step carries exact code or exact find/replace text.
- **Type consistency:** `Localizer` ctor `(LocalizationCatalog, IHttpContextAccessor)` and indexers match across Tasks 3–8; `LocalizationCatalog.Get/Has/DisplayName/Languages` signatures match Tasks 2, 4, 6; `DeviceSettings(bool,bool,string)` matches Tasks 1, 5.
