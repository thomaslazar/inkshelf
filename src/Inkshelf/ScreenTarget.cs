using System.Globalization;

namespace Inkshelf;

public static class ScreenTarget
{
    // Parse the "scr" cookie (the layout script reports "<cssW>x<cssH>x<dpr>")
    // into a page-image cap and the device pixel ratio.
    //
    // The Tolino reader lays fixed-layout pages out in CSS pixels, so the page
    // viewport must be the CSS size (cssW×cssH) to fill the screen — but the
    // image itself is kept at physical resolution (css×dpr) so text stays crisp
    // (a "retina" page). MaxW/MaxH are therefore the physical cap; Dpr converts
    // an image's pixel size back to the CSS viewport size (viewport = px / dpr).
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
                return ((int)Math.Round(cw * dpr), (int)Math.Round(ch * dpr), dpr);
            // Legacy 2-part physical cookie, transient until the script rewrites it.
            if (p.Length == 2 && int.TryParse(p[0], out var w2) && int.TryParse(p[1], out var h2) && w2 > 0 && h2 > 0)
                return (w2, h2, 1);
        }
        return (0, 0, 1);
    }
}
