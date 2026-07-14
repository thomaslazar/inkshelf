using SharpCompress.Archives;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

public class EpubConverter
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    // maxWidth/maxHeight cap page image pixels (0 = no cap); dpr converts those
    // pixels to the CSS viewport (viewport = px / dpr). dpr ≤ 0 falls back to 1.
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, int maxWidth, int maxHeight, double dpr, CancellationToken ct)
    {
        if (dpr <= 0) dpr = 1;
        var cap = maxWidth > 0 && maxHeight > 0;
        // Collect pages in archive order. Downscale anything larger than the cap
        // and transcode WebP → JPEG (many e-readers can't decode WebP); other
        // in-bounds images pass through untouched. Track each page's final pixel
        // size — the fixed-layout viewport is set from it.
        var pages = new List<EpubWriter.Page>();
        using (var arc = ArchiveFactory.OpenArchive(archive))
        {
            var entries = arc.Entries
                .Where(e => !e.IsDirectory && ImageExts.Contains(Path.GetExtension(e.Key ?? "").ToLowerInvariant()))
                .OrderBy(e => e.Key, StringComparer.Ordinal);
            var idx = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                using var es = e.OpenEntryStream();
                using var mem = new MemoryStream();
                await es.CopyToAsync(mem, ct);
                var bytes = mem.ToArray();
                var ext = Path.GetExtension(e.Key ?? "").ToLowerInvariant();
                var info = Image.Identify(bytes);
                var (w, h) = (info.Width, info.Height);
                var oversized = cap && (w > maxWidth || h > maxHeight);
                if (oversized || ext == ".webp")
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
                    bytes = outMs.ToArray(); ext = ".jpg";
                    w = img.Width; h = img.Height;
                }
                idx++;
                pages.Add(new EpubWriter.Page($"page-{idx:D4}{ext}", bytes, w, h));
            }
        }

        EpubWriter.Write(outPath, meta, pages, dpr);
    }
}
