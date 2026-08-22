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

    // A book whose pages are NOT all the same size: two portrait pages and a landscape
    // spread, which is the shape that shipped the bug.
    private static MemoryStream MixedCbz()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            add("p-01.jpg", Img(600, 900, new JpegEncoder()));
            add("p-02.jpg", Img(1200, 900, new JpegEncoder()));   // spread
            add("p-03.jpg", Img(500, 900, new JpegEncoder()));    // a narrower page
        }
        ms.Position = 0; return ms;
    }

    private static List<(int W, int H)> Viewports(string epubPath)
    {
        using var epub = ZipFile.OpenRead(epubPath);
        var vps = new List<(int, int)>();
        foreach (var e in epub.Entries.Where(e => e.FullName.StartsWith("OEBPS/page-")).OrderBy(e => e.FullName))
        {
            using var r = new StreamReader(e.Open());
            var m = System.Text.RegularExpressions.Regex.Match(r.ReadToEnd(), @"width=(\d+), height=(\d+)");
            vps.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        return vps;
    }

    [Theory]
    [InlineData(SpreadMode.Fit)]
    [InlineData(SpreadMode.SplitLeftFirst)]
    [InlineData(SpreadMode.SplitRightFirst)]
    [InlineData(SpreadMode.RotateLeft)]
    [InlineData(SpreadMode.RotateRight)]
    public async Task Every_page_in_a_book_declares_the_same_viewport(SpreadMode mode)
    {
        // Verified on device: the reader lays every page of a book out in ONE box and
        // clips anything bigger, so a book with mixed page sizes loses the right edge of
        // its odd-sized pages. Mixed sizes in, one uniform size out.
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var target = new RenderTarget(300, 450, 1, false) { Spread = mode };
        await new EpubConverter().ConvertAsync(MixedCbz(), new EbookMeta("T", "A", null, null), outPath, target, default);

        var vps = Viewports(outPath);
        Assert.True(vps.Count >= 3, $"expected at least 3 pages, got {vps.Count}");
        Assert.Single(vps.Distinct());
        File.Delete(outPath);
    }

    [Fact]
    public async Task Scale_shrinks_the_declared_viewport_but_not_the_image()
    {
        // The manual fix for a reader that cuts a strip off the page. The IMAGE must keep
        // its pixels — only the CSS box shrinks — or the knob would cost sharpness.
        var full = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var small = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var target = new RenderTarget(600, 900, 1, false) { Spread = SpreadMode.Fit };
        await new EpubConverter().ConvertAsync(MixedCbz(), new EbookMeta("T", "A", null, null), full, target, default);
        await new EpubConverter().ConvertAsync(MixedCbz(), new EbookMeta("T", "A", null, null), small,
            target with { Scale = 90 }, default);

        Assert.Equal((600, 900), Viewports(full)[0]);
        Assert.Equal((540, 810), Viewports(small)[0]);   // 90% of the CSS box

        using (var a = ZipFile.OpenRead(full))
        using (var b = ZipFile.OpenRead(small))
        {
            var ia = Image.Identify(a.Entries.First(e => e.FullName.StartsWith("OEBPS/img/")).Open());
            var ib = Image.Identify(b.Entries.First(e => e.FullName.StartsWith("OEBPS/img/")).Open());
            Assert.Equal((ia.Width, ia.Height), (ib.Width, ib.Height));   // same pixels
        }
        // The scale lives in the DECLARED viewport only; the CSS stays relative.
        using (var b = ZipFile.OpenRead(small))
        {
            var xhtml = await new StreamReader(
                b.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEndAsync();
            Assert.Contains("width=540, height=810", xhtml);
            Assert.DoesNotContain("540px", xhtml);
            Assert.DoesNotContain("810px", xhtml);
        }
        File.Delete(full); File.Delete(small);
    }

    private static MemoryStream SmallScansCbz()
    {
        // 1125x1600 scans — the real proportions of a book that rendered small on
        // device, and smaller than the cap used below.
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 1; i <= 3; i++)
            {
                using var s = zip.CreateEntry($"p-{i:D2}.jpg").Open();
                s.Write(Img(1125, 1600, new JpegEncoder()));
            }
        ms.Position = 0; return ms;
    }

    [Fact]
    public async Task Scans_smaller_than_the_screen_still_declare_a_full_size_page()
    {
        // The reader lays a page out at its declared CSS size and never scales it UP,
        // so a viewport of image px ÷ dpr drew a low-resolution book small with dead
        // margin around it — 1125x1600 scans on a 1442x1787 screen came out at 78% of
        // the width. The viewport is scaled to the cap instead, while the IMAGE keeps
        // its own pixels: no extra bytes, and the reader does the upscaling.
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var target = new RenderTarget(1442, 1787, 1.875, false) { Spread = SpreadMode.Fit };
        await new EpubConverter().ConvertAsync(SmallScansCbz(), new EbookMeta("T", "A", null, null),
            outPath, target, default);

        // fit = min(1442/1125, 1787/1600) = 1.1169 → 1125*1.1169/1.875 = 670, and the
        // height lands exactly on the screen: 1600*1.1169/1.875 = 953.
        Assert.All(Viewports(outPath), v => Assert.Equal((670, 953), v));

        using (var epub = ZipFile.OpenRead(outPath))
        {
            var info = Image.Identify(epub.Entries.First(e => e.FullName.StartsWith("OEBPS/img/")).Open());
            Assert.Equal((1125, 1600), (info.Width, info.Height));   // NOT upscaled
        }
        File.Delete(outPath);
    }

    [Fact]
    public async Task A_page_at_the_cap_is_unaffected_by_the_upscale_rule()
    {
        // The regression risk of the rule above: it must be a no-op for a book whose
        // scans already meet or exceed the cap, or every existing conversion changes.
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var target = new RenderTarget(1442, 1787, 1.875, false) { Spread = SpreadMode.Fit };
        await new EpubConverter().ConvertAsync(MixedCbz(), new EbookMeta("T", "A", null, null),
            outPath, target, default);

        // First page 600x900 fits inside the cap unscaled, so the box is 600x900 and
        // fit = min(1442/600, 1787/900) = 1.9856 → 600*1.9856/1.875 = 635, and the
        // height lands on the screen exactly: 900*1.9856/1.875 = 953.
        Assert.All(Viewports(outPath), v => Assert.Equal((635, 953), v));
        File.Delete(outPath);
    }
}
