namespace Inkshelf.Convert;

// What to do with a landscape page image — a two-page spread scanned as one image.
//
//   Fit              — whole spread on one page, letterboxed. Complete but small.
//   SplitLeftFirst   — cut in two; left half is the earlier page (western comics).
//   SplitRightFirst  — cut in two; right half is the earlier page (manga).
//   RotateLeft       — turned 90° anticlockwise, so it fills the screen sideways.
//   RotateRight      — turned 90° clockwise.
//
// Split has both directions because a CBZ is just images 1..N and carries nothing
// that says which half comes first; rotate has both because which way the reader
// wants to tilt the device is theirs to choose, not ours to guess.
//
// Fit is 0 so a RenderTarget built without an explicit mode behaves sanely.
public enum SpreadMode { Fit, SplitLeftFirst, SplitRightFirst, RotateLeft, RotateRight }

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

// A hand-entered screen geometry, replacing the "scr" probe. W/H are physical
// image pixels (what a vendor spec sheet gives); Dpr is how many image pixels the
// reader draws per CSS layout pixel, so viewport = px × scale ÷ Dpr.
//
// Only ever constructed from already-sanitised values (DeviceSettings clamps them
// on the way out of the cookie), but ScreenTarget clamps again — the numbers cross
// a trust boundary and clamping twice is cheaper than trusting once.
public readonly record struct ScreenOverride(int W, int H, double Dpr);
