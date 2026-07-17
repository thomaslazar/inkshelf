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
