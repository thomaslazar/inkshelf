using System.IO.Compression;
using Inkshelf.Convert;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace Inkshelf.Tests;

public class ComicArchiveReaderTests
{
    private static byte[] Img(int w, int h, SixLabors.ImageSharp.Formats.IImageEncoder enc)
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
            add("page-02.png", Img(10, 10, new PngEncoder()));
            add("page-01.jpg", Img(10, 10, new JpegEncoder()));
            add("readme.txt", new byte[] { 1, 2, 3 });          // non-image, must be skipped
        }
        ms.Position = 0; return ms;
    }

    [Fact]
    public async Task ReadAsync_yields_images_in_ordinal_order_skipping_non_images()
    {
        var keys = new List<string>();
        await foreach (var p in ComicArchiveReader.ReadAsync(Cbz(), default))
            keys.Add(p.Key);

        Assert.Equal(new[] { "page-01.jpg", "page-02.png" }, keys); // ordinal order, txt skipped
    }

    [Fact]
    public async Task ReadAsync_returns_entry_bytes()
    {
        await foreach (var p in ComicArchiveReader.ReadAsync(Cbz(), default))
        {
            Assert.NotEmpty(p.Bytes);
            break;
        }
    }
}
