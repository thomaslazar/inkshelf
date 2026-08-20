using Inkshelf.Auth;
using Inkshelf.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class SettingsModel : PageModel
{
    private readonly LocalizationCatalog _catalog;
    public SettingsModel(LocalizationCatalog catalog) => _catalog = catalog;

    public DeviceSettings Settings { get; private set; } = DeviceSettings.Default;

    // The raw device probe, shown as a read-only readout so "retina" has context.
    public string? DetectedScreen { get; private set; }

    // English first (empty catalog = keys), then each loaded language.
    public IReadOnlyList<(string Code, string Name)> AvailableLanguages { get; private set; } = [];
    public string CurrentLang => Settings.Lang;

    // What the override fields show: the stored override when there is one, else
    // whatever the probe reported, else blank. 0 / "" render as an empty field.
    public int PrefillW { get; private set; }
    public int PrefillH { get; private set; }
    public string PrefillDpr { get; private set; } = "";

    public void OnGet()
    {
        Settings = DeviceSettings.Read(Request);
        var langs = new List<(string, string)> { ("en", "English") };
        foreach (var code in _catalog.Languages.OrderBy(c => c))
            langs.Add((code, _catalog.DisplayName(code)));
        AvailableLanguages = langs;

        // Parsed ONCE: the display string and the prefill must describe the same
        // screen, or the page contradicts itself (one number for "detected", a
        // different one prefilled into the override fields).
        var probe = ParseScreen(Request.Cookies["scr"]);
        DetectedScreen = probe is { } p
            ? $"{p.CssW} × {p.CssH} @ dpr {p.Dpr.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : FormatScreenFallback(Request.Cookies["scr"]);

        // The prefill is what this device is CURRENTLY getting, so accepting it as
        // an override is a no-op for image weight: retina on converts at physical
        // pixels (css × dpr), retina off converts at CSS pixels with dpr 1 — mirrors
        // ScreenTarget.FromCookie exactly.
        int curW = 0, curH = 0; double curDpr = 0;
        if (probe is { } cur)
        {
            (curW, curH, curDpr) = Settings.Retina
                ? ((int)Math.Round(cur.CssW * cur.Dpr), (int)Math.Round(cur.CssH * cur.Dpr), cur.Dpr)
                : (cur.CssW, cur.CssH, 1);
        }

        PrefillW = Settings.OverrideW > 0 ? Settings.OverrideW : curW;
        PrefillH = Settings.OverrideH > 0 ? Settings.OverrideH : curH;
        var dpr = Settings.OverrideDpr > 0 ? Settings.OverrideDpr : curDpr;
        PrefillDpr = dpr > 0 ? dpr.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    // "769x953" (no dpr reported) → "769 × 953". null when absent/unparseable.
    // Only reached when ParseScreen couldn't make sense of the cookie as CSS+dpr.
    private static string? FormatScreenFallback(string? scr)
    {
        if (string.IsNullOrEmpty(scr)) return null;
        var p = scr.Split('x');
        return p.Length == 2 ? $"{p[0]} × {p[1]}" : null;
    }

    // "769x953x1.875" → (769, 953, 1.875) in CSS pixels × dpr, as the "scr" cookie
    // itself reports them — NOT multiplied together. Callers decide what to do with
    // the two numbers. null when absent/unparseable.
    private static (int CssW, int CssH, double Dpr)? ParseScreen(string? scr)
    {
        if (string.IsNullOrEmpty(scr)) return null;
        var p = scr.Split('x');
        if (p.Length < 3
            || !int.TryParse(p[0], out var w) || !int.TryParse(p[1], out var h)
            || w <= 0 || h <= 0
            || !double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) || d <= 0) return null;
        return (w, h, d);
    }
}
