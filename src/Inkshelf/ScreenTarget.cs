using System.Globalization;

namespace Inkshelf;

public static class ScreenTarget
{
    // TODO(configurable): whether converted pages are "retina". Hard-coded to
    // non-retina for now because full-resolution pages can crash the tolino
    // epos's reader (memory) on large comics. Make this a setting later — ideally
    // per-device via the cookie, alongside the planned user-defined resolution
    // override — so higher-memory devices / the app / webreader can opt into
    // crisp retina pages.
    //   retina  = true  → image at physical px (css × dpr), viewport = css  (crisp, heavy)
    //   retina  = false → image at css px,                  viewport = css  (softer, ~3.5× lighter)
    public const bool Retina = false;

    // Parse the "scr" cookie (the layout script reports "<cssW>x<cssH>x<dpr>")
    // into a page-image cap (MaxW/MaxH) and the pixel ratio the converter uses to
    // derive each page's CSS viewport (viewport = image px / Dpr). The Tolino
    // reader lays fixed-layout pages out in CSS pixels, so the viewport must be
    // the CSS size to fill the screen.
    //
    // Non-retina (current): cap = CSS size, Dpr = 1 → image == page == CSS size.
    // Retina: cap = physical (css × dpr), Dpr = dpr → physical image in a CSS page.
    //
    // Returns (0,0,1) when absent/unparseable → no downscaling, viewport = image.
    public static (int MaxW, int MaxH, double Dpr) FromCookie(string? scr)
    {
        if (!string.IsNullOrEmpty(scr))
        {
            var p = scr.Split('x');
            if (p.Length >= 3
                && int.TryParse(p[0], out var cw) && int.TryParse(p[1], out var ch)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dpr)
                && cw > 0 && ch > 0 && dpr > 0)
                return Retina
                    ? ((int)Math.Round(cw * dpr), (int)Math.Round(ch * dpr), dpr)
                    : (cw, ch, 1);
            // Legacy 2-part physical cookie, transient until the script rewrites it.
            if (p.Length == 2 && int.TryParse(p[0], out var w2) && int.TryParse(p[1], out var h2) && w2 > 0 && h2 > 0)
                return (w2, h2, 1);
        }
        return (0, 0, 1);
    }
}
