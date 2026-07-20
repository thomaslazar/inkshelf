using System.Runtime.CompilerServices;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

// Orchestrates CBZ/CBR → fixed-layout EPUB conversion: read pages in order,
// process each image, stream it into the EPUB (one page held at a time).
public class EpubConverter
{
    // target.MaxW/MaxH cap page image pixels (0 = no cap); target.Dpr converts those
    // pixels to the CSS viewport (viewport = px / dpr). dpr ≤ 0 falls back to 1.
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target,
        CancellationToken ct, (byte[] Bytes, string Ext)? cover = null)
    {
        var dpr = target.Dpr <= 0 ? 1 : target.Dpr;
        var processedCover = await ProcessCoverAsync(cover, target, ct);
        await EpubWriter.WriteAsync(outPath, meta,
            ProcessPagesAsync(archive, target.MaxW, target.MaxH, target.Grayscale, ct), dpr, ct, processedCover);
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
            var img = await PageImageProcessor.ProcessAsync(c.Bytes, c.Ext, target.MaxW, target.MaxH, target.Grayscale, ct);
            return new EpubWriter.Cover(img.Bytes, img.Extension);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Lazily decode → downscale → transcode each page and yield it, so the writer
    // pulls one page at a time and only one page's bytes are ever live.
    private static async IAsyncEnumerable<EpubWriter.Page> ProcessPagesAsync(
        Stream archive, int maxWidth, int maxHeight, bool grayscale, [EnumeratorCancellation] CancellationToken ct)
    {
        var idx = 0;
        await foreach (var raw in ComicArchiveReader.ReadAsync(archive, ct))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(raw.Key).ToLowerInvariant();
            var img = await PageImageProcessor.ProcessAsync(raw.Bytes, ext, maxWidth, maxHeight, grayscale, ct);
            idx++;
            yield return new EpubWriter.Page($"page-{idx:D4}{img.Extension}", img.Bytes, img.Width, img.Height);
        }
    }
}
