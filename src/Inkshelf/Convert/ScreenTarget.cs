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

    // devicePixelRatio reaches us as a float widened to a double, so a screen at
    // 1.325 reports 1.3250000476837158. That value is not just noise in a
    // calculation: it is stored in the settings cookie, shown in the settings
    // readout, and written into the cache filename. Four decimals is finer than any
    // real screen and keeps all three short and stable.
    public const int DprDecimals = 4;

    public static double RoundDpr(double dpr) => Math.Round(dpr, DprDecimals);

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
    //
    // An override (hand-entered geometry) wins over the cookie entirely, including
    // when the cookie is missing.
    public static RenderTarget FromCookie(string? scr, bool retina = false, bool grayscale = false,
        SpreadMode spread = SpreadMode.Fit, int scale = 100, ScreenOverride? over = null)
    {
        // FIRST, before the cookie is even looked at. Being merely "preferred over a
        // bad value" would not help: the no-probe case returns at the bottom of this
        // method, so an override consulted later would never be reached when the
        // cookie is absent — which is one of the reasons the override exists.
        if (over is { W: > 0, H: > 0, Dpr: > 0 } o)
        {
            var od = RoundDpr(Math.Min(o.Dpr, MaxDpr));
            var ow = Math.Min(o.W, MaxDimension);
            var oh = Math.Min(o.H, MaxDimension);
            // retina means the same thing here as on the probe path below: the entered
            // numbers are PHYSICAL pixels, so retina off converts at the CSS size
            // (numbers ÷ ratio, dpr 1) — identical page layout, a quarter of the pixels
            // at ratio 2. Ignoring retina here would leave it an inert control, which
            // was the only reason the UI ever had to disable it.
            return retina
                ? new RenderTarget(ow, oh, od, grayscale) { Spread = spread, Scale = scale }
                : new RenderTarget(Math.Max(1, (int)Math.Round(ow / od)),
                                   Math.Max(1, (int)Math.Round(oh / od)), 1, grayscale)
                { Spread = spread, Scale = scale };
        }
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
                    dpr = RoundDpr(Math.Min(dpr, MaxDpr));
                    var w = Math.Min((int)Math.Round(cw * dpr), MaxDimension);
                    var h = Math.Min((int)Math.Round(ch * dpr), MaxDimension);
                    return new RenderTarget(w, h, dpr, grayscale) { Spread = spread, Scale = scale };
                }
                return new RenderTarget(Math.Min(cw, MaxDimension), Math.Min(ch, MaxDimension), 1, grayscale) { Spread = spread, Scale = scale };
            }
            // Legacy 2-part physical cookie, transient until the script rewrites it.
            if (p.Length == 2 && int.TryParse(p[0], out var w2) && int.TryParse(p[1], out var h2) && w2 > 0 && h2 > 0)
                return new RenderTarget(Math.Min(w2, MaxDimension), Math.Min(h2, MaxDimension), 1, grayscale) { Spread = spread, Scale = scale };
        }
        return new RenderTarget(0, 0, 1, grayscale) { Spread = spread, Scale = scale };
    }
}
