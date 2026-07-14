using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Inkshelf.Convert;

// Decodes one comic page image, downscaling anything larger than the cap (aspect
// preserved) and transcoding WebP → JPEG (many e-readers can't decode WebP).
// In-bounds non-WebP images pass through untouched. Returns the final bytes,
// extension, and pixel size (the viewport is derived from the size downstream).
public static class PageImageProcessor
{
    public sealed record ProcessedImage(byte[] Bytes, string Extension, int Width, int Height);

    public static async Task<ProcessedImage> ProcessAsync(byte[] bytes, string extension,
        int maxWidth, int maxHeight, CancellationToken ct)
    {
        var cap = maxWidth > 0 && maxHeight > 0;
        var info = Image.Identify(bytes);
        var (w, h) = (info.Width, info.Height);
        var oversized = cap && (w > maxWidth || h > maxHeight);
        if (oversized || extension == ".webp")
        {
            using var img = Image.Load(bytes);
            if (oversized)
            {
                var scale = Math.Min((double)maxWidth / img.Width, (double)maxHeight / img.Height);
                img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * scale)),
                                         Math.Max(1, (int)Math.Round(img.Height * scale))));
            }
            using var outMs = new MemoryStream();
            await img.SaveAsJpegAsync(outMs, ct);
            return new ProcessedImage(outMs.ToArray(), ".jpg", img.Width, img.Height);
        }
        return new ProcessedImage(bytes, extension, w, h);
    }
}
