using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Inkshelf.Convert;

// Decodes one comic page image, downscaling anything larger than the cap (aspect
// preserved) and transcoding WebP → JPEG (many e-readers can't decode WebP).
// In-bounds non-WebP images pass through untouched. Returns the final bytes,
// extension, and pixel size (the viewport is derived from the size downstream).
//
// A landscape image is treated as a two-page spread and handled per SpreadMode. A
// split returns TWO images, in reading order, so the result is always a list.
//
// padToBox letterboxes the result onto exactly maxWidth × maxHeight, which is how
// every page in a book ends up the same size — see EpubConverter's page box.
//
// EVERY mode emits a portrait-shaped page — Fit pads the spread onto the full
// cap box rather than leaving a wide page behind. That is the whole point, not
// tidiness: a wide fixed-layout viewport is what the e-reader mishandles (it
// letterboxes vertically AND clips ~10% off the right edge). Padding reduces the
// spread to the same shape as the ordinary pages that already render correctly.
public static class PageImageProcessor
{
    public sealed record ProcessedImage(byte[] Bytes, string Extension, int Width, int Height);

    public static async Task<ProcessedImage[]> ProcessAsync(byte[] bytes, string extension,
        int maxWidth, int maxHeight, bool grayscale, SpreadMode spread = SpreadMode.Fit,
        bool padToBox = false, CancellationToken ct = default)
    {
        var info = Image.Identify(bytes);
        // ponytail: any landscape page counts as a spread. Tighten to
        // w > h * 1.2 if a single-page landscape illustration gets split.
        var wide = info.Width > info.Height;

        if (wide && spread is SpreadMode.SplitLeftFirst or SpreadMode.SplitRightFirst)
        {
            using var img = Image.Load(bytes);
            var half = img.Width / 2;
            // Clone-and-crop off ONE decode, and crop before the downscale so each
            // half is resized from full resolution rather than from a shrunk spread.
            var left = img.Clone(x => x.Crop(new Rectangle(0, 0, half, img.Height)));
            var right = img.Clone(x => x.Crop(new Rectangle(half, 0, img.Width - half, img.Height)));
            var (first, second) = spread == SpreadMode.SplitRightFirst ? (right, left) : (left, right);
            return
            [
                await FinishAsync(first, maxWidth, maxHeight, grayscale, padToBox, ct),
                await FinishAsync(second, maxWidth, maxHeight, grayscale, padToBox, ct),
            ];
        }

        var rotate = wide && spread is SpreadMode.RotateLeft or SpreadMode.RotateRight;
        var (w, h) = rotate ? (info.Height, info.Width) : (info.Width, info.Height);
        var box = maxWidth > 0 && maxHeight > 0;
        var oversized = box && (w > maxWidth || h > maxHeight);
        // An image already exactly the box needs no padding — the common case for an
        // ordinary page, and it keeps the pass-through path below alive.
        var needsPad = padToBox && box && (w != maxWidth || h != maxHeight);
        if (oversized || rotate || needsPad || extension == ".webp" || grayscale)
        {
            var img = Image.Load(bytes);
            if (rotate) img.Mutate(x => x.Rotate(
                spread == SpreadMode.RotateLeft ? RotateMode.Rotate270 : RotateMode.Rotate90));
            return [await FinishAsync(img, maxWidth, maxHeight, grayscale, padToBox, ct)];
        }
        return [new ProcessedImage(bytes, extension, w, h)];
    }

    // Downscale to fit the cap (aspect preserved), optionally letterbox onto the
    // full cap box, desaturate, encode as JPEG. Takes ownership of img.
    private static async Task<ProcessedImage> FinishAsync(Image img,
        int maxWidth, int maxHeight, bool grayscale, bool pad, CancellationToken ct)
    {
        using (img)
        {
            var cap = maxWidth > 0 && maxHeight > 0;
            if (cap && (img.Width > maxWidth || img.Height > maxHeight))
            {
                var scale = Math.Min((double)maxWidth / img.Width, (double)maxHeight / img.Height);
                img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * scale)),
                                         Math.Max(1, (int)Math.Round(img.Height * scale))));
            }
            // Pad, NOT Resize(…, ResizeMode.Pad): the latter would scale a small
            // spread UP to fill the box. This centres at whatever size it is.
            if (pad && cap) img.Mutate(x => x.Pad(maxWidth, maxHeight, Color.White));
            if (grayscale) img.Mutate(x => x.Grayscale());
            using var outMs = new MemoryStream();
            await img.SaveAsJpegAsync(outMs, ct);
            return new ProcessedImage(outMs.ToArray(), ".jpg", img.Width, img.Height);
        }
    }
}
