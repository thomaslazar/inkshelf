namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

// Orchestrates CBZ/CBR → fixed-layout EPUB conversion: read pages in order,
// process each image, write the EPUB.
public class EpubConverter
{
    // maxWidth/maxHeight cap page image pixels (0 = no cap); dpr converts those
    // pixels to the CSS viewport (viewport = px / dpr). dpr ≤ 0 falls back to 1.
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, int maxWidth, int maxHeight, double dpr, CancellationToken ct)
    {
        if (dpr <= 0) dpr = 1;
        var pages = new List<EpubWriter.Page>();
        var idx = 0;
        await foreach (var raw in ComicArchiveReader.ReadAsync(archive, ct))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(raw.Key).ToLowerInvariant();
            var img = await PageImageProcessor.ProcessAsync(raw.Bytes, ext, maxWidth, maxHeight, ct);
            idx++;
            pages.Add(new EpubWriter.Page($"page-{idx:D4}{img.Extension}", img.Bytes, img.Width, img.Height));
        }
        EpubWriter.Write(outPath, meta, pages, dpr);
    }
}
