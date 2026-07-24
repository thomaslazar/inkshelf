using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Inkshelf.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Inkshelf.Pages;

// Plain-HTML explanation for a failed conversion. Reached by the poll-JS auto-nav
// on failure, or the "why?" link on a Failed row (JS and no-JS alike). If the
// item isn't currently Failed (expired TTL, re-queued, converted), redirect back
// so the page never shows stale state.
public class ConvertWhyModel : PageModel
{
    private readonly ConvertService _convert;
    private readonly AbsOptions _options;
    public ConvertWhyModel(ConvertService convert, AbsOptions options)
    { _convert = convert; _options = options; }

    [FromRoute] public string Id { get; set; } = "";
    [FromQuery] public string? File { get; set; }
    [FromQuery(Name = "return")] public string? Return { get; set; }

    public string Title { get; private set; } = "";
    public ConvertFailReason Reason { get; private set; }
    public long? ArchiveBytes { get; private set; }
    public long LimitBytes { get; private set; }
    public string BackUrl { get; private set; } = "/";
    public string RetryUrl { get; private set; } = "/";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Id)) return NotFound();
        var ds = DeviceSettings.Read(Request);
        var target = ScreenTarget.FromCookie(Request.Cookies["scr"], ds.Retina, ds.Grayscale);

        var f = await _convert.FailureAsync(Id, target, ct, File);
        BackUrl = ConvertEndpoints.LocalReturn(Return);
        if (f is null) return Redirect(BackUrl); // not (any longer) Failed → nothing to explain

        Title = f.Value.Title;
        Reason = f.Value.Reason;
        ArchiveBytes = f.Value.ArchiveBytes;
        LimitBytes = _options.MaxArchiveBytes;

        var fileQ = string.IsNullOrEmpty(File) ? "" : $"file={Uri.EscapeDataString(File)}&";
        RetryUrl = $"/convert/{Id}?{fileQ}return={Uri.EscapeDataString(BackUrl)}";
        return Page();
    }

    // Binary units, one decimal (e.g. "1.3 GiB", "293.0 KiB"). Static so the view
    // formats both the actual size and the limit identically.
    public static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double n = bytes; var u = 0;
        while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return u == 0 ? $"{bytes} {units[u]}" : $"{n:0.0} {units[u]}";
    }
}
