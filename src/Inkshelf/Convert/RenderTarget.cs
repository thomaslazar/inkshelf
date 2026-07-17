namespace Inkshelf.Convert;

// The resolved per-device render knobs for one conversion: the page-image pixel
// cap (MaxW/MaxH, 0 = no cap), the pixel ratio used to derive each page's CSS
// viewport (viewport = image px / Dpr), and whether pages are desaturated.
public readonly record struct RenderTarget(int MaxW, int MaxH, double Dpr, bool Grayscale);
