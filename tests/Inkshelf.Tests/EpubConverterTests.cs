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
        // No cap (0×0), dpr 1 → 80×120 fixtures pass through, viewport = image.
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("Vol 1", "Artist", "Saga", "1"), outPath, new RenderTarget(0, 0, 1, false), default);

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        // mimetype first and stored uncompressed
        Assert.Equal("mimetype", epub.Entries[0].FullName);
        Assert.Equal(epub.Entries[0].Length, epub.Entries[0].CompressedLength);
        // three page images + xhtml, container + opf + nav + ncx
        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains(names, n => n.EndsWith("content.opf"));
        Assert.Contains(names, n => n.EndsWith("toc.ncx"));
        Assert.Equal(3, names.Count(n => n.EndsWith(".xhtml") && n.Contains("page")));
        // webp transcoded away
        Assert.DoesNotContain(names, n => n.EndsWith(".webp"));
        // opf references title/author, is fixed-layout, and is EPUB3-valid.
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("Vol 1", opf);
        Assert.Contains("Artist", opf);
        Assert.Contains("pre-paginated", opf);
        Assert.Contains("dcterms:modified", opf);  // required by EPUB3
        Assert.Contains("toc=\"ncx\"", opf);        // EPUB2 nav for older readers

        // Pages set the viewport to the page size and fill it with the image.
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("<img", page);
        Assert.Contains("width=80, height=120", page); // viewport = 80×120 fixture
        Assert.DoesNotContain("<svg", page);
        File.Delete(outPath);
    }

    [Fact]
    public async Task Convert_downscales_pages_larger_than_cap()
    {
        // One 400×600 page, capped at 200×200 → scale 0.333 → 133×200 (aspect kept).
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("p1.jpg").Open())
            s.Write(Img(400, 600, new JpegEncoder()));
        ms.Position = 0;

        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(ms, new EbookMeta("T", "A", null, null), outPath, new RenderTarget(200, 200, 1, false), default);

        using var epub = ZipFile.OpenRead(outPath);
        var imgEntry = epub.Entries.First(e => e.FullName.StartsWith("OEBPS/img/"));
        using var imgStream = imgEntry.Open();
        var info = Image.Identify(imgStream);
        Assert.True(info.Width <= 200 && info.Height <= 200, $"expected ≤200×200, got {info.Width}×{info.Height}");
        File.Delete(outPath);
    }

    [Fact]
    public async Task Convert_sets_viewport_to_css_size_image_stays_physical()
    {
        // 400×600 page at dpr 2, no cap: image kept at 400×600, viewport halved
        // to 200×300 (the CSS size) so it fills the screen crisply.
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("p1.jpg").Open())
            s.Write(Img(400, 600, new JpegEncoder()));
        ms.Position = 0;

        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(ms, new EbookMeta("T", "A", null, null), outPath, new RenderTarget(0, 0, 2, false), default);

        using var epub = ZipFile.OpenRead(outPath);
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=200, height=300", page); // viewport = image ÷ dpr
        var info = Image.Identify(epub.Entries.First(e => e.FullName.StartsWith("OEBPS/img/")).Open());
        Assert.Equal(400, info.Width); // image itself stays physical
        File.Delete(outPath);
    }

    [Fact]
    public async Task Convert_embeds_supplied_cover_transcoding_webp_to_jpg()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("T", "A", null, null),
            outPath, new RenderTarget(0, 0, 1, false), default,
            (Img(300, 450, new WebpEncoder()), ".webp"));

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("OEBPS/cover.jpg", names);              // webp transcoded to jpg
        Assert.DoesNotContain(names, n => n == "OEBPS/cover.webp");
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("id=\"cover-img\"", opf);
        Assert.Contains("<meta name=\"cover\" content=\"cover-img\"/>", opf);
        File.Delete(outPath);
    }

    [Fact]
    public async Task Convert_with_undecodable_cover_falls_back_to_first_page()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await new EpubConverter().ConvertAsync(Cbz(), new EbookMeta("T", "A", null, null),
            outPath, new RenderTarget(0, 0, 1, false), default,
            (new byte[] { 1, 2, 3, 4 }, ".jpg"));               // not a real image

        using var epub = ZipFile.OpenRead(outPath);
        Assert.DoesNotContain(epub.Entries.Select(e => e.FullName), n => n.StartsWith("OEBPS/cover"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("<meta name=\"cover\" content=\"img1\"/>", opf);
        File.Delete(outPath);
    }
}
