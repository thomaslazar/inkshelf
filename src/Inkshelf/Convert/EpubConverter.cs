using System.Runtime.CompilerServices;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

// Orchestrates CBZ/CBR → fixed-layout EPUB conversion: read pages in order,
// process each image, stream it into the EPUB (one page held at a time).
public class EpubConverter
{
    // target.MaxW/MaxH cap page image pixels (0 = no cap); target.Dpr converts those
    // pixels to the CSS viewport (viewport = px / dpr). dpr ≤ 0 falls back to 1.
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, RenderTarget target, CancellationToken ct)
    {
        var dpr = target.Dpr <= 0 ? 1 : target.Dpr;
        await EpubWriter.WriteAsync(outPath, meta,
            ProcessPagesAsync(archive, target.MaxW, target.MaxH, target.Grayscale, ct), dpr, ct);
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
