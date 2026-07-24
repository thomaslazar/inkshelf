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
