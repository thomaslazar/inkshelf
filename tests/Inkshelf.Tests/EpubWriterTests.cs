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

    private static async IAsyncEnumerable<EpubWriter.Page> Stream(params EpubWriter.Page[] pages)
    {
        foreach (var p in pages) { yield return p; }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAsync_produces_valid_fixed_layout_epub()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("Vol 1", "Artist", "Saga", "1"),
            Stream(new EpubWriter.Page("page-0001.jpg", Jpg(80, 120), 80, 120)), dpr: 1, default);

        using var epub = ZipFile.OpenRead(outPath);
        var names = epub.Entries.Select(e => e.FullName).ToList();
        Assert.Equal("mimetype", epub.Entries[0].FullName);
        Assert.Equal(epub.Entries[0].Length, epub.Entries[0].CompressedLength);
        Assert.Contains("META-INF/container.xml", names);
        Assert.Contains(names, n => n.EndsWith("content.opf"));
        Assert.Contains(names, n => n.EndsWith("toc.ncx"));
        Assert.Contains(names, n => n.EndsWith("nav.xhtml"));
        var opf = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("content.opf")).Open()).ReadToEnd();
        Assert.Contains("Vol 1", opf); Assert.Contains("Artist", opf);
        Assert.Contains("pre-paginated", opf); Assert.Contains("dcterms:modified", opf);
        Assert.Contains("toc=\"ncx\"", opf);
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=80, height=120", page);
        File.Delete(outPath);
    }

    [Fact]
    public async Task WriteAsync_sets_viewport_to_css_size_via_dpr()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null),
            Stream(new EpubWriter.Page("page-0001.jpg", Jpg(400, 600), 400, 600)), dpr: 2, default);
        using var epub = ZipFile.OpenRead(outPath);
        var page = new StreamReader(epub.Entries.First(e => e.FullName.EndsWith("page-0001.xhtml")).Open()).ReadToEnd();
        Assert.Contains("width=200, height=300", page);
        File.Delete(outPath);
    }

    // Guards the streaming invariant: the writer must write each page into the
    // tmp zip as it pulls it, NOT buffer all pages first. We record the tmp file
    // size at the moment each page is requested; if the writer streams, the file
    // has already grown with the previous page's (large) image by the next pull.
    [Fact]
    public async Task WriteAsync_writes_incrementally_not_buffered()
    {
        var outPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".epub");
        var tmp = outPath + ".tmp";
        var sizesWhenAsked = new List<long>();
        // Large images so each write exceeds FileStream's internal buffer and grows the file.
        var big = Jpg(1600, 2400);

        async IAsyncEnumerable<EpubWriter.Page> Produce()
        {
            for (var i = 1; i <= 3; i++)
            {
                sizesWhenAsked.Add(File.Exists(tmp) ? new FileInfo(tmp).Length : 0);
                yield return new EpubWriter.Page($"page-{i:D4}.jpg", big, 1600, 2400);
            }
            await Task.CompletedTask;
        }

        await EpubWriter.WriteAsync(outPath, new EbookMeta("T", "A", null, null), Produce(), 1, default);

        // Buffered-then-write would show all three sizes equal (only mimetype+container
        // present at each pull). Streaming shows strict growth as prior pages land.
        Assert.True(sizesWhenAsked[1] > sizesWhenAsked[0], $"expected growth, got {string.Join(",", sizesWhenAsked)}");
        Assert.True(sizesWhenAsked[2] > sizesWhenAsked[1], $"expected growth, got {string.Join(",", sizesWhenAsked)}");
        File.Delete(outPath);
    }
}
