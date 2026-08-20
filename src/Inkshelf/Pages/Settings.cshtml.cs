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
        DetectedScreen = FormatScreen(Request.Cookies["scr"]);
        var langs = new List<(string, string)> { ("en", "English") };
        foreach (var code in _catalog.Languages.OrderBy(c => c))
            langs.Add((code, _catalog.DisplayName(code)));
        AvailableLanguages = langs;

        var probe = ParseScreen(Request.Cookies["scr"]);
        PrefillW = Settings.OverrideW > 0 ? Settings.OverrideW : probe?.W ?? 0;
        PrefillH = Settings.OverrideH > 0 ? Settings.OverrideH : probe?.H ?? 0;
        var dpr = Settings.OverrideDpr > 0 ? Settings.OverrideDpr : probe?.Dpr ?? 0;
        PrefillDpr = dpr > 0 ? dpr.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
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
}
