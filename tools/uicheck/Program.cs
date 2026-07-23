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

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });

var failures = new List<string>();

// Portrait e-reader-ish viewport to expose wrapping/overflow.
async Task Check(string label, string? settingsCookie, string path,
                 string[] mustContain, string[] mustNotContain)
{
    var ctx = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 758, Height = 1024 } });
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

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS — screenshots in {outDir}, all assertions held.");
    return 0;
}
Console.WriteLine($"FAIL — {failures.Count} issue(s):");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;
