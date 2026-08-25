using Inkshelf.Auth;
using Inkshelf.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

public class SettingsModel : PageModel
{
    private readonly LocalizationCatalog _catalog;
    private readonly Localizer _loc;
    public SettingsModel(LocalizationCatalog catalog, Localizer loc)
    { _catalog = catalog; _loc = loc; }

    public DeviceSettings Settings { get; private set; } = DeviceSettings.Default;

    // The raw device probe, shown as a read-only readout so "retina" has context.
    public string? DetectedScreen { get; private set; }

    // "" (follow Accept-Language) first, then English (empty catalog = keys), then
    // each loaded language. The empty code has to be offered: without it a device
    // that never chose a language has nothing selected, so the browser shows the
    // first option and saving any other setting pins that language.
    public IReadOnlyList<(string Code, string Name)> AvailableLanguages { get; private set; } = [];
    public string CurrentLang => Settings.Lang;

    // Set by the PRG redirect when the override was saved ticked but unusable, so the
    // page can say why nothing changed instead of silently re-showing the probe.
    public bool RangeWarning { get; private set; }

    // Same idea for the page scale, which is a free number rather than a menu now.
    public bool ScaleWarning { get; private set; }

    // What the override fields show: the stored override when there is one, else
    // whatever the probe reported, else blank. 0 / "" render as an empty field.
    public int PrefillW { get; private set; }
    public int PrefillH { get; private set; }
    public string PrefillDpr { get; private set; } = "";

    public void OnGet()
    {
        // A bookmarked URL carrying settings IS the restore mechanism for devices
        // that lose their cookies on a browser restart: apply it and store it, so
        // the rest of the session behaves as if the values had been typed in.
        // Only this page honours query settings - see the spec.
        // Localizer.CurrentLang() reads the REQUEST cookie; this only writes the
        // response cookie, so this first render still uses the previous locale - the
        // next request picks up the restored language. Not worth re-architecting the
        // localizer over.
        var restored = DeviceSettings.FromQuery(Request.Query);
        Settings = restored is { } r ? DeviceSettings.Set(Response, r) : DeviceSettings.Read(Request);
        // A restored override that is stored yet unusable (e.g. a hand-typed ovrw of
        // 99999) must warn the same way a POST-time rejection does - otherwise the
        // page ticks the override and silently shows the probe's numbers instead,
        // exactly what SettingsEndpoints' `unusable` comment says must never happen.
        RangeWarning = Request.Query.ContainsKey("range")
            || (restored is not null && Settings.OverrideScreen && Settings.ActiveOverride is null);
        ScaleWarning = Request.Query.ContainsKey("scalerange");
        var langs = new List<(string, string)> { ("", _loc["Automatic"]), ("en", "English") };
        foreach (var code in _catalog.Languages.OrderBy(c => c))
            langs.Add((code, _catalog.DisplayName(code)));
        AvailableLanguages = langs;

        // Parsed ONCE: the display string and the prefill must describe the same
        // screen, or the page contradicts itself (one number for "detected", a
        // different one prefilled into the override fields).
        var probe = ParseScreen(Request.Cookies["scr"]);

        // Both the readout and the override fields report the screen in PHYSICAL
        // pixels - the number a person goes looking for, and the number a vendor spec
        // sheet gives. The cookie stores CSS pixels plus the ratio, so multiply.
        // Printing the cookie raw made the page look like it gave two sizes for one
        // screen (769 × 953 in the readout, 1442 × 1787 in the fields).
        //
        // Deliberately NOT retina-aware: this is a statement about the hardware and
        // the fields have to agree with it. So ticking the override converts at the
        // screen's real resolution whatever retina says - consistent with retina being
        // disabled while an override is active.
        int devW = 0, devH = 0; double devDpr = 0;
        if (probe is { } cur)
        {
            devW = (int)Math.Round(cur.CssW * cur.Dpr);
            devH = (int)Math.Round(cur.CssH * cur.Dpr);
            devDpr = cur.Dpr;
        }

        DetectedScreen = probe is { } p
            ? $"{devW} × {devH} @ dpr {p.Dpr.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : FormatScreenFallback(Request.Cookies["scr"]);

        PrefillW = Settings.OverrideW > 0 ? Settings.OverrideW : devW;
        PrefillH = Settings.OverrideH > 0 ? Settings.OverrideH : devH;
        var dpr = Settings.OverrideDpr > 0 ? Settings.OverrideDpr : devDpr;
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
    // itself reports them - NOT multiplied together. Callers decide what to do with
    // the two numbers. null when absent/unparseable.
    //
    // The legacy 2-part cookie ("769x953", written before the script reported dpr) is
    // accepted as dpr 1, matching what ScreenTarget.FromCookie does with it. Rejecting
    // it here instead would blank the prefill on a device whose cookie is mid-upgrade,
    // while conversion happily used the same numbers.
    private static (int CssW, int CssH, double Dpr)? ParseScreen(string? scr)
    {
        if (string.IsNullOrEmpty(scr)) return null;
        var p = scr.Split('x');
        if (p.Length < 2
            || !int.TryParse(p[0], out var w) || !int.TryParse(p[1], out var h)
            || w <= 0 || h <= 0) return null;
        if (p.Length == 2) return (w, h, 1);
        return double.TryParse(p[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? (w, h, Inkshelf.Convert.ScreenTarget.RoundDpr(d)) : null;
    }
}
