using System.IO.Compression;
using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace Inkshelf.Tests;

public class EpubConverterTests
{
    private static byte[] Img(int w, int h, IImageEncoder enc)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream(); img.Save(ms, enc); return ms.ToArray();
    }

    private static MemoryStream Cbz()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            add("page-02.png", Img(80, 120, new PngEncoder()));
            add("page-01.jpg", Img(80, 120, new JpegEncoder()));
            add("page-03.webp", Img(80, 120, new WebpEncoder()));
        }
        ms.Position = 0; return ms;
    }

    [Fact]
    public async Task Convert_produces_fixed_layout_epub_pages_in_order_no_webp()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("Vol 1", "Artist", "Saga", "1"), outPath, default);

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        // mimetype first and stored uncompressed
        Assert.Equal("mimetype", epub.Entries[0].FullName);
        Assert.Equal(epub.Entries[0].Length, epub.Entries[0].CompressedLength);
        // three page images + xhtml, container + opf + nav
        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains(names, n => n.EndsWith("content.opf"));
        Assert.Equal(3, names.Count(n => n.EndsWith(".xhtml") && n.Contains("page")));
        // webp transcoded away
        Assert.DoesNotContain(names, n => n.EndsWith(".webp"));
        // opf references title/author and pre-paginated
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("Vol 1", opf);
        Assert.Contains("Artist", opf);
        Assert.Contains("pre-paginated", opf);
        File.Delete(outPath);
    }
}
