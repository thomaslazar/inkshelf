using System.Runtime.CompilerServices;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

// Orchestrates CBZ/CBR → fixed-layout EPUB conversion: read pages in order,
// process each image, stream it into the EPUB (one page held at a time).
public class EpubConverter
{
    // maxWidth/maxHeight cap page image pixels (0 = no cap); dpr converts those
    // pixels to the CSS viewport (viewport = px / dpr). dpr ≤ 0 falls back to 1.
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, int maxWidth, int maxHeight, double dpr, CancellationToken ct)
    {
        if (dpr <= 0) dpr = 1;
        await EpubWriter.WriteAsync(outPath, meta, ProcessPagesAsync(archive, maxWidth, maxHeight, ct), dpr, ct);
    }

    // Lazily decode → downscale → transcode each page and yield it, so the writer
    // pulls one page at a time and only one page's bytes are ever live.
    private static async IAsyncEnumerable<EpubWriter.Page> ProcessPagesAsync(
        Stream archive, int maxWidth, int maxHeight, [EnumeratorCancellation] CancellationToken ct)
    {
        var idx = 0;
        await foreach (var raw in ComicArchiveReader.ReadAsync(archive, ct))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(raw.Key).ToLowerInvariant();
            var img = await PageImageProcessor.ProcessAsync(raw.Bytes, ext, maxWidth, maxHeight, grayscale: false, ct);
            idx++;
            yield return new EpubWriter.Page($"page-{idx:D4}{img.Extension}", img.Bytes, img.Width, img.Height);
        }
    }
}
