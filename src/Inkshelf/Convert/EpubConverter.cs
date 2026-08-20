using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

// Orchestrates CBZ/CBR → fixed-layout EPUB conversion: read pages in order,
// process each image, stream it into the EPUB (one page held at a time).
public class EpubConverter
{
    // target.MaxW/MaxH cap page image pixels (0 = no cap); target.Dpr converts those
    // pixels to the CSS viewport (viewport = px / dpr). dpr ≤ 0 falls back to 1.
    // target.Scale then shrinks that viewport (percent, 100 = no shrink).
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target,
        CancellationToken ct, (byte[] Bytes, string Ext)? cover = null)
    {
        var dpr = target.Dpr <= 0 ? 1 : target.Dpr;
        var scale = target.Scale is >= 10 and <= 100 ? target.Scale : 100;
        var processedCover = await ProcessCoverAsync(cover, target, ct);
        await EpubWriter.WriteAsync(outPath, meta,
            ProcessPagesAsync(archive, target, ct), dpr / (scale / 100.0), ct, processedCover);
    }

    // Process the raw ABS cover through the same pipeline as pages (cap, grayscale,
    // WebP→JPEG). A cover that fails to decode is dropped (null) so the writer falls
    // back to flagging the first page — a bad cover must never fail the conversion.
    private static async Task<EpubWriter.Cover?> ProcessCoverAsync(
        (byte[] Bytes, string Ext)? cover, RenderTarget target, CancellationToken ct)
    {
        if (cover is not { } c) return null;
        try
        {
            // SpreadMode.Fit: a cover is one image by definition — never split or
            // rotate it. A rare landscape cover gets white bars, which beats a
            // library grid stretching it.
            // A cover is not a page: never split, rotate or letterbox it.
            var img = (await PageImageProcessor.ProcessAsync(c.Bytes, c.Ext, target.MaxW, target.MaxH,
                target.Grayscale, SpreadMode.Fit, padToBox: false, ct))[0];
            return new EpubWriter.Cover(img.Bytes, img.Extension);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Lazily decode → downscale → transcode each page and yield it, so the writer
    // pulls one page at a time and only one page's bytes are ever live.
    // One archive entry can yield TWO pages (a split spread), so the page index
    // advances per emitted image, not per archive entry.
    //
    // EVERY page is letterboxed onto ONE box, fixed by the first page. That is
    // load-bearing: a book with two different page sizes renders wrong on a real
    // e-reader — the wider pages lose their right edge. Verified on device.
    private static async IAsyncEnumerable<EpubWriter.Page> ProcessPagesAsync(
        Stream archive, RenderTarget target, [EnumeratorCancellation] CancellationToken ct)
    {
        var idx = 0;
        var (boxW, boxH) = (0, 0);
        await foreach (var raw in ComicArchiveReader.ReadAsync(archive, ct))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(raw.Key).ToLowerInvariant();
            // Identify is header-only (no decode), so fixing the box off the first page
            // costs nothing and lets page 1 itself be letterboxed into it.
            if (boxH == 0) (boxW, boxH) = PageBox(Image.Identify(raw.Bytes), target);
            foreach (var img in await PageImageProcessor.ProcessAsync(raw.Bytes, ext,
                boxW, boxH, target.Grayscale, target.Spread, padToBox: true, ct))
            {
                idx++;
                yield return new EpubWriter.Page($"page-{idx:D4}{img.Extension}", img.Bytes, img.Width, img.Height);
            }
        }
    }

    // The book's one page size: the first page scaled to fit the device cap, so pages
    // keep the shape of the comic page. A landscape first page is a spread, which is no
    // basis for a page size, so that falls back to the cap; with no cap at all (no
    // screen probe) the first page's own size has to do.
    private static (int W, int H) PageBox(ImageInfo first, RenderTarget target)
    {
        var (w, h) = (first.Width, first.Height);
        if (target.MaxW <= 0 || target.MaxH <= 0) return (w, h);
        if (w > h) return (target.MaxW, target.MaxH);
        var scale = Math.Min((double)target.MaxW / w, (double)target.MaxH / h);
        return scale >= 1 ? (w, h)
            : (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }
}
