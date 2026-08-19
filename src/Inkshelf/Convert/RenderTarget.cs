namespace Inkshelf.Convert;

// What to do with a landscape page image — a two-page spread scanned as one
// image. Reading direction is NOT considered anywhere: Split always emits the
// left half first, which is right for western comics and wrong for manga, whose
// readers want Rotate instead.
//
//   Fit    — whole spread on one page, letterboxed by us onto a screen-shaped
//            canvas. Complete but small.
//   Split  — cut into two portrait pages.
//   Rotate — turned 90° so the spread fills the screen sideways.
//
// Fit is 0 so a RenderTarget built without an explicit mode gets the
// least-surprising behaviour.
public enum SpreadMode { Fit, Split, Rotate }

// The resolved per-device render knobs for one conversion: the page-image pixel
// cap (MaxW/MaxH, 0 = no cap), the pixel ratio used to derive each page's CSS
// viewport (viewport = image px / Dpr), and whether pages are desaturated.
//
// Spread is an init property, not a fifth positional parameter, so the existing
// positional construction sites keep compiling.
public readonly record struct RenderTarget(int MaxW, int MaxH, double Dpr, bool Grayscale)
{
    public SpreadMode Spread { get; init; }

    // Page scale in PERCENT (100 = no shrink). Shrinks the declared CSS viewport, not
    // the image, so pages keep their pixels and stay sharp — the page simply lays out
    // in a slightly smaller box. This is the manual fix for a reader that cuts a strip
    // off the page: see EpubWriter. Init property so positional construction still works.
    public int Scale { get; init; } = 100;
}
