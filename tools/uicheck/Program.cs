using Microsoft.Playwright;
using System.Text.RegularExpressions;

// Headless-browser UI pass for Inkshelf. Captures full-page screenshots and
// asserts key strings on the pages that render without an ABS login, so gross
// breakage, layout overflow, and untranslated/English-leak strings are caught
// before the manual e-reader verification pass.
//
// NOT a substitute for the e-reader: desktop Chromium does not reproduce the old
// e-ink engine (no object-fit, no flex gap), so device testing stays mandatory
// for engine-specific rendering. Authenticated pages (Library/Item/Converted)
// ARE covered - see the UICHECK_AUTHED block below, which run.sh enables against
// the seeded local ABS.
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
            failures.Add($"{label}: English leak - saw \"{s}\" (should be translated)");
    Console.WriteLine($"[{label}] HTTP {status}");
}

// The settings cookie is keyed: "retina=1&gray=0&lang=de&fav=" = retina on,
// grayscale off, German, no favorite. No cookie = English (the source-string
// keys). Playwright sets the cookie value raw, which is what the server reads
// after its own unescaping, so no %-escaping is needed here.
const string De = "retina=1&gray=0&lang=de&fav=";

// run.sh sets OIDC_ENABLED=true, so the SSO button must be on both login pages.
// The seeded ABS has no provider configured, so it is never clicked here.
await Check("login-de", De, "/login",
    mustContain: ["Anmelden", "Passwort", "Benutzername", "Mit SSO anmelden"],
    mustNotContain: ["Log in", "Password", "Username"]);

await Check("settings-de", De, "/settings",
    mustContain: ["Einstellungen", "Sprache", "Speichern", "Bibliotheken", "Deutsch",
                  "Doppelseiten", "linke Hälfte zuerst", "rechte Hälfte zuerst",
                  "Um 90° nach rechts drehen", "Um 90° nach links drehen", "Seitenskalierung", "Prozent.",
                  "Bildschirmauflösung überschreiben", "Pixelverhältnis", "Automatisch", "als Lesezeichen speichern",
                  "Kleine Seiten vergrößern"],
    mustNotContain: ["Save", "Language", "Split into two pages", "Page scale", "Enlarge small pages"]);

await Check("login-en", null, "/login",
    mustContain: ["Log in", "Password", "Username", "Log in with SSO"],
    mustNotContain: []);

await Check("settings-en", null, "/settings",
    mustContain: ["Settings", "Language", "Save", "Libraries", "English",
                  "Two-page spreads", "left half first", "right half first",
                  "Rotate 90° to the right", "Rotate 90° to the left", "Page scale", "Percent.",
                  "Override screen resolution", "Pixel ratio", "Automatic", "Bookmark this page",
                  "Enlarge small pages"],
    mustNotContain: []);

// The capability probe. Its measured rows are what an engine with no CSS.supports()
// gets asked instead, so a headless run is the only place their agreement with
// CSS.supports() can be checked at all - on a device, one of the two is missing.
await Check("diag", null, "/diag.html",
    mustContain: ["measured box-sizing", "measured display: flex",
                  "measured float ignored inside flex", "measured calc()",
                  "measured agrees with CSS.supports()"],
    mustNotContain: []);

// --- Authenticated pages (opt-in; run.sh brings up + seeds the local ABS) ---
// These are where the real chrome lives - listings, item detail (Kategorien /
// Schlagwörter / Erzähler), converted - plus a live Convert-button click that
// exercises the JS label path.
if (Environment.GetEnvironmentVariable("UICHECK_AUTHED") == "1")
{
    var ctx = await browser.NewContextAsync(new() { ViewportSize = new() { Width = vpW, Height = vpH } });
    await ctx.AddCookiesAsync([ new() { Name = "inkshelf_settings", Value = De, Url = baseUrl } ]);
    var page = await ctx.NewPageAsync();

    // forPage defaults to the shared authed page; the no-JS check below passes
    // its own second-context page so it can reuse this same helper.
    async Task Shot(string label, IPage? forPage = null) =>
        await (forPage ?? page).ScreenshotAsync(new() { Path = Path.Combine(outDir, label + ".png"), FullPage = true });
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
        Expect("index-de", await page.InnerTextAsync("body"), "Bibliotheken", "Abmelden", "Benutzer: root");

        // Library listing (open the first library).
        await page.ClickAsync("a[href^='/library/']");
        await page.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 15000 });
        await Shot("library-de");
        Expect("library-de", await page.InnerTextAsync("body"), "Sortierung:", "Titel", "Herunterladen");
        // A link with no ticket is a download an e-reader's manager cannot finish -
        // the listing mints its own, so assert here too, not just on the item page.
        if (!Regex.IsMatch(await page.ContentAsync(), @"href=""/download/[^""]*(\?|&amp;)t=[A-Za-z0-9_-]{22}"""))
            failures.Add("library-de: a listing download link carries no ticket");
        var libUrl = page.Url;

        // Search results - books + series + author sections, each its own layout
        // (item rows vs .taplist), and previously the only authed page with no
        // screenshot at all.
        await page.FillAsync("input[name=q]", "Dresden");
        await page.PressAsync("input[name=q]", "Enter");
        await page.WaitForSelectorAsync("nav.tabs", new() { Timeout = 15000 });
        await Shot("search-de");
        Expect("search-de", await page.InnerTextAsync("body"), "Ergebnisse für", "Bücher", "Serien");

        // Item detail of the enriched epub - genres/tags/narrators labels.
        await page.GotoAsync(libUrl);
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

        // Item page of a COMIC - the only place ↻ Regenerate is offered now that
        // listing rows have dropped it, so nothing else screenshots that button.
        await page.GotoAsync(libUrl);
        await page.FillAsync("input[name=q]", "Neon Blade");
        await page.PressAsync("input[name=q]", "Enter");
        await page.ClickAsync("a[href^='/item/']:has-text('Neon Blade')");
        await Shot("item-comic-de");
        Expect("item-comic-de", await page.InnerTextAsync("body"), "Neu erzeugen", "Herunterladen");

        // A link with no ticket is a download an e-reader's manager cannot finish.
        var comicHtml = await page.ContentAsync();
        if (!Regex.IsMatch(comicHtml, @"href=""/download/[^""]*(\?|&amp;)t=[A-Za-z0-9_-]{22}"""))
            failures.Add("item-comic-de: a raw download link carries no ticket");
        if (!Regex.IsMatch(comicHtml, @"href=""/convert/[^""]*(\?|&amp;)t=[A-Za-z0-9_-]{22}"""))
            failures.Add("item-comic-de: the convert link carries no ticket");

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
        // The JS never mints its own "done" state: it may still be converting
        // ("Konvert...") or must land on exactly "EPUB" - never "EPUB ↓", which
        // means "already downloaded" and must only ever come from the server.
        if (!label.Contains("Konvert", StringComparison.Ordinal) && label != "EPUB")
            failures.Add($"convert-clicked: unexpected label \"{label}\"");

        // Live Read-button click: label must flip without a reload, never leak an
        // HTML entity, and never get stuck on the "Markiere…" working label - that
        // would mean the XHR success/failure branch in the layout script never ran.
        await page.GotoAsync(libUrl);
        await page.FillAsync("input[name=q]", "The Silent Sea");
        await page.PressAsync("input[name=q]", "Enter");
        await page.ClickAsync("a[href^='/item/']:has-text('The Silent Sea')");
        await page.WaitForSelectorAsync("form.read-form button.read-btn", new() { Timeout = 15000 });
        var readBtn = page.Locator("form.read-form button.read-btn").First;
        var readLabelBefore = await readBtn.InnerTextAsync();
        var urlBeforeRead = page.Url;
        await readBtn.ClickAsync();
        await page.WaitForTimeoutAsync(1500); // let the JS swap the label
        var readLabelAfter = await readBtn.InnerTextAsync();
        await Shot("read-clicked-de");
        if (readLabelAfter == readLabelBefore)
            failures.Add($"read-clicked: label did not change after click (\"{readLabelBefore}\")");
        if (readLabelAfter.Contains("Markiere", StringComparison.Ordinal))
            failures.Add($"read-clicked: label stuck on the working state \"{readLabelAfter}\"");
        if (readLabelAfter.Contains("&#", StringComparison.Ordinal))
            failures.Add($"read-clicked: HTML entity leaked into JS label: \"{readLabelAfter}\"");
        if (page.Url != urlBeforeRead)
            failures.Add($"read-clicked: page navigated from {urlBeforeRead} to {page.Url}");

        // Live Read-button click, XHR FAILS: there is deliberately no error UI, so
        // the un-flipped label IS the failure signal. That only works if the
        // script's revert branch actually restores the pre-click label - if it
        // does not, the button is stuck on "Markiere..." (the working label)
        // forever, which is the dead-button outcome the design forbids. Scoped to
        // this page/route pair and unrouted right after so it cannot catch a
        // later check's request.
        Func<string, bool> matchReadXhr = url => url.Contains("/read/") && url.Contains("xhr=1");
        Func<IRoute, Task> failReadXhr = route => route.FulfillAsync(new() { Status = 500 });
        await page.RouteAsync(matchReadXhr, failReadXhr);
        var readLabelBeforeFail = await readBtn.InnerTextAsync();
        var urlBeforeReadFail = page.Url;
        await readBtn.ClickAsync();
        await page.WaitForTimeoutAsync(1500); // let the JS revert branch run
        var readLabelAfterFail = await readBtn.InnerTextAsync();
        await page.UnrouteAsync(matchReadXhr, failReadXhr);
        await Shot("read-clicked-fail-de");
        if (readLabelAfterFail != readLabelBeforeFail)
            failures.Add($"read-clicked-fail: label did not revert (\"{readLabelBeforeFail}\" -> \"{readLabelAfterFail}\")");
        if (readLabelAfterFail.Contains("Markiere", StringComparison.Ordinal))
            failures.Add($"read-clicked-fail: label stuck on the working state \"{readLabelAfterFail}\"");
        if (page.Url != urlBeforeReadFail)
            failures.Add($"read-clicked-fail: page navigated from {urlBeforeReadFail} to {page.Url}");

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

        // Failed-row layout on the LISTING - the fixed-width .actions column is
        // where the narrow-screen overflow of the "warum?" link shows (the item
        // detail page's file-row is full-width and doesn't reproduce it). The three
        // broken comics above are now Failed; they were seeded first, so sort by
        // added ASCENDING to keep them on page 1 (the default is newest-first).
        await page.GotoAsync(libUrl + "?sort=addedAt");
        await page.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 15000 });
        await Shot("failed-row-de");
        Expect("failed-row-de", await page.InnerTextAsync("body"), "warum?");

        // Converted view again, now that a conversion has actually landed - this is
        // where the sortbar exists (it's hidden on the empty state). Waiting on the
        // selector doubles as "the conversion finished".
        await page.GotoAsync(baseUrl + "/converted");
        await page.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 30000 });
        await Shot("converted-sorted-de");
        Expect("converted-sorted-de", await page.InnerTextAsync("body"),
            "Sortierung:", "Konvertiert", "Serien", "Titel", "Autor");

        // The retired checkmark must be gone and no download arrow present yet:
        // nothing has been downloaded in this run.
        var convertedHtml = await page.ContentAsync();
        if (convertedHtml.Contains("EPUB &#10003;", StringComparison.Ordinal))
            failures.Add("converted-sorted-de: the retired EPUB checkmark is still rendered");
        if (convertedHtml.Contains("EPUB &#8595;", StringComparison.Ordinal))
            failures.Add("converted-sorted-de: a download arrow is rendered before anything was downloaded");

        Console.WriteLine("[authed] index / library / item / converted / convert-click / read-click / convert-failed (toolarge/badarchive/converterror) / failed-row / converted-sorted captured");
    }
    catch (Exception ex)
    {
        failures.Add($"authed flow error: {ex.Message}");
        try { await Shot("authed-error"); } catch { }
    }
    await ctx.CloseAsync();

    // No-JavaScript round trip. The read-form's whole reason for existing is
    // that it degrades to a plain POST plus a redirect-with-fragment when JS
    // is off - that is the justification for allowing any JS in this project
    // at all - but the maintainer's e-reader has no way to turn JavaScript
    // off, so this path has never been exercised end to end on real hardware.
    // Playwright can disable JS where a device can't, which is why this check
    // exists. It needs its own BrowserContext (JavaScriptEnabled can't be
    // flipped on an existing one) and, since cookies don't cross contexts,
    // its own login - hence logging in a second time here.
    var noJsCtx = await browser.NewContextAsync(new()
    {
        ViewportSize = new() { Width = vpW, Height = vpH },
        JavaScriptEnabled = false,
    });
    await noJsCtx.AddCookiesAsync([ new() { Name = "inkshelf_settings", Value = De, Url = baseUrl } ]);
    var noJsPage = await noJsCtx.NewPageAsync();
    try
    {
        await noJsPage.GotoAsync(baseUrl + "/login");
        await noJsPage.FillAsync("input[name=Username]", "root");
        await noJsPage.FillAsync("input[name=Password]", "root");
        await noJsPage.ClickAsync("button[type=submit]");
        await noJsPage.WaitForSelectorAsync("text=Bibliotheken", new() { Timeout = 15000 });

        // Act on the LISTING, not the item page: the fragment only means
        // anything on a page with more than one row, and this is also the
        // first coverage of the listing's own copy of the read-form (the JS
        // click test above runs on the item page). "Field Manual" is a
        // different seeded item from "The Silent Sea" - the only item that
        // earlier test touches - so the two checks cannot race each other's
        // read state.
        await noJsPage.ClickAsync("a[href^='/library/']");
        await noJsPage.WaitForSelectorAsync("nav.sortbar", new() { Timeout = 15000 });
        await noJsPage.FillAsync("input[name=q]", "Field Manual");
        await noJsPage.PressAsync("input[name=q]", "Enter");
        await noJsPage.WaitForSelectorAsync("form.read-form button.read-btn", new() { Timeout = 15000 });

        var noJsRow = noJsPage.Locator("div.item:has-text('Field Manual')").First;
        var rowId = await noJsRow.GetAttributeAsync("id"); // "item-<abs item id>"
        var noJsBtn = noJsRow.Locator("form.read-form button.read-btn").First;
        var noJsLabelBefore = await noJsBtn.InnerTextAsync();

        // With JS off this is a real native form POST and a real navigation,
        // not an XHR - Playwright's click auto-waits for it, so just wait for
        // the resulting page to settle rather than a fixed timeout.
        await noJsBtn.ClickAsync();
        await noJsPage.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Shot("read-nojs-de", noJsPage);
        if (!noJsPage.Url.EndsWith("#" + rowId, StringComparison.Ordinal))
            failures.Add($"read-nojs: expected the URL to end with #{rowId}, got {noJsPage.Url}");
        if (await noJsPage.Locator($"[id='{rowId}']").CountAsync() == 0)
            failures.Add($"read-nojs: no element with id \"{rowId}\" on the landed page");
        var noJsLabelAfter = await noJsPage.Locator($"[id='{rowId}'] form.read-form button.read-btn").First.InnerTextAsync();
        if (noJsLabelAfter == noJsLabelBefore)
            failures.Add($"read-nojs: label did not change after the no-JS POST (\"{noJsLabelBefore}\")");

        Console.WriteLine("[authed] no-JS read-form round trip captured");
    }
    catch (Exception ex)
    {
        failures.Add($"no-JS read flow error: {ex.Message}");
        try { await Shot("read-nojs-error", noJsPage); } catch { }
    }
    await noJsCtx.CloseAsync();
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS - screenshots in {outDir}, all assertions held.");
    return 0;
}
Console.WriteLine($"FAIL - {failures.Count} issue(s):");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;
