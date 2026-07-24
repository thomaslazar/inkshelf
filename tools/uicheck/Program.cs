using Microsoft.Playwright;

// Headless-browser UI pass for Inkshelf. Captures full-page screenshots and
// asserts key strings on the pages that render without an ABS login, so gross
// breakage, layout overflow, and untranslated/English-leak strings are caught
// before the manual e-reader verification pass.
//
// NOT a substitute for the e-reader: desktop Chromium does not reproduce the old
// e-ink engine (no object-fit, no flex gap), so device testing stays mandatory
// for engine-specific rendering. Authenticated pages (Library/Item/Converted)
// need a real ABS session and are not covered here.
//
// To extend as features land: add Check(...) calls below (a new page, a new
// language cookie, new expected/forbidden strings). Run with tools/uicheck/run.sh.

var baseUrl = Environment.GetEnvironmentVariable("BASE_URL") ?? "http://127.0.0.1:5099";
var outDir = Environment.GetEnvironmentVariable("OUT_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "shots");
Directory.CreateDirectory(outDir);

// Viewport, overridable via env so the pass can be run at a specific e-reader's
// reported CSS size (e.g. VIEWPORT_W=769 VIEWPORT_H=953). Defaults to a portrait
// e-reader-ish size that exposes wrapping/overflow.
var vpW = int.TryParse(Environment.GetEnvironmentVariable("VIEWPORT_W"), out var w) ? w : 758;
var vpH = int.TryParse(Environment.GetEnvironmentVariable("VIEWPORT_H"), out var h) ? h : 1024;

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });

var failures = new List<string>();

// Portrait e-reader-ish viewport to expose wrapping/overflow.
async Task Check(string label, string? settingsCookie, string path,
                 string[] mustContain, string[] mustNotContain)
{
    var ctx = await browser.NewContextAsync(new() { ViewportSize = new() { Width = vpW, Height = vpH } });
    if (settingsCookie is not null)
        await ctx.AddCookiesAsync([ new() { Name = "inkshelf_settings", Value = settingsCookie, Url = baseUrl } ]);
    var page = await ctx.NewPageAsync();
    var resp = await page.GotoAsync(baseUrl + path, new() { WaitUntil = WaitUntilState.NetworkIdle });
    await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, label + ".png"), FullPage = true });
    var body = await page.InnerTextAsync("body");

    var status = (int?)resp?.Status ?? 0;
    if (status is not (200 or 302)) failures.Add($"{label}: HTTP {status} for {path}");
    foreach (var s in mustContain)
        if (!body.Contains(s, StringComparison.Ordinal))
            failures.Add($"{label}: expected to see \"{s}\" but did not");
    foreach (var s in mustNotContain)
        if (body.Contains(s, StringComparison.Ordinal))
            failures.Add($"{label}: English leak — saw \"{s}\" (should be translated)");
    Console.WriteLine($"[{label}] HTTP {status}");
}

// The settings cookie is "<retina><grayscale><lang>", e.g. "10de" = retina on,
// grayscale off, German. No cookie = English (the source-string keys).

await Check("login-de", "10de", "/login",
    mustContain: ["Anmelden", "Passwort", "Benutzername"],
    mustNotContain: ["Log in", "Password", "Username"]);

await Check("settings-de", "10de", "/settings",
    mustContain: ["Einstellungen", "Sprache", "Speichern", "Bibliotheken", "Deutsch"],
    mustNotContain: ["Save", "Language"]);

await Check("login-en", null, "/login",
    mustContain: ["Log in", "Password", "Username"],
    mustNotContain: []);

await Check("settings-en", null, "/settings",
    mustContain: ["Settings", "Language", "Save", "Libraries", "English"],
    mustNotContain: []);

// --- Authenticated pages (opt-in; run.sh brings up + seeds the local ABS) ---
// These are where the real chrome lives — listings, item detail (Kategorien /
// Schlagwörter / Erzähler), converted — plus a live Convert-button click that
// exercises the JS label path.
if (Environment.GetEnvironmentVariable("UICHECK_AUTHED") == "1")
{
    var ctx = await browser.NewContextAsync(new() { ViewportSize = new() { Width = vpW, Height = vpH } });
    await ctx.AddCookiesAsync([ new() { Name = "inkshelf_settings", Value = "10de", Url = baseUrl } ]);
    var page = await ctx.NewPageAsync();

    async Task Shot(string label) =>
        await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, label + ".png"), FullPage = true });
    void Expect(string label, string body, params string[] needles)
    {
        foreach (var s in needles)
            if (!body.Contains(s, StringComparison.Ordinal))
                failures.Add($"{label}: expected to see \"{s}\"");
    }

    try
    {
        // Log in through Inkshelf (German context) with the seeded root/root.
        await page.GotoAsync(baseUrl + "/login");
        await page.FillAsync("input[name=Username]", "root");
        await page.FillAsync("input[name=Password]", "root");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForSelectorAsync("text=Bibliotheken", new() { Timeout = 15000 });

        await Shot("index-de");
        Expect("index-de", await page.InnerTextAsync("body"), "Bibliotheken", "Abmelden");

        // Library listing (open the first library).
        await page.ClickAsync("a[href^='/library/']");
        await page.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 15000 });
        await Shot("library-de");
        Expect("library-de", await page.InnerTextAsync("body"), "Sortierung:", "Titel", "Herunterladen");
        var libUrl = page.Url;

        // Item detail of the enriched epub — genres/tags/narrators labels.
        await page.FillAsync("input[name=q]", "The Silent Sea");
        await page.PressAsync("input[name=q]", "Enter");
        // Click the row's item link (not the results heading, which echoes the query).
        await page.ClickAsync("a[href^='/item/']:has-text('The Silent Sea')");
        await page.WaitForSelectorAsync("text=Dateien", new() { Timeout = 15000 });
        await Shot("item-de");
        // genres → Kategorien, tags → Schlagwörter, narrators → Erzähler.
        Expect("item-de", await page.InnerTextAsync("body"),
            "Kategorien:", "Schlagwörter:", "Erzähler:", "Dateien", "Herunterladen");

        // Converted view (empty state).
        await page.GotoAsync(baseUrl + "/converted");
        await Shot("converted-de");
        Expect("converted-de", await page.InnerTextAsync("body"), "Konvertiert");

        // Live Convert-button click: label must flip to German, never a raw entity.
        await page.GotoAsync(libUrl);
        await page.FillAsync("input[name=q]", "Neon Blade");
        await page.PressAsync("input[name=q]", "Enter");
        await page.WaitForSelectorAsync("a[data-warm]", new() { Timeout = 15000 });
        var convert = page.Locator("a[data-warm]").First;
        await convert.ClickAsync();
        await page.WaitForTimeoutAsync(1500); // let the JS swap the label
        var label = await convert.InnerTextAsync();
        await Shot("convert-clicked-de");
        if (label.Contains("&#x", StringComparison.Ordinal))
            failures.Add($"convert-clicked: HTML entity leaked into JS label: \"{label}\"");
        if (!label.Contains("Konvert", StringComparison.Ordinal) && !label.Contains("EPUB", StringComparison.Ordinal))
            failures.Add($"convert-clicked: unexpected label \"{label}\"");

        // Failure reasons: each seeded broken comic must land on the German reason
        // page (poll-JS auto-nav on failure) with the right explanation.
        //   Big Comic      → over the run's tiny ceiling → TooLarge
        //   Corrupt Archive→ not a real archive          → BadArchive
        //   Broken Page    → valid zip, page won't decode → ConvertError
        async Task ConvertShouldExplain(string search, string label, params string[] needles)
        {
            await page.GotoAsync(libUrl);
            await page.FillAsync("input[name=q]", search);
            await page.PressAsync("input[name=q]", "Enter");
            await page.ClickAsync($"a[href^='/item/']:has-text('{search}')");
            await page.WaitForSelectorAsync("a[data-warm]", new() { Timeout = 15000 });
            await page.Locator("a[data-warm]").First.ClickAsync();
            await page.WaitForURLAsync("**/convert/**/why**", new() { Timeout = 20000 });
            await Shot(label);
            Expect(label, await page.InnerTextAsync("body"), needles);
        }

        await ConvertShouldExplain("Big Comic", "convert-failed-de",
            "Konvertierung fehlgeschlagen", "überschreitet", "Erneut versuchen", "Zurück");
        await ConvertShouldExplain("Corrupt Archive", "convert-badarchive-de",
            "Konvertierung fehlgeschlagen", "konnte nicht gelesen werden", "Erneut versuchen", "Zurück");
        await ConvertShouldExplain("Broken Page", "convert-converterror-de",
            "Konvertierung fehlgeschlagen", "unerwartet fehlgeschlagen", "Erneut versuchen", "Zurück");

        // Failed-row layout on the LISTING — the fixed-width .actions column is
        // where the narrow-screen overflow of the "warum?" link shows (the item
        // detail page's file-row is full-width and doesn't reproduce it). The three
        // broken comics above are now Failed and sit on page 1 of the default sort.
        await page.GotoAsync(libUrl);
        await page.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 15000 });
        await Shot("failed-row-de");
        Expect("failed-row-de", await page.InnerTextAsync("body"), "warum?");

        Console.WriteLine("[authed] index / library / item / converted / convert-click / convert-failed (toolarge/badarchive/converterror) / failed-row captured");
    }
    catch (Exception ex)
    {
        failures.Add($"authed flow error: {ex.Message}");
        try { await Shot("authed-error"); } catch { }
    }
    await ctx.CloseAsync();
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS — screenshots in {outDir}, all assertions held.");
    return 0;
}
Console.WriteLine($"FAIL — {failures.Count} issue(s):");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;
