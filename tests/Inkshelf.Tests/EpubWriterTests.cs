using System.IO.Compression;
using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Inkshelf.Tests;

public class EpubWriterTests
{
    private static byte[] Jpg(int w, int h)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream(); img.Save(ms, new JpegEncoder()); return ms.ToArray();
    }

    [Fact]
    public void Write_produces_valid_fixed_layout_epub()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var pages = new List<EpubWriter.Page> { new("page-0001.jpg", Jpg(80, 120), 80, 120) };
        EpubWriter.Write(outPath, new EbookMeta("Vol 1", "Artist", "Saga", "1"), pages, dpr: 1);

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        Assert.Equal("mimetype", epub.Entries[0].FullName);
        Assert.Equal(epub.Entries[0].Length, epub.Entries[0].CompressedLength); // stored uncompressed
        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains(names, n => n.EndsWith("content.opf"));
        Assert.Contains(names, n => n.EndsWith("toc.ncx"));
        Assert.Contains(names, n => n.EndsWith("nav.xhtml"));

        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("Vol 1", opf);
        Assert.Contains("Artist", opf);
        Assert.Contains("pre-paginated", opf);
        Assert.Contains("dcterms:modified", opf);
        Assert.Contains("toc=\"ncx\"", opf);

        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=80, height=120", page);
        File.Delete(outPath);
    }

    [Fact]
    public void Write_sets_viewport_to_css_size_via_dpr()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var pages = new List<EpubWriter.Page> { new("page-0001.jpg", Jpg(400, 600), 400, 600) };
        EpubWriter.Write(outPath, new EbookMeta("T", "A", null, null), pages, dpr: 2);

        using var epub = ZipFile.OpenRead(outPath);
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=200, height=300", page); // 400/2 × 600/2
        File.Delete(outPath);
    }
}
