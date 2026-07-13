using System.IO.Compression;
using System.Text;
using SharpCompress.Archives;
using SixLabors.ImageSharp;

namespace Inkshelf.Convert;

public record EbookMeta(string Title, string Author, string? Series, string? Sequence);

public class EpubConverter
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    public async Task ConvertAsync(Stream archive, EbookMeta meta, string outPath, CancellationToken ct)
    {
        // Collect + normalise pages (transcode webp -> jpeg) in archive order.
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
                int w, h;
                if (ext == ".webp")
                {
                    using var img = Image.Load(bytes);
                    w = img.Width; h = img.Height;
                    using var outMs = new MemoryStream();
                    await img.SaveAsJpegAsync(outMs, ct);
                    bytes = outMs.ToArray(); ext = ".jpg";
                }
                else
                {
                    var info = Image.Identify(bytes);
                    w = info.Width; h = info.Height;
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
                Write($"OEBPS/page-{i + 1:D4}.xhtml", PageXhtml(p.Name, p.W, p.H));
            }
            Write("OEBPS/content.opf", Opf(meta, pages));
            Write("OEBPS/nav.xhtml", Nav(pages.Count));
        }
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static string PageXhtml(string img, int w, int h) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta name=\"viewport\" content=\"width={w}, height={h}\"/><style>html,body{{margin:0;padding:0}}img{{width:100%;height:100%}}</style></head><body><img src=\"img/{img}\" alt=\"\"/></body></html>";

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
        sb.Append($"<dc:identifier id=\"bookid\">inkshelf</dc:identifier><dc:title>{Esc(m.Title)}</dc:title><dc:language>en</dc:language><dc:creator>{Esc(m.Author)}</dc:creator>");
        sb.Append("<meta property=\"rendition:layout\">pre-paginated</meta>");
        if (!string.IsNullOrEmpty(m.Series)) sb.Append($"<meta name=\"calibre:series\" content=\"{Esc(m.Series)}\"/>");
        if (!string.IsNullOrEmpty(m.Sequence)) sb.Append($"<meta name=\"calibre:series_index\" content=\"{Esc(m.Sequence)}\"/>");
        sb.Append("</metadata><manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        for (var i = 0; i < pages.Count; i++)
        {
            var mime = Path.GetExtension(pages[i].Name).ToLowerInvariant() == ".png" ? "image/png"
                : Path.GetExtension(pages[i].Name).ToLowerInvariant() == ".gif" ? "image/gif" : "image/jpeg";
            sb.Append($"<item id=\"img{i + 1}\" href=\"img/{pages[i].Name}\" media-type=\"{mime}\"/>");
            sb.Append($"<item id=\"pg{i + 1}\" href=\"page-{i + 1:D4}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        }
        sb.Append("</manifest><spine>");
        for (var i = 0; i < pages.Count; i++) sb.Append($"<itemref idref=\"pg{i + 1}\" properties=\"rendition:layout-pre-paginated\"/>");
        sb.Append("</spine></package>");
        return sb.ToString();
    }
}
