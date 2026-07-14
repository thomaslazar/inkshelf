using System.IO.Compression;
using System.Text;
using SharpCompress.Archives;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence, string? Identifier = null);

public class EpubConverter
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    // maxWidth/maxHeight cap page image pixels (0 = no cap); dpr converts those
    // pixels to the CSS viewport size (viewport = px / dpr). Pages are kept at
    // physical resolution but declared at their CSS size so the reader shows them
    // full-screen and crisp. dpr ≤ 0 falls back to 1 (viewport = image size).
    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, int maxWidth, int maxHeight, double dpr, CancellationToken ct)
    {
        if (dpr <= 0) dpr = 1;
        var cap = maxWidth > 0 && maxHeight > 0;
        // Collect pages in archive order. Downscale anything larger than the cap
        // and transcode WebP → JPEG (many e-readers can't decode WebP); other
        // in-bounds images pass through untouched. Track each page's final pixel
        // size — the fixed-layout viewport is set from it.
        var pages = new List<(string Name, byte[] Bytes, int W, int H)>();
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
                pages.Add(($"page-{idx:D4}{ext}", bytes, w, h));
            }
        }

        var tmp = outPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // mimetype MUST be first and stored uncompressed.
            var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var s = mt.Open()) s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));

            void Write(string name, string content)
            { using var s = zip.CreateEntry(name).Open(); var b = Encoding.UTF8.GetBytes(content); s.Write(b); }

            Write("META-INF/container.xml",
                "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

            // page images + xhtml
            for (var i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                using (var s = zip.CreateEntry($"OEBPS/img/{p.Name}").Open()) s.Write(p.Bytes);
                // Viewport is the page's CSS size (image pixels ÷ dpr) so the
                // reader lays it out to fill the screen; the image stays physical.
                var vw = Math.Max(1, (int)Math.Round(p.W / dpr));
                var vh = Math.Max(1, (int)Math.Round(p.H / dpr));
                Write($"OEBPS/page-{i + 1:D4}.xhtml", PageXhtml(p.Name, vw, vh, i + 1));
            }
            Write("OEBPS/content.opf", Opf(meta, pages));
            Write("OEBPS/nav.xhtml", Nav(pages.Count));
            Write("OEBPS/toc.ncx", Ncx(meta, pages.Count));
        }
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    // Fixed-layout page: the viewport is the page's pixel size and the image
    // fills it. Full-bleed (no reader margins). Because pages are pre-sized to
    // the device resolution, the reader shows them edge-to-edge without clipping.
    private static string PageXhtml(string img, int w, int h, int page) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta charset=\"utf-8\"/><title>Page {page}</title><meta name=\"viewport\" content=\"width={w}, height={h}\"/><style>html,body{{margin:0;padding:0}}img{{width:100%;height:100%}}</style></head><body><img src=\"img/{img}\" alt=\"\"/></body></html>";

    private static string Nav(int n)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>nav</title></head><body><nav epub:type=\"toc\"><ol>");
        for (var i = 1; i <= n; i++) sb.Append($"<li><a href=\"page-{i:D4}.xhtml\">Page {i}</a></li>");
        sb.Append("</ol></nav></body></html>");
        return sb.ToString();
    }

    private static string Opf(EbookMeta m, List<(string Name, byte[] Bytes, int W, int H)> pages)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" prefix=\"rendition: http://www.idpf.org/vocab/rendition/#\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        sb.Append($"<dc:identifier id=\"bookid\">{Esc(Uid(m))}</dc:identifier><dc:title>{Esc(m.Title)}</dc:title><dc:language>en</dc:language><dc:creator>{Esc(m.Author)}</dc:creator>");
        // dcterms:modified is required by EPUB3; without it readers flag the book.
        sb.Append($"<meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>");
        // Fixed-layout, single page per screen.
        sb.Append("<meta property=\"rendition:layout\">pre-paginated</meta><meta property=\"rendition:spread\">none</meta>");
        if (!string.IsNullOrEmpty(m.Series)) sb.Append($"<meta name=\"calibre:series\" content=\"{Esc(m.Series)}\"/>");
        if (!string.IsNullOrEmpty(m.Sequence)) sb.Append($"<meta name=\"calibre:series_index\" content=\"{Esc(m.Sequence)}\"/>");
        // NCX (EPUB2) alongside the EPUB3 nav for older readers (e.g. Tolino).
        sb.Append("</metadata><manifest><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        for (var i = 0; i < pages.Count; i++)
        {
            var mime = Path.GetExtension(pages[i].Name).ToLowerInvariant() == ".png" ? "image/png"
                : Path.GetExtension(pages[i].Name).ToLowerInvariant() == ".gif" ? "image/gif" : "image/jpeg";
            sb.Append($"<item id=\"img{i + 1}\" href=\"img/{pages[i].Name}\" media-type=\"{mime}\"/>");
            sb.Append($"<item id=\"pg{i + 1}\" href=\"page-{i + 1:D4}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        }
        sb.Append("</manifest><spine toc=\"ncx\">");
        for (var i = 0; i < pages.Count; i++) sb.Append($"<itemref idref=\"pg{i + 1}\" properties=\"rendition:layout-pre-paginated\"/>");
        sb.Append("</spine></package>");
        return sb.ToString();
    }

    // Stable per-book identifier (item id when available) so each book is unique.
    private static string Uid(EbookMeta m) => "urn:inkshelf:" + (string.IsNullOrEmpty(m.Identifier) ? "unknown" : m.Identifier);

    // EPUB2 NCX navigation, mirroring nav.xhtml, for readers that require it.
    private static string Ncx(EbookMeta m, int n)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head>");
        sb.Append($"<meta name=\"dtb:uid\" content=\"{Esc(Uid(m))}\"/><meta name=\"dtb:depth\" content=\"1\"/><meta name=\"dtb:totalPageCount\" content=\"0\"/><meta name=\"dtb:maxPageNumber\" content=\"0\"/></head>");
        sb.Append($"<docTitle><text>{Esc(m.Title)}</text></docTitle><navMap>");
        for (var i = 1; i <= n; i++)
            sb.Append($"<navPoint id=\"np{i}\" playOrder=\"{i}\"><navLabel><text>Page {i}</text></navLabel><content src=\"page-{i:D4}.xhtml\"/></navPoint>");
        sb.Append("</navMap></ncx>");
        return sb.ToString();
    }
}
