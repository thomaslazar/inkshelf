using System.Globalization;

namespace Inkshelf.Convert;

public static class ScreenTarget
{
    // Upper bound on a page dimension fed into the converter + cache key, so a
    // client-set "scr" cookie can't mint absurd sizes (disk exhaustion / OOM).
    public const int MaxDimension = 4096;

    // Upper bound on the client-supplied device-pixel-ratio. Bounded because it
    // multiplies the page dimensions under retina — an unbounded dpr would blow
    // past MaxDimension's intent.
    public const double MaxDpr = 4.0;

    // Parse the "scr" cookie ("<cssW>x<cssH>x<dpr>", written by the layout script)
    // into a RenderTarget. The Tolino reader lays fixed-layout pages out in CSS
    // pixels, so the viewport must be the CSS size to fill the screen.
    //
    //   retina = false → cap = CSS size,        Dpr = 1   (image == page == CSS; softer, light)
    //   retina = true  → cap = CSS size × dpr,  Dpr = dpr (physical image in a CSS page; crisp, heavy)
    //
    // dpr is bounded to MaxDpr, and dimensions are clamped to MaxDimension AFTER
    // the dpr multiply (a raw cssW × dpr must not exceed the cap). Returns
    // (0, 0, 1, grayscale) when absent/unparseable → no downscaling.
    public static RenderTarget FromCookie(string? scr, bool retina = false, bool grayscale = false)
    {
        if (!string.IsNullOrEmpty(scr))
        {
            var p = scr.Split('x');
            if (p.Length >= 3
                && int.TryParse(p[0], out var cw) && int.TryParse(p[1], out var ch)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dpr)
                && cw > 0 && ch > 0 && dpr > 0)
            {
                if (retina)
                {
                    dpr = Math.Min(dpr, MaxDpr);
                    var w = Math.Min((int)Math.Round(cw * dpr), MaxDimension);
                    var h = Math.Min((int)Math.Round(ch * dpr), MaxDimension);
                    return new RenderTarget(w, h, dpr, grayscale);
                }
                return new RenderTarget(Math.Min(cw, MaxDimension), Math.Min(ch, MaxDimension), 1, grayscale);
            }
            // Legacy 2-part physical cookie, transient until the script rewrites it.
            if (p.Length == 2 && int.TryParse(p[0], out var w2) && int.TryParse(p[1], out var h2) && w2 > 0 && h2 > 0)
                return new RenderTarget(Math.Min(w2, MaxDimension), Math.Min(h2, MaxDimension), 1, grayscale);
        }
        return new RenderTarget(0, 0, 1, grayscale);
    }
}
